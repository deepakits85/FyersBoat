using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using FyersLoginWeb.Models;
using FyersLoginWeb.Services;
using FyersLoginWeb.Services.Broker;
using FyersLoginWeb.Strategy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FyersLoginWeb.Controllers
{
    // LIVE feed test/diagnostics — Fyers SDK HSM websocket se real-time LTP.
    // Commodity (MCX) shaam ko bhi live hota hai -> off-hours test ke liye.
    public class LiveFeedController : Controller
    {
        private readonly FyersAuthService _auth;
        private readonly FyersLiveFeed _feed;
        private readonly FyersSettings _cfg;
        private readonly PositionManager _mgr;
        private readonly PaperBrokerOrders _broker;
        private readonly FyersHistoryService _history;

        public LiveFeedController(FyersAuthService auth, FyersLiveFeed feed, IOptions<FyersSettings> cfg,
            PositionManager mgr, PaperBrokerOrders broker, FyersHistoryService history)
        {
            _auth = auth;
            _feed = feed;
            _cfg = cfg.Value;
            _mgr = mgr;
            _broker = broker;
            _history = history;
        }

        // GET /LiveFeed/Steps?symbol=BSE:SENSEX26JUL77600CE&rr=3&date=2026-07-29
        // ASLI strategy ka poora step-trace: reference candle (High/Low/prior-high), first breakout,
        // retracement, 2nd breakout, entry level + outcome — HAR reference window ke liye.
        [HttpGet]
        public async Task<IActionResult> Steps(string symbol, decimal rr = 3m, string date = "")
        {
            var token = _auth.GetValidToken();
            if (token == null) return Content("Koi valid token nahi. /Auth login.", "text/plain");
            var day = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date);
            var candles = await _history.GetCandlesAsync(symbol, "3", day, token.AccessToken);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{symbol}  {day:yyyy-MM-dd}  candles={candles.Count}  RR 1:{rr}  (retr 50%, prior-high ON, buf 0)");
            var cfg = new StrategyConfig
            { EntryBufferPoints = 0m, StopLossBufferPoints = 0m, RiskRewardRatio = rr, UseTrailing = true };
            cfg.SetRetracementFromPercentage(50m);

            var refs = new[] { new TimeSpan(10,15,0), new TimeSpan(10,45,0), new TimeSpan(11,15,0), new TimeSpan(11,45,0), new TimeSpan(12,45,0) };
            foreach (var rs in refs)
            {
                var res = StrategyBacktester.Run(candles, cfg, rs);
                var reference = res.LongSteps.Select(s => s.Reference).FirstOrDefault(r => r != null && r.StartTime.TimeOfDay == rs);
                sb.AppendLine($"\n=== REFERENCE {rs:hh\\:mm}-{rs.Add(TimeSpan.FromMinutes(30)):hh\\:mm} ===");
                if (reference != null)
                    sb.AppendLine($"  ref High={reference.High}  Low={reference.Low}  BreakoutLevel(prior-high included)={reference.BreakoutLevel}");
                else { sb.AppendLine("  (is ref ka data/setup nahi)"); continue; }

                foreach (var st in res.LongSteps.Where(s => s.Result.JustConfirmed != null))
                    sb.AppendLine($"  {st.Candle.StartTime:HH:mm}  [{st.Result.JustConfirmed}]  {st.Result.Message}");
                foreach (var sig in res.BuySignals)
                    sb.AppendLine($"  >>> ENTRY {sig.EntryTime:HH:mm} @ {sig.EntryPrice}  SL {sig.StopLoss}  target {sig.Target}  => {sig.Outcome} ({sig.RealizedR:+0.##;-0.##}R)");
                if (res.BuySignals.Count == 0) sb.AppendLine("  (koi entry nahi is ref se)");
            }
            return Content(sb.ToString(), "text/plain");
        }

        // GET /LiveFeed/TrailTest -> trailing state-machine ka deterministic verify (market ki zaroorat nahi).
        // entry 100, SL 90 (risk 10). Expect: OPEN target=125 (2.5R); 2R(120) par SL->101 (trailed);
        // 125 par TargetHit +2.5R.
        [HttpGet]
        public async Task<IActionResult> TrailTest()
        {
            var now = DateTime.Today.AddHours(11);   // market-hours time (square-off 2:45 se pehle)
            string sym = "TRAILTEST-" + DateTime.Now.ToString("HHmmss");
            var sb = new System.Text.StringBuilder();

            await _mgr.OnSignal(sym, "CE", 1, 100m, 90m, 120m, now);
            _broker.MarkPrice(sym, 100m, 100m, 100m, now);           // buy-limit fill
            await _mgr.Tick(now);
            var p = _mgr.Positions.First(x => x.Symbol == sym);
            sb.AppendLine($"1) entry:  state={p.State} SL={p.SL} target={p.Target}  [expect OPEN, SL 90, target 125]");

            _broker.MarkPrice(sym, 120m, 118m, 119m, now);            // 2R reached, <2.5R
            await _mgr.Tick(now);
            await _mgr.CheckTrail(sym, 120m, now);
            p = _mgr.Positions.First(x => x.Symbol == sym);
            sb.AppendLine($"2) at 2R:  state={p.State} SL={p.SL} trailed={p.Trailed}  [expect SL 101, trailed True]");

            _broker.MarkPrice(sym, 125m, 123m, 124m, now);            // target 2.5R hit
            await _mgr.Tick(now);
            p = _mgr.Positions.First(x => x.Symbol == sym);
            sb.AppendLine($"3) target: state={p.State} outcome={p.Outcome} exit={p.ExitPrice} R={p.RealizedR}  [expect CLOSED TargetHit 125 +2.5R]");

            // dusra scenario: 2R ke baad wapas entry+1 pe aaye -> TrailStopHit (breakeven+)
            string sym2 = "TRAILSTOP-" + DateTime.Now.ToString("HHmmss");
            await _mgr.OnSignal(sym2, "CE", 1, 100m, 90m, 120m, now);
            _broker.MarkPrice(sym2, 100m, 100m, 100m, now); await _mgr.Tick(now);
            _broker.MarkPrice(sym2, 120m, 118m, 119m, now); await _mgr.Tick(now); await _mgr.CheckTrail(sym2, 120m, now);
            _broker.MarkPrice(sym2, 119m, 101m, 102m, now); await _mgr.Tick(now);   // low touches 101
            var p2 = _mgr.Positions.First(x => x.Symbol == sym2);
            sb.AppendLine($"4) trailstop: outcome={p2.Outcome} exit={p2.ExitPrice} R={p2.RealizedR}  [expect TrailStopHit 101 +0.1R]");

            return Content(sb.ToString(), "text/plain");
        }

        // GET /LiveFeed/Start?symbols=MCX:CRUDEOIL25AUGFUT
        [HttpGet]
        public async Task<IActionResult> Start(string symbols = "MCX:CRUDEOIL25AUGFUT")
        {
            var token = _auth.GetValidToken();
            if (token == null)
                return Content("Koi valid token nahi. Pehle /Auth par login karo (commodity test ke liye bhi token chahiye).", "text/plain");

            var list = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim()).ToList();
            await _feed.StartAsync(_cfg.ClientId, token.AccessToken, list);
            return Content($"LiveFeed start requested for: {string.Join(", ", list)}\n" +
                           $"Ab /LiveFeed/Status kholo (2-3 sec baad) tick aane lage ya nahi dekhne ke liye.", "text/plain");
        }

        // GET /LiveFeed/Status
        [HttpGet]
        public IActionResult Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Connected : {_feed.Connected}");
            sb.AppendLine($"Ticks     : {_feed.TickCount}");
            sb.AppendLine($"LastTick  : {(_feed.LastTickAt == default ? "—" : _feed.LastTickAt.ToString("HH:mm:ss"))}");
            sb.AppendLine($"LastCross : {(string.IsNullOrEmpty(_feed.LastCrossInfo) ? "— (abhi tak koi cross nahi)" : _feed.LastCrossInfo)}");
            sb.AppendLine("--- armed watches ---");
            foreach (var w in _feed.ActiveWatches)
                sb.AppendLine($"{w.Symbol,-28} {(w.Above ? ">=" : "<=")} {w.Level}");
            sb.AppendLine("--- positions (paper) ---");
            foreach (var p in _mgr.Positions)
                sb.AppendLine($"{p.Symbol,-28} {p.Side} x{p.Qty} @ {p.Entry}  SL {p.SL}  T {p.Target}  [{p.State}]");
            sb.AppendLine("--- latest LTP ---");
            foreach (var kv in _feed.AllLtp)
                sb.AppendLine($"{kv.Key,-28} {kv.Value}");
            return Content(sb.ToString(), "text/plain");
        }

        // GET /LiveFeed/TestEntry?symbol=MCX:CRUDEOIL26AUGFUT&level=7530&dir=above
        // POORA real-time entry pipeline live-verify: level cross hote hi ASLI paper entry
        // (_mgr.OnSignal -> paper position). Live MCX se end-to-end proof.
        [HttpGet]
        public IActionResult TestEntry(string symbol, decimal level, string dir = "above")
        {
            var token = _auth.GetValidToken();
            if (token == null) return Content("Koi valid token nahi. Pehle /Auth login.", "text/plain");
            if (!_feed.Connected)
                _ = _feed.StartAsync(_cfg.ClientId, token.AccessToken, new System.Collections.Generic.List<string> { symbol });

            bool above = !string.Equals(dir, "below", StringComparison.OrdinalIgnoreCase);
            _feed.WatchLevel(symbol, level, above, (sym, ltp) =>
            {
                // real-time entry: live price pe paper position banao (yahi bot me hoga)
                decimal sl = above ? ltp - 10m : ltp + 10m;
                decimal target = above ? ltp + 20m : ltp - 20m;
                _ = _mgr.OnSignal(sym, "CE", 1, ltp, sl, target, DateTime.Now);
            });
            return Content($"TEST-ENTRY armed: {symbol} {(above ? ">=" : "<=")} {level}\n" +
                           $"Cross hote hi paper entry banegi -> /LiveFeed/Status me 'positions' dekho.", "text/plain");
        }

        // GET /LiveFeed/Watch?symbol=MCX:CRUDEOIL26AUGFUT&level=7600&dir=above
        // Live LTP jaise hi level cross kare -> turant trigger (real-time entry ka core).
        // Test: dir=above -> level current price se thoda UPAR; dir=below -> thoda NEECHE rakho.
        [HttpGet]
        public IActionResult Watch(string symbol, decimal level, string dir = "above")
        {
            var token = _auth.GetValidToken();
            if (token == null) return Content("Koi valid token nahi. Pehle /Auth login.", "text/plain");
            // feed already chal raha ho to reuse; na chale to start
            if (!_feed.Connected)
                _ = _feed.StartAsync(_cfg.ClientId, token.AccessToken, new System.Collections.Generic.List<string> { symbol });

            bool above = !string.Equals(dir, "below", StringComparison.OrdinalIgnoreCase);
            _feed.WatchLevel(symbol, level, above, (sym, ltp) =>
            {
                // >>> ASLI bot me yahan _mgr.OnSignal(...) aayega (live price pe entry) <<<
                // abhi test: LiveFeed khud LastCrossInfo + log set karta hai.
            });
            return Content($"WATCH armed: {symbol} {(above ? ">=" : "<=")} {level}\n" +
                           $"Ab price cross karte hi /LiveFeed/Status me LastCross dikhega (turant).", "text/plain");
        }

        // GET /LiveFeed/StrategyArm?symbol=MCX:CRUDEOIL26AUGFUT
        // ASLI production arm-logic (RunLongManager -> state -> WatchLevel -> OnSignal) ko LIVE
        // verify karta hai — synthetic candles se strategy ko WaitingForSecondBreakout me laata
        // hai (entry level = live LTP + 2), phir live tick cross karte hi asli entry.
        [HttpGet]
        public async Task<IActionResult> StrategyArm(string symbol = "MCX:CRUDEOIL26AUGFUT")
        {
            var token = _auth.GetValidToken();
            if (token == null) return Content("Koi valid token nahi. /Auth login.", "text/plain");
            if (!_feed.Connected)
                await _feed.StartAsync(_cfg.ClientId, token.AccessToken, new List<string> { symbol });
            else _feed.Subscribe(new List<string> { symbol });

            decimal? ltp = null;
            for (int i = 0; i < 20 && ltp == null; i++) { await Task.Delay(500); ltp = _feed.Ltp(symbol); }
            if (ltp == null) return Content("Live LTP nahi mila (feed connect nahi hua?).", "text/plain");
            decimal L = ltp.Value;

            // synthetic setup: EntryCapLevel = L + 2 (reachable). UsePriorLevelBreak=false, buffer=0.
            decimal refHigh = L + 2m, refLow = refHigh - 30m, retr = refHigh - 15m;
            var day = DateTime.Today;
            DateTime T(int h, int m) => day.AddHours(h).AddMinutes(m);
            var candles = new List<Candle>
            {
                new Candle(T(9,15), T(9,18), refLow + 5m, refHigh,      refLow,       refHigh - 10m), // 9:15 window (High=refHigh)
                new Candle(T(9,45), T(9,48), refHigh,      refHigh + 3m, refHigh - 1m, refHigh + 1m),  // FIRST breakout
                new Candle(T(9,48), T(9,51), refHigh - 2m, refHigh,      retr - 2m,    refHigh - 5m),  // RETRACEMENT
            };
            var cfg = new StrategyConfig
            { UsePriorLevelBreak = false, EntryBufferPoints = 0m, StopLossBufferPoints = 0m, RiskRewardRatio = 2m, UseTrailing = true };
            cfg.SetRetracementFromPercentage(50m);

            var lm = StrategyBacktester.RunLongManager(candles, cfg, new TimeSpan(9, 15, 0));

            var sb = new StringBuilder();
            sb.AppendLine($"symbol {symbol}  live LTP {L}");
            sb.AppendLine($"Strategy State  = {lm.State}");
            sb.AppendLine($"EntryCapLevel   = {lm.EntryCapLevel}  (target {refHigh})");
            sb.AppendLine($"RetracementLvl  = {lm.RetracementLevel}");
            if (lm.State == StrategyState.WaitingForSecondBreakout || lm.State == StrategyState.WaitingForEntry)
            {
                decimal level = lm.EntryCapLevel;
                bool above = lm.State == StrategyState.WaitingForSecondBreakout;
                decimal retrLvl = lm.RetracementLevel;
                _feed.WatchLevel(symbol, level, above, (s, px) =>
                {
                    decimal sl = retrLvl - cfg.StopLossBufferPoints;
                    decimal risk = px - sl; if (risk <= 0) return;
                    decimal target = px + risk * cfg.RiskRewardRatio;
                    _ = _mgr.OnSignal(s, "CE", 1, px, sl, target, DateTime.Now);
                });
                sb.AppendLine($">>> ARMED (asli logic): live price {(above ? ">=" : "<=")} {level} hote hi entry. /LiveFeed/Status dekho.");
            }
            else sb.AppendLine(">>> Arming-state me nahi aaya.");
            return Content(sb.ToString(), "text/plain");
        }

        // GET /LiveFeed/Add?symbols=...
        [HttpGet]
        public IActionResult Add(string symbols)
        {
            var list = (symbols ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim()).ToList();
            if (list.Count > 0) _feed.Subscribe(list);
            return Content($"Subscribed: {string.Join(", ", list)}", "text/plain");
        }
    }
}
