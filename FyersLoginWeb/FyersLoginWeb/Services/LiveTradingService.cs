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
    // Market hours me: exit-management har 15s (2:45 square-off + sibling-cancel timely),
    // signal-scan har 60s (option chart -> strategy -> taken signals -> PAPER order execution).
    public class LiveTradingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LiveTradingService> _log;

        // ---- PAPER trading: koi REAL order nahi. Real ke liye IBrokerOrders ka Fyers impl plug karo. ----
        private const bool PaperMode = true;
        private readonly PaperBrokerOrders _broker;
        private readonly PositionManager _mgr;

        // position sizing (25% capital deploy) — lot sizes APPROXIMATE, exchange ke hisaab se update karein
        private const decimal Capital = 100000m;
        private const decimal DeployPct = 0.25m;

        private static readonly TimeSpan[] Refs = { new(10, 45, 0), new(11, 15, 0), new(12, 45, 0) };

        private record Leg(string Index, string OptRoot, decimal RR, decimal Step);
        private static readonly Leg[] Legs =
        {
            new("NSE:NIFTY50-INDEX",  "NSE:NIFTY",     2m, 50m),
            new("BSE:SENSEX-INDEX",   "BSE:SENSEX",    3m, 100m),
            new("NSE:NIFTYBANK-INDEX","NSE:BANKNIFTY", 2m, 100m),
        };

        private DateTime _lastSignalScan = DateTime.MinValue;

        public LiveTradingService(IServiceScopeFactory scopeFactory, ILogger<LiveTradingService> log,
            PaperBrokerOrders broker, PositionManager mgr)
        {
            _scopeFactory = scopeFactory;
            _log = log;
            _broker = broker;   // shared singleton — dashboard bhi isi ko padhta hai
            _mgr = mgr;
        }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            _log.LogInformation($"LiveTradingService started (PaperMode={PaperMode}).");
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

                        if (token == null) { _log.LogWarning("Live: valid token nahi (re-login /Auth)."); }
                        else
                        {
                            // 1) EXIT management pehle — har cycle (timing critical: 2:45 + sibling cancel)
                            await ManageExits(hist, token.AccessToken, now);

                            // 2) SIGNAL scan — ~60s me ek baar (heavy: bahut fetches)
                            if ((now - _lastSignalScan).TotalSeconds >= 60)
                            {
                                await ScanSignals(hist, db, token.AccessToken, now);
                                _lastSignalScan = now;
                            }
                        }
                    }
                }
                catch (Exception ex) { _log.LogWarning("Live loop error: " + ex.Message); }

                await Task.Delay(TimeSpan.FromSeconds(15), stop);   // fast tick for exit timing
            }
        }

        private static bool IsMarketHours(DateTime now)
        {
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday) return false;
            var t = now.TimeOfDay;
            return t >= new TimeSpan(9, 15, 0) && t <= new TimeSpan(15, 35, 0);
        }

        // ---- exit management: active positions ki latest price feed + state reconcile ----
        private async Task ManageExits(FyersHistoryService hist, string token, DateTime now)
        {
            var day = now.Date;
            foreach (var mp in _mgr.Positions.Where(p => p.State != "CLOSED"))
            {
                // PAPER: latest completed candle se fills simulate. REAL me broker khud karta (yeh skip).
                if (PaperMode)
                {
                    var oc = await hist.GetCandlesAsync(mp.Symbol, "3", day, token);
                    var last = oc.LastOrDefault();
                    if (last != null) _broker.MarkPrice(mp.Symbol, last.High, last.Low, last.Close, now);
                }
            }
            await _mgr.Tick(now);   // sibling-cancel + 2:45 square-off yahan hota hai
        }

        // ---- signal scan: option chart pe strategy -> taken -> FRESH ko entry ----
        private async Task ScanSignals(FyersHistoryService hist, DbService db, string token, DateTime now)
        {
            var day = now.Date;
            string yy = day.ToString("yy");
            string mmm = day.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpper();

            var dayTrades = new List<(string sym, string cepe, string rw, string dkey, TradeSignal s)>();

            foreach (var leg in Legs)
            {
                var idx = await hist.GetCandlesAsync(leg.Index, "3", day, token);
                if (idx.Count == 0) continue;

                var config = new StrategyConfig
                { EntryBufferPoints = 0m, StopLossBufferPoints = 0m, RiskRewardRatio = leg.RR, UseTrailing = true };
                config.SetRetracementFromPercentage(50m);

                foreach (var rs in Refs)
                {
                    var monStart = rs.Add(TimeSpan.FromMinutes(30));
                    if (now.TimeOfDay < monStart) continue;

                    var spotCandle = idx.FirstOrDefault(c => c.StartTime.TimeOfDay == monStart);
                    if (spotCandle == null) continue;
                    long strike = (long)(Math.Round(spotCandle.Open / leg.Step) * leg.Step);

                    foreach (var cepe in new[] { "CE", "PE" })
                    {
                        string sym = $"{leg.OptRoot}{yy}{mmm}{strike}{cepe}";
                        var opt = await hist.GetCandlesAsync(sym, "3", day, token);
                        if (opt.Count == 0) continue;

                        var res = StrategyBacktester.Run(opt, config, rs);
                        string rw = $"{rs:hh\\:mm}";
                        string dkey = leg.OptRoot + "|" + cepe;
                        foreach (var s in res.BuySignals)
                            dayTrades.Add((sym, cepe, rw, dkey, s));
                    }
                }
            }

            // Data-driven SL-reduction filters (strict: skip 10:45/Tue/Sensex, lag≥30, min risk)
            var entryFilter = EntryFilterConfig.ReduceStopLossStrictPreset();
            dayTrades = dayTrades.Where(x => entryFilter.Allows(x.s, x.sym)).ToList();

            // overlap dedup (underlying+side)
            var openUntil = new Dictionary<string, DateTime>();
            var survivors = new List<(string sym, string cepe, string rw, TradeSignal s, int prio)>();
            foreach (var (sym, cepe, rw, dkey, s) in dayTrades.OrderBy(x => x.s.EntryTime))
            {
                if (openUntil.TryGetValue(dkey, out var busyTill) && s.EntryTime <= busyTill) continue;
                openUntil[dkey] = s.OutcomeTime ?? day.AddHours(15).AddMinutes(30);
                survivors.Add((sym, cepe, rw, s, PriorityOf(dkey)));
            }

            // portfolio rules (SHARED selector — dashboard jaisa)
            var cands = survivors.Select(x => new LiveCandidate(x.s, x.prio)).ToList();
            // maxSl 2->4: shared 2-SL/din teeno index par bahut sakht tha (backtest 28-Jul: +86.9->+113.3R)
            var decisions = PortfolioSelector.Select(cands, maxSlPerDay: 4);

            for (int i = 0; i < survivors.Count; i++)
            {
                var (sym, cepe, rw, s, _) = survivors[i];
                db.SaveSignal(sym, cepe, rw, s, "live");
                if (decisions[cands[i]].Skipped) continue;

                // ---- TIMING GUARD: sirf FRESH signal pe entry ----
                // Backtester har poll re-run hota hai -> purane signal ka outcome already bana ho sakta.
                // Sirf abhi-abhi bane setup (<= 6 min) par entry; warna sirf record.
                double ageMin = (now - s.EntryTime).TotalMinutes;
                if (ageMin <= 6)
                {
                    int qty = SizeQty(sym, s.EntryPrice);
                    await _mgr.OnSignal(sym, cepe, qty, s.EntryPrice, s.StopLoss, s.Target, s.EntryTime);
                }
                else
                {
                    _log.LogInformation($"LIVE take {sym} {s.EntryTime:HH:mm} STALE ({ageMin:F0}m) — record only, entry NAHI");
                }
            }
        }

        // ---- position sizing: 25% capital / premium -> lots ----
        private static int SizeQty(string sym, decimal premium)
        {
            int lot = LotSizeOf(sym);
            if (premium <= 0) return lot;
            int lots = (int)Math.Floor((Capital * DeployPct) / (premium * lot));
            return Math.Max(1, lots) * lot;
        }

        // APPROXIMATE lot sizes — exchange revise karta rehta hai, verify/update karein
        private static int LotSizeOf(string sym)
        {
            var u = sym.ToUpperInvariant();
            if (u.Contains("BANKNIFTY")) return 35;
            if (u.Contains("SENSEX")) return 20;
            if (u.Contains("NIFTY")) return 75;
            return 50;
        }

        private static int PriorityOf(string rootOrKey)
        {
            var u = rootOrKey.ToUpperInvariant();
            if (u.Contains("BANKNIFTY")) return 2;
            if (u.Contains("SENSEX")) return 1;
            if (u.Contains("NIFTY")) return 0;
            return 9;
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
