using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FyersLoginWeb.Services.Broker;
using FyersLoginWeb.Strategy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FyersLoginWeb.Services
{
    /// <summary>
    /// Market hours loop (har ~15s):
    ///  - Exit management (paper marks + 2:45 square-off)
    ///  - Signal scan (history 3m CLOSED candles → close-confirm → paper entry)
    ///
    /// Close-confirm = USI 3m candle ke band hone par decide — agli 3m candle ka wait NAHI.
    /// Delay sirf poll gap (~15s max). Data = History API, tick stream nahi.
    /// </summary>
    public class LiveTradingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LiveTradingService> _log;

        // PAPER only. Real Fyers IBrokerOrders impl plug karo jab live jana ho.
        private const bool PaperMode = true;
        private readonly PaperBrokerOrders _broker;
        private readonly PositionManager _mgr;

        private const decimal Capital = 100000m;
        private const decimal DeployPct = 0.25m;

        private record Leg(string Index, string OptRoot, decimal RR, decimal Step, int Priority);
        // Recommended: Nifty + Bank only (Sensex OFF)
        private static readonly Leg[] Legs =
        {
            new("NSE:NIFTY50-INDEX",   "NSE:NIFTY",     RecommendedLiveConfig.NiftyRr, RecommendedLiveConfig.StrikeStepNifty, 0),
            new("NSE:NIFTYBANK-INDEX", "NSE:BANKNIFTY", RecommendedLiveConfig.BankRr,  RecommendedLiveConfig.StrikeStepBank,  1),
        };

        // last closed-bar end we already scanned — avoid re-logging; still re-run for late API
        private DateTime _lastClosedBarEnd = DateTime.MinValue;

        public LiveTradingService(IServiceScopeFactory scopeFactory, ILogger<LiveTradingService> log,
            PaperBrokerOrders broker, PositionManager mgr)
        {
            _scopeFactory = scopeFactory;
            _log = log;
            _broker = broker;
            _mgr = mgr;
        }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            _log.LogInformation(
                "LiveTradingService started (PaperMode={Paper}). Config: refs={Refs} RR=1:2 strict " +
                "closeConfirm=ON (same-bar, no +3m wait) poll=15s Sensex=OFF maxSL={MaxSl}/day",
                PaperMode,
                string.Join(",", RecommendedLiveConfig.Refs.Select(r => r.ToString(@"hh\:mm"))),
                RecommendedLiveConfig.MaxSlPerDay);

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    if (IsMarketHours(now))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var auth = scope.ServiceProvider.GetRequiredService<FyersAuthService>();
                        var hist = scope.ServiceProvider.GetRequiredService<FyersHistoryService>();
                        var db = scope.ServiceProvider.GetRequiredService<DbService>();
                        var token = auth.GetValidToken();

                        if (token == null)
                        {
                            _log.LogWarning("Live: valid token nahi (re-login /Auth).");
                        }
                        else
                        {
                            await ManageExits(hist, token.AccessToken, now);
                            // Har 15s scan — candle close ke turant baad miss kam
                            await ScanSignals(hist, db, token.AccessToken, now);
                        }
                    }
                }
                catch (Exception ex) { _log.LogWarning("Live loop error: " + ex.Message); }

                await Task.Delay(TimeSpan.FromSeconds(15), stop);
            }
        }

        private static bool IsMarketHours(DateTime now)
        {
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var t = now.TimeOfDay;
            return t >= new TimeSpan(9, 15, 0) && t <= new TimeSpan(15, 35, 0);
        }

        private async Task ManageExits(FyersHistoryService hist, string token, DateTime now)
        {
            var day = now.Date;
            foreach (var mp in _mgr.Positions.Where(p => p.State != "CLOSED"))
            {
                if (PaperMode)
                {
                    var oc = RecommendedLiveConfig.ClosedOnly(
                        await hist.GetCandlesAsync(mp.Symbol, "3", day, token), now);
                    var last = oc.LastOrDefault();
                    if (last != null)
                        _broker.MarkPrice(mp.Symbol, last.High, last.Low, last.Close, now);
                }
            }
            await _mgr.Tick(now);
        }

        private async Task ScanSignals(FyersHistoryService hist, DbService db, string token, DateTime now)
        {
            var day = now.Date;
            string yy = day.ToString("yy");
            string mmm = day.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpper();

            var dayTrades = new List<(string sym, string cepe, string rw, string dkey, TradeSignal s, int prio)>();

            foreach (var leg in Legs)
            {
                var idx = RecommendedLiveConfig.ClosedOnly(
                    await hist.GetCandlesAsync(leg.Index, "3", day, token), now);
                if (idx.Count == 0) continue;

                var config = RecommendedLiveConfig.MakeConfig(leg.RR);

                foreach (var rs in RecommendedLiveConfig.Refs)
                {
                    var monStart = rs.Add(TimeSpan.FromMinutes(30));
                    if (now.TimeOfDay < monStart) continue;

                    var spotCandle = idx.FirstOrDefault(c => c.StartTime.TimeOfDay == monStart);
                    if (spotCandle == null) continue;
                    long strike = (long)(Math.Round(spotCandle.Open / leg.Step) * leg.Step);

                    foreach (var cepe in new[] { "CE", "PE" })
                    {
                        string sym = $"{leg.OptRoot}{yy}{mmm}{strike}{cepe}";
                        var opt = RecommendedLiveConfig.ClosedOnly(
                            await hist.GetCandlesAsync(sym, "3", day, token), now);
                        if (opt.Count == 0) continue;

                        var res = StrategyBacktester.Run(opt, config, rs);
                        string rw = $"{rs:hh\\:mm}";
                        string dkey = leg.OptRoot + "|" + cepe;
                        foreach (var s in res.BuySignals)
                            dayTrades.Add((sym, cepe, rw, dkey, s, leg.Priority));
                    }
                }
            }

            var entryFilter = RecommendedLiveConfig.EntryFilters();
            dayTrades = dayTrades.Where(x => entryFilter.Allows(x.s, x.sym)).ToList();

            // overlap dedup (underlying+CE/PE)
            var openUntil = new Dictionary<string, DateTime>();
            var survivors = new List<(string sym, string cepe, string rw, TradeSignal s, int prio)>();
            foreach (var (sym, cepe, rw, dkey, s, prio) in dayTrades.OrderBy(x => x.s.EntryTime))
            {
                if (openUntil.TryGetValue(dkey, out var busyTill) && s.EntryTime <= busyTill) continue;
                openUntil[dkey] = s.OutcomeTime ?? day.AddHours(15).AddMinutes(30);
                survivors.Add((sym, cepe, rw, s, prio));
            }

            var cands = survivors.Select(x => new LiveCandidate(x.s, x.prio)).ToList();
            var decisions = PortfolioSelector.Select(
                cands,
                maxSlPerDay: RecommendedLiveConfig.MaxSlPerDay,
                minGapMinutes: RecommendedLiveConfig.MinGapMinutes);

            for (int i = 0; i < survivors.Count; i++)
            {
                var (sym, cepe, rw, s, _) = survivors[i];
                db.SaveSignal(sym, cepe, rw, s, "live");
                if (decisions[cands[i]].Skipped) continue;

                // Freshness from CANDLE CLOSE (EntryTime=StartTime → EndTime = +3m).
                // Close confirm = usi bar ke end par — agli bar ka +3m wait NAHI.
                var confirmAt = s.EntryTime.AddMinutes(RecommendedLiveConfig.CandleMinutes);
                double ageSinceClose = (now - confirmAt).TotalMinutes;
                if (ageSinceClose >= -0.25 && ageSinceClose <= RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                {
                    int qty = SizeQty(sym, s.EntryPrice);
                    await _mgr.OnSignal(sym, cepe, qty, s.EntryPrice, s.StopLoss, s.Target, s.EntryTime);
                    _log.LogInformation(
                        "LIVE ENTRY {Sym} {Side} @ {Px} SL {Sl} T {Tgt} ref {Ref} sinceClose={Age:F1}m qty={Qty}",
                        sym, cepe, s.EntryPrice, s.StopLoss, s.Target, rw, ageSinceClose, qty);
                }
                else if (ageSinceClose > RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                {
                    _log.LogInformation(
                        "LIVE take {Sym} {Entry:HH:mm} STALE (close+{Age:F0}m) — record only, entry NAHI",
                        sym, s.EntryTime, ageSinceClose);
                }
            }

            var newestBarEnd = dayTrades
                .Select(x => x.s.EntryTime.AddMinutes(RecommendedLiveConfig.CandleMinutes))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (newestBarEnd > _lastClosedBarEnd)
                _lastClosedBarEnd = newestBarEnd;
        }

        private static int SizeQty(string sym, decimal premium)
        {
            int lot = LotSizeOf(sym);
            if (premium <= 0) return lot;
            int lots = (int)Math.Floor((Capital * DeployPct) / (premium * lot));
            return Math.Max(1, lots) * lot;
        }

        private static int LotSizeOf(string sym)
        {
            var u = sym.ToUpperInvariant();
            if (u.Contains("BANKNIFTY")) return 35;
            if (u.Contains("NIFTY")) return 75;
            return 50;
        }

        private sealed class LiveCandidate : IPortfolioTrade
        {
            private readonly TradeSignal _s;
            public LiveCandidate(TradeSignal s, int prio) { _s = s; Priority = prio; }
            public DateTime EntryTime => _s.EntryTime;
            public DateTime ExitTime => _s.OutcomeTime ?? _s.EntryTime.Date.AddHours(15).AddMinutes(30);
            public int Priority { get; }
            public bool IsStopLoss => _s.Outcome == TradeOutcome.StopLossHit;
        }
    }
}
