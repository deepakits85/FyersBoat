using System;
using System.Collections.Generic;
using System.IO;
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
    /// COMPARE mode: CLOSE + TICK dono ON, lekin RULE same —
    /// 1) Breakout sirf 3m CLOSE confirm
    /// 2) Entry price = reference candle HIGH (cap), LTP pe chase nahi
    /// TICK sirf fill-timing: close confirm / WaitingForEntry ke baad LTP jab ref-high touch kare.
    /// </summary>
    public class LiveTradingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LiveTradingService> _log;

        private const bool PaperMode = true;
        // Kal compare: DONO ON (rules same — close breakout + entry @ ref high)
        private const bool UseCandleEntry = true;
        private const bool UseLiveEntry = true;

        private readonly PaperBrokerOrders _broker;
        private readonly PositionManager _mgr;
        private readonly FyersLiveFeed _feed;
        private bool _feedStarted;

        // symbol -> which mode took the real paper entry today
        private readonly Dictionary<string, string> _enteredByMode = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _enteredDay = DateTime.MinValue;
        private readonly object _entryLock = new();
        private readonly object _logFileLock = new();
        private string? _compareLogPath;

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
            EnsureCompareLog(DateTime.Now);
            CompareLog("BOOT", "-",
                $"Paper={PaperMode} CLOSE={UseCandleEntry} TICK={UseLiveEntry} " +
                $"refs={string.Join(",", RecommendedLiveConfig.Refs.Select(r => r.ToString(@"hh\\:mm")))} Sensex=OFF");

            _log.LogInformation(
                "Live COMPARE mode: CLOSE={Close} TICK={Tick} Paper={Paper}. Log={Log}",
                UseCandleEntry, UseLiveEntry, PaperMode, _compareLogPath);

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
                            ResetDayIfNeeded(now);

                            if (UseLiveEntry && !_feedStarted)
                            {
                                var opt = scope.ServiceProvider
                                    .GetService<Microsoft.Extensions.Options.IOptions<Models.FyersSettings>>();
                                if (opt != null)
                                {
                                    _ = _feed.StartAsync(opt.Value.ClientId, token.AccessToken, new List<string>());
                                    _feedStarted = true;
                                    CompareLog("FEED", "-", "HSM LITE tick feed START");
                                }
                            }

                            await ManageExits(hist, token.AccessToken, now);
                            await ScanSignals(hist, db, token.AccessToken, now);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Live loop error: " + ex.Message);
                    CompareLog("ERROR", "-", ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stop);
            }
        }

        private static bool IsMarketHours(DateTime now)
        {
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var t = now.TimeOfDay;
            return t >= new TimeSpan(9, 15, 0) && t <= new TimeSpan(15, 35, 0);
        }

        private void ResetDayIfNeeded(DateTime now)
        {
            lock (_entryLock)
            {
                if (_enteredDay != now.Date)
                {
                    _enteredByMode.Clear();
                    _enteredDay = now.Date;
                    EnsureCompareLog(now);
                    CompareLog("DAY", "-", $"new session {_enteredDay:yyyy-MM-dd}");
                }
            }
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

                        // TICK path: setup ready -> WatchLevel (fires OnCross with sec precision)
                        if (UseLiveEntry)
                            TryArmLive(sym, cepe, opt, config, rs, now);
                    }
                }
            }

            if (!UseCandleEntry) return;

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
                if (decisions[cands[i]].Skipped)
                {
                    CompareLog("CLOSE_SKIP", sym,
                        $"portfolio skip ref={rw} signalCandle={s.EntryTime:HH:mm:ss}");
                    continue;
                }

                var confirmAt = s.EntryTime.AddMinutes(RecommendedLiveConfig.CandleMinutes);
                double ageSinceClose = (now - confirmAt).TotalMinutes;
                if (ageSinceClose < -0.25 || ageSinceClose > RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                {
                    if (ageSinceClose > RecommendedLiveConfig.FreshSignalMaxAgeMinutes)
                        CompareLog("CLOSE_STALE", sym,
                            $"signalCandle={s.EntryTime:HH:mm:ss} closeAt={confirmAt:HH:mm:ss} ageSinceClose={ageSinceClose:F2}m");
                    continue;
                }

                // Entry ALWAYS at reference candle high (long). Recalc target from that entry.
                decimal refHigh = s.Reference.High;
                decimal entryPx = refHigh;
                decimal sl = s.StopLoss;
                decimal risk = entryPx - sl;
                if (risk <= 0)
                {
                    CompareLog("CLOSE_SKIP", sym, $"risk<=0 entry(refHigh)={entryPx} sl={sl}");
                    continue;
                }
                decimal tgt = entryPx + risk * s.RiskRewardRatio;

                // BuySignal = confirm ke BAAD wali candle ne ref-high touch kiya (usi confirm candle pe nahi)
                if (UseCandleEntry)
                {
                    await TryTakeEntry(
                        mode: "CLOSE",
                        sym: sym,
                        cepe: cepe,
                        px: entryPx,
                        sl: sl,
                        tgt: tgt,
                        entryTimeKey: s.EntryTime,
                        detail: $"POST_CONFIRM_ENTRY @REF_HIGH={refHigh} ref={rw} " +
                                $"entryCandle={s.EntryTime:HH:mm:ss}-{confirmAt:HH:mm:ss} " +
                                $"(confirm candle pe entry nahi; next candle pe level touch) age={ageSinceClose:F2}m");
                }
            }
        }

        /// <summary>
        /// TICK fill: WaitingForEntry (close-confirm ho chuka) — LTP jab ref-high touch kare.
        /// Confirm candle pe entry nahi; uske baad wale time pe @ ref high.
        /// </summary>
        private void ArmTickFillAtRefHigh(string sym, string cepe, decimal refHigh, decimal sl, decimal tgt,
            string rw, DateTime armedAt, DateTime confirmAt)
        {
            lock (_entryLock)
            {
                if (_enteredByMode.ContainsKey(sym)) return;
            }

            CompareLog("TICK_ARM", sym,
                $"POST_CONFIRM wait LTP<=REF_HIGH={refHigh} entry@REF_HIGH (not confirm candle) ref={rw} " +
                $"armedAt={armedAt:HH:mm:ss.fff}");

            _feed.WatchLevel(sym, refHigh, above: false, (s, ltp) =>
            {
                var fireAt = DateTime.Now;
                if (!LivePortfolioAllows(fireAt))
                {
                    CompareLog("TICK_SKIP", s,
                        $"portfolio block LTP={ltp} refHigh={refHigh} fireAt={fireAt:HH:mm:ss.fff}");
                    return;
                }

                _ = TryTakeEntry(
                    mode: "TICK",
                    sym: s,
                    cepe: cepe,
                    px: refHigh,
                    sl: sl,
                    tgt: tgt,
                    entryTimeKey: fireAt,
                    detail: $"POST_CONFIRM FILL@REF_HIGH={refHigh} LTP={ltp} ref={rw} " +
                            $"fireAt={fireAt:HH:mm:ss.fff} feedLastTick={_feed.LastTickAt:HH:mm:ss.fff} tick#{_feed.TickCount}");
            });

            var cur = _feed.Ltp(sym);
            if (cur != null && cur.Value > 0 && cur.Value <= refHigh)
            {
                var fireAt = DateTime.Now;
                CompareLog("TICK_IMMEDIATE", sym,
                    $"LTP={cur} already <= REF_HIGH={refHigh} -> fill now {fireAt:HH:mm:ss.fff}");
                _feed.ClearWatch(sym);
                if (LivePortfolioAllows(fireAt))
                {
                    _ = TryTakeEntry("TICK", sym, cepe, refHigh, sl, tgt, fireAt,
                        $"POST_CONFIRM FILL@REF_HIGH={refHigh} LTP={cur} IMMEDIATE ref={rw}");
                }
            }
        }

        /// <summary>
        /// Close-confirm ke baad WaitingForEntry — next bars/ticks pe ref-high pe entry.
        /// </summary>
        private void TryArmLive(string sym, string cepe, List<Candle> opt, StrategyConfig config,
            TimeSpan rs, DateTime now)
        {
            if (now.TimeOfDay >= config.SquareOffTime)
            { _feed.ClearWatch(sym); return; }

            lock (_entryLock)
            {
                if (_enteredByMode.ContainsKey(sym))
                { _feed.ClearWatch(sym); return; }
            }

            var lm = StrategyBacktester.RunLongManager(opt, config, rs);
            if (lm.State != StrategyState.WaitingForEntry)
            {
                if (lm.State == StrategyState.WaitingForSecondBreakout)
                    CompareLog("TICK_WAIT_CLOSE", sym,
                        $"state={lm.State} — pehle CLOSE > refHigh chahiye; phir entry level ka wait");
                return;
            }

            var refc = lm.CurrentReference;
            if (refc != null && config.UseReferenceMaxWait &&
                now > refc.EndTime.AddMinutes(config.MaxReferenceWaitMinutes))
            { _feed.ClearWatch(sym); return; }

            decimal refHigh = lm.EntryCapLevel;
            decimal retr = lm.RetracementLevel;
            decimal rr = config.RiskRewardRatio;
            decimal sl = retr - config.StopLossBufferPoints;
            decimal risk = refHigh - sl;
            if (risk <= 0) return;
            decimal tgt = refHigh + risk * rr;
            string rw = $"{rs:hh\\:mm}";

            ArmTickFillAtRefHigh(sym, cepe, refHigh, sl, tgt, rw, now, now);
        }

        /// <summary>
        /// Pehli mode PAPER entry leti hai; doosri SHADOW log (timing compare ke liye).
        /// </summary>
        private async Task TryTakeEntry(string mode, string sym, string cepe,
            decimal px, decimal sl, decimal tgt, DateTime entryTimeKey, string detail)
        {
            var now = DateTime.Now;
            string? already = null;
            bool take = false;
            lock (_entryLock)
            {
                ResetDayIfNeeded(now);
                if (_enteredByMode.TryGetValue(sym, out var who))
                    already = who;
                else
                {
                    _enteredByMode[sym] = mode;
                    take = true;
                }
            }

            if (!take)
            {
                CompareLog($"{mode}_SHADOW", sym,
                    $"wouldEnter @ {px} SL {sl} T {tgt} | alreadyEnteredBy={already} | {detail}");
                _log.LogInformation(
                    "[{Mode}_SHADOW] {Time:HH:mm:ss.fff} {Sym} @ {Px} (already {Who}) {Detail}",
                    mode, now, sym, px, already, detail);
                return;
            }

            if (!LivePortfolioAllows(now) && mode == "CLOSE")
            {
                // CLOSE path already filtered by PortfolioSelector; keep soft check for TICK primarily
            }

            int qty = SizeQty(sym, px);
            await _mgr.OnSignal(sym, cepe, qty, px, sl, tgt, entryTimeKey);
            CompareLog($"{mode}_ENTRY", sym,
                $"EXECUTED @ {px} SL {sl} T {tgt} qty={qty} | {detail}");
            _log.LogInformation(
                "[{Mode}_ENTRY] {Time:HH:mm:ss.fff} {Sym} {Side} @ {Px} SL {Sl} T {Tgt} qty={Qty} | {Detail}",
                mode, now, sym, cepe, px, sl, tgt, qty, detail);
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

        private void EnsureCompareLog(DateTime now)
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                _compareLogPath = Path.Combine(dir, $"live-compare-{now:yyyy-MM-dd}.log");
                if (!File.Exists(_compareLogPath))
                {
                    File.WriteAllText(_compareLogPath,
                        "# time\tmode\tsymbol\tdetail\n");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("compare log file create fail: " + ex.Message);
                _compareLogPath = null;
            }
        }

        private void CompareLog(string mode, string symbol, string detail)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff}\t{mode}\t{symbol}\t{detail}";
            _log.LogInformation("[COMPARE] {Line}", line);
            if (_compareLogPath == null) return;
            lock (_logFileLock)
            {
                try { File.AppendAllText(_compareLogPath, line + Environment.NewLine); }
                catch { /* ignore IO race */ }
            }
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
