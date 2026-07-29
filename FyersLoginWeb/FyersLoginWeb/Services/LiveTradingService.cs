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
    /// Default = candle CLOSE confirm (RecommendedLiveConfig) — tick/wick path OFF.
    /// UseLiveEntry=true -> FyersLiveFeed WatchLevel pe real-time LTP cross entry
    /// (unke FyersLoginWeb repo me ye path −21R aaya tha, isliye default false).
    /// </summary>
    public class LiveTradingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LiveTradingService> _log;

        private const bool PaperMode = true;
        // Tick entry: default OFF (candle-close safer). true = FyersLiveFeed WatchLevel.
        private const bool UseLiveEntry = false;

        private readonly PaperBrokerOrders _broker;
        private readonly PositionManager _mgr;
        private readonly FyersLiveFeed _feed;
        private bool _feedStarted;
        private readonly HashSet<string> _liveEntered = new();
        private DateTime _liveEnteredDay = DateTime.MinValue;

        private const decimal Capital = 100000m;
        private const decimal DeployPct = 0.25m;

        private record Leg(string Index, string OptRoot, decimal RR, decimal Step, int Priority);
        private static readonly Leg[] Legs =
        {
            new("NSE:NIFTY50-INDEX",   "NSE:NIFTY",     RecommendedLiveConfig.NiftyRr, RecommendedLiveConfig.StrikeStepNifty, 0),
            new("NSE:NIFTYBANK-INDEX", "NSE:BANKNIFTY", RecommendedLiveConfig.BankRr,  RecommendedLiveConfig.StrikeStepBank,  1),
        };

        public LiveTradingService(IServiceScopeFactory scopeFactory, ILogger<LiveTradingService> log,
            PaperBrokerOrders broker, PositionManager mgr, FyersLiveFeed feed)
        {
            _scopeFactory = scopeFactory;
            _log = log;
            _broker = broker;
            _mgr = mgr;
            _feed = feed;
        }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            _log.LogInformation(
                "LiveTradingService started (Paper={Paper} UseLiveEntry={LiveTick}). refs={Refs} Sensex=OFF default=candle-close",
                PaperMode, UseLiveEntry,
                string.Join(",", RecommendedLiveConfig.Refs.Select(r => r.ToString(@"hh\:mm"))));

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
                            _log.LogWarning("Live: valid token nahi (re-login /Auth).");
                        else
                        {
                            if (UseLiveEntry && !_feedStarted)
                            {
                                var opt = scope.ServiceProvider
                                    .GetService<Microsoft.Extensions.Options.IOptions<Models.FyersSettings>>();
                                if (opt != null)
                                {
                                    _ = _feed.StartAsync(opt.Value.ClientId, token.AccessToken, new List<string>());
                                    _feedStarted = true;
                                    _log.LogInformation("LIVE-ENTRY tick feed started (HSM LITE).");
                                }
                            }

                            await ManageExits(hist, token.AccessToken, now);
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
            var highs = new Dictionary<string, decimal>();
            foreach (var mp in _mgr.Positions.Where(p => p.State != "CLOSED"))
            {
                if (PaperMode)
                {
                    var oc = RecommendedLiveConfig.ClosedOnly(
                        await hist.GetCandlesAsync(mp.Symbol, "3", day, token), now);
                    var last = oc.LastOrDefault();
                    if (last != null)
                    {
                        _broker.MarkPrice(mp.Symbol, last.High, last.Low, last.Close, now);
                        highs[mp.Symbol] = last.High;
                    }
                }
            }
            await _mgr.Tick(now);
            foreach (var kv in highs) await _mgr.CheckTrail(kv.Key, kv.Value, now);
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

                        if (UseLiveEntry) TryArmLive(sym, cepe, opt, config, rs, now);
                    }
                }
            }

            var entryFilter = RecommendedLiveConfig.EntryFilters();
            dayTrades = dayTrades.Where(x => entryFilter.Allows(x.s, x.sym)).ToList();

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
                if (UseLiveEntry) continue; // entry tick path se

                var confirmAt = s.EntryTime.AddMinutes(RecommendedLiveConfig.CandleMinutes);
                double ageSinceClose = (now - confirmAt).TotalMinutes;
                if (ageSinceClose >= -0.25 && ageSinceClose <= RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                {
                    int qty = SizeQty(sym, s.EntryPrice);
                    await _mgr.OnSignal(sym, cepe, qty, s.EntryPrice, s.StopLoss, s.Target, s.EntryTime);
                    _log.LogInformation(
                        "LIVE ENTRY (close) {Sym} {Side} @ {Px} sinceClose={Age:F1}m qty={Qty}",
                        sym, cepe, s.EntryPrice, ageSinceClose, qty);
                }
                else if (ageSinceClose > RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                {
                    _log.LogInformation(
                        "LIVE take {Sym} {Entry:HH:mm} STALE (close+{Age:F0}m) — record only",
                        sym, s.EntryTime, ageSinceClose);
                }
            }
        }

        private void TryArmLive(string sym, string cepe, List<Candle> opt, StrategyConfig config,
            TimeSpan rs, DateTime now)
        {
            if (_liveEnteredDay != now.Date) { _liveEntered.Clear(); _liveEnteredDay = now.Date; }

            if (now.TimeOfDay >= config.SquareOffTime || _liveEntered.Contains(sym))
            { _feed.ClearWatch(sym); return; }

            var lm = StrategyBacktester.RunLongManager(opt, config, rs);
            var st = lm.State;
            bool arm = st == StrategyState.WaitingForSecondBreakout || st == StrategyState.WaitingForEntry;
            if (!arm) { _feed.ClearWatch(sym); return; }

            var refc = lm.CurrentReference;
            if (refc != null && config.UseReferenceMaxWait &&
                now > refc.EndTime.AddMinutes(config.MaxReferenceWaitMinutes))
            { _feed.ClearWatch(sym); return; }

            decimal level = lm.EntryCapLevel;
            bool above = st == StrategyState.WaitingForSecondBreakout;
            decimal retr = lm.RetracementLevel;
            decimal rr = config.RiskRewardRatio;

            _feed.WatchLevel(sym, level, above, (s, ltp) =>
            {
                if (_liveEntered.Contains(s)) return;
                if (!LivePortfolioAllows(DateTime.Now))
                {
                    _log.LogInformation("LIVE-ENTRY {Sym} SKIP: portfolio gap/maxSL", s);
                    return;
                }
                _liveEntered.Add(s);
                decimal sl = retr - config.StopLossBufferPoints;
                decimal risk = ltp - sl;
                if (risk <= 0)
                {
                    _log.LogWarning("LIVE-ENTRY {Sym}: risk<=0 — skip", s);
                    return;
                }
                decimal target = ltp + risk * rr;
                int qty = SizeQty(s, ltp);
                _log.LogInformation(
                    "LIVE-ENTRY realtime {Sym} @ {Ltp} SL {Sl} T {Tgt} (level {Level}, {State})",
                    s, ltp, sl, target, level, st);
                _ = _mgr.OnSignal(s, cepe, qty, ltp, sl, target, now);
            });
        }

        private bool LivePortfolioAllows(DateTime now)
        {
            var today = _mgr.Positions.Where(p => p.EntryTime.Date == now.Date).ToList();
            if (today.Count(p => p.Outcome is "StopLossHit" or "TrailStopHit") >= RecommendedLiveConfig.MaxSlPerDay)
                return false;
            if (today.Count > 0 &&
                (now - today.Max(p => p.EntryTime)).TotalMinutes < RecommendedLiveConfig.MinGapMinutes)
                return false;
            return true;
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
