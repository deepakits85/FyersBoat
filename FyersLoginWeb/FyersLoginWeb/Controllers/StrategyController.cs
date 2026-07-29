using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FyersLoginWeb.Models;
using FyersLoginWeb.Services;
using FyersLoginWeb.Services.Broker;
using FyersLoginWeb.Strategy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FyersLoginWeb.Controllers
{
    public class StrategyController : Controller
    {
        private readonly FyersAuthService _auth;
        private readonly FyersHistoryService _history;
        private readonly DbService _db;
        private readonly PositionManager _mgr;
        private readonly PaperBrokerOrders _paper;

        public StrategyController(FyersAuthService auth, FyersHistoryService history, DbService db,
            PositionManager mgr, PaperBrokerOrders paper)
        {
            _auth = auth;
            _history = history;
            _db = db;
            _mgr = mgr;
            _paper = paper;
        }

        // GET /Strategy/IndexSim?symbol=BSE:SENSEX-INDEX&fromMonth=4&toMonth=7
        // Index chart par LATEST saare rules (square-off 2:45 + no-entry-after, reference-90min-wait,
        // 30-min gap, max-2-SL) lagakar multi-month sim. Long+Short dono. Cached data se (token optional).
        [HttpGet]
        public async Task<IActionResult> IndexSim(string symbol = "BSE:SENSEX-INDEX",
            int year = 2026, int fromMonth = 4, int toMonth = 7, int toYear = 0, decimal rr = 3m,
            string refs = "10:45,11:15,12:45", decimal entryBuffer = 0m, decimal slBuffer = 0m,
            int maxSl = 2, int gap = 30, bool squareOff = true, bool refWait = true, string side = "both",
            int skipDayFrom = 0, int skipDayTo = 0, bool trailing = false)
        {
            if (toYear == 0) toYear = year;   // cross-year range support (e.g. Oct-2025 -> Jan-2026)
            var token = _auth.GetValidToken();
            string accessToken = token?.AccessToken ?? "";

            var config = new StrategyConfig
            {
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr,
                UseTrailing = trailing,
                UseSquareOff = squareOff,        // 2:45 square-off + no-entry-after
                UseReferenceMaxWait = refWait    // reference 90-min max-wait
            };
            config.SetRetracementFromPercentage(50m);

            var refStarts = refs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => TimeSpan.TryParse(s.Trim(), out var ts) ? ts : (TimeSpan?)null)
                .Where(ts => ts != null).Select(ts => ts!.Value).ToList();

            var all = new List<IndexTrade>();
            var sb = new System.Text.StringBuilder();
            try
            {
                // (year, fromMonth) se (toYear, toMonth) tak — saal cross kar sakta hai
                int cy = year, cmo = fromMonth;
                while (cy < toYear || (cy == toYear && cmo <= toMonth))
                {
                    var firstDay = new DateTime(cy, cmo, 1);
                    var lastDay = firstDay.AddMonths(1).AddDays(-1);
                    if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

                    for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                    {
                        if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;
                        var candles = await _history.GetCandlesAsync(symbol, "3", day, accessToken);
                        if (candles.Count == 0) continue;

                        foreach (var rs in refStarts)
                        {
                            var res = StrategyBacktester.Run(candles, config, rs);
                            IEnumerable<TradeSignal> sigs =
                                side == "long" ? res.BuySignals :
                                side == "short" ? res.SellSignals :
                                res.BuySignals.Concat(res.SellSignals);
                            foreach (var s in sigs) all.Add(new IndexTrade(s));
                        }
                        await Task.Delay(40);   // rate-limit safety (fresh fetch ke liye)
                    }
                    cmo++; if (cmo > 12) { cmo = 1; cy++; }
                }
            }
            catch (Exception ex)
            {
                return Content("Error (data cached nahi + token expired?): " + ex.Message, "text/plain");
            }

            // date-avoid filter: mahine ke skipDayFrom..skipDayTo tareekh ke trades chhod do
            if (skipDayFrom > 0 && skipDayTo > 0)
                all = all.Where(t => !(t.EntryTime.Day >= skipDayFrom && t.EntryTime.Day <= skipDayTo)).ToList();

            // LATEST portfolio rules: 30-min gap + max-2-SL (single symbol -> priority moot). SHARED selector.
            var decisions = PortfolioSelector.Select(all, maxSlPerDay: maxSl, minGapMinutes: gap);
            var taken = all.Where(t => !decisions[t].Skipped).ToList();

            sb.AppendLine($"{symbol}  {fromMonth}/{year} - {toMonth}/{toYear}  (refs {refs}, RR 1:{rr}, buf {entryBuffer}/{slBuffer})");
            sb.AppendLine($"Rules: square-off={(squareOff ? "ON(2:45)" : "OFF")}, ref-max-wait={(refWait ? config.MaxReferenceWaitMinutes + "min" : "OFF")}, {gap}-min gap, max {maxSl} SL/din, side={side}" +
                (skipDayFrom > 0 && skipDayTo > 0 ? $", AVOID day {skipDayFrom}-{skipDayTo}" : "") + ".");
            sb.AppendLine(new string('-', 78));
            sb.AppendLine($"{"Month",-8}{"Raw#",6}{"RawR",9}   {"Taken#",7}{"TakenR",9}{"Win%",7}{"SL",5}{"Sqoff",7}");

            string Fmt(IEnumerable<IndexTrade> g)
            {
                var lst = g.ToList();
                int wins = lst.Count(x => x.Sig.RealizedR > 0);
                int losses = lst.Count(x => x.Sig.RealizedR < 0);
                int sl = lst.Count(x => x.Sig.Outcome == TradeOutcome.StopLossHit);
                int sq = lst.Count(x => x.Sig.Outcome == TradeOutcome.TimeExit);
                decimal r = Math.Round(lst.Sum(x => x.Sig.RealizedR), 1);
                int wr = (wins + losses) == 0 ? 0 : (int)Math.Round(100.0 * wins / (wins + losses));
                return $"{lst.Count,7}{r,9}{wr + "%",7}{sl,5}{sq,7}";
            }

            foreach (var g in all.GroupBy(t => new { t.EntryTime.Year, t.EntryTime.Month })
                                  .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
            {
                var takM = taken.Where(t => t.EntryTime.Year == g.Key.Year && t.EntryTime.Month == g.Key.Month).ToList();
                var mn = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM-yy");
                sb.AppendLine($"{mn,-8}{g.Count(),6}{Math.Round(g.Sum(x => x.Sig.RealizedR), 1),9}   {Fmt(takM)}");
            }
            sb.AppendLine(new string('-', 78));
            sb.AppendLine($"{"TOTAL",-8}{all.Count,6}{Math.Round(all.Sum(x => x.Sig.RealizedR), 1),9}   {Fmt(taken)}");
            sb.AppendLine();
            sb.AppendLine($"TAKEN net R = {Math.Round(taken.Sum(x => x.Sig.RealizedR), 2)} over {taken.Count} trades " +
                          $"(exp {(taken.Count == 0 ? 0 : Math.Round(taken.Sum(x => x.Sig.RealizedR) / taken.Count, 2))} R/trade). " +
                          $"Portfolio ne {all.Count - taken.Count} skip kiye (gap/max-2-SL).");
            return Content(sb.ToString(), "text/plain");
        }

        // GET /Strategy/PortfolioSim -> teeno INDEX ek saath (Nifty+SENSEX+BankNifty), LIVE jaisa
        // CROSS-INDEX selector (30-min gap + max-2-SL + NIFTY-priority) ek hi timeline par.
        // IndexSim per-index alag chalta tha (priority moot); yahan combined -> priority/grace test hota hai.
        // grace = NIFTY-priority grace window (min). grace=0 -> exact-time tie only (purana behaviour).
        // Defaults = RecommendedLiveConfig (Nifty+Bank, 11:15/12:45, no Sensex).
        [HttpGet]
        public async Task<IActionResult> PortfolioSim(
            int year = 2025, int fromMonth = 8, int toYear = 2026, int toMonth = 6,
            string refs = "11:15,12:45",
            int maxSl = 2, int gap = 30, int grace = 0, bool trailing = true, string side = "both",
            bool squareOff = true, bool refWait = true, bool bank = true, bool sensex = false,
            bool useOptions = false, bool list = false,
            bool fromDb = false, bool retrFirst = true, bool closeConfirm = true, bool confirmRetr = false)
        {
            var token = _auth.GetValidToken();
            string accessToken = token?.AccessToken ?? "";

            var refStarts = refs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => TimeSpan.TryParse(s.Trim(), out var ts) ? ts : (TimeSpan?)null)
                .Where(ts => ts != null).Select(ts => ts!.Value).ToList();

            // legs: index symbol, RR, priority (0=Nifty best). sensex=false (recommended).
            // bank=false -> Bank drop. useOptions=true -> ATM option chart pe.
            var legs = new[]
            {
                new { Sym = "NSE:NIFTY50-INDEX",   RR = 2m, Prio = 0, Name = "Nifty",     OptRoot = "NSE:NIFTY",     Step = 50m  },
                new { Sym = "BSE:SENSEX-INDEX",    RR = 3m, Prio = 1, Name = "SENSEX",    OptRoot = "BSE:SENSEX",    Step = 100m },
                new { Sym = "NSE:NIFTYBANK-INDEX", RR = 2m, Prio = 2, Name = "BankNifty", OptRoot = "NSE:BANKNIFTY", Step = 100m },
            }.Where(l => (bank || l.Name != "BankNifty") && (sensex || l.Name != "SENSEX")).ToArray();

            var all = new List<PfTrade>();
            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (var leg in legs)
                {
                    var config = new StrategyConfig
                    {
                        EntryBufferPoints = 0m,
                        StopLossBufferPoints = 0m,
                        RiskRewardRatio = leg.RR,
                        UseTrailing = trailing,
                        UseSquareOff = squareOff,
                        UseReferenceMaxWait = refWait,
                        UseRetracementFirst = retrFirst,
                        RequireCloseConfirm = closeConfirm,
                        ConfirmRetrFirstOnly = confirmRetr
                    };
                    config.SetRetracementFromPercentage(50m);

                    int cy = year, cmo = fromMonth;
                    while (cy < toYear || (cy == toYear && cmo <= toMonth))
                    {
                        var firstDay = new DateTime(cy, cmo, 1);
                        var lastDay = firstDay.AddMonths(1).AddDays(-1);
                        if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

                        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                        {
                            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;

                            if (!useOptions)
                            {
                                // ---- INDEX mode ----
                                var candles = fromDb ? _db.GetCandles(leg.Sym, "3", day)
                                                     : await _history.GetCandlesAsync(leg.Sym, "3", day, accessToken);
                                if (candles.Count == 0) continue;
                                foreach (var rs in refStarts)
                                {
                                    var res = StrategyBacktester.Run(candles, config, rs);
                                    IEnumerable<TradeSignal> sigs =
                                        side == "long" ? res.BuySignals :
                                        side == "short" ? res.SellSignals :
                                        res.BuySignals.Concat(res.SellSignals);
                                    foreach (var s in sigs) all.Add(new PfTrade(s, leg.Prio, leg.Name, rs));
                                }
                                await Task.Delay(40);
                            }
                            else
                            {
                                // ---- OPTIONS mode: ATM strike (ref-start spot se), CE+PE dono KHARIDNA ----
                                var idx = fromDb ? _db.GetCandles(leg.Sym, "3", day)
                                                 : await _history.GetCandlesAsync(leg.Sym, "3", day, accessToken);
                                if (idx.Count == 0) continue;
                                string yy = day.ToString("yy");
                                string mmm = day.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpper();
                                foreach (var rs in refStarts)
                                {
                                    var monStart = rs.Add(TimeSpan.FromMinutes(30));
                                    var spotCandle = idx.FirstOrDefault(c => c.StartTime.TimeOfDay == monStart);
                                    if (spotCandle == null) continue;
                                    long strike = (long)(Math.Round(spotCandle.Open / leg.Step) * leg.Step);
                                    foreach (var cepe in new[] { "CE", "PE" })
                                    {
                                        // side filter: CE=bullish(long), PE=bearish; long-only -> CE, short-only -> PE
                                        if (side == "long" && cepe != "CE") continue;
                                        if (side == "short" && cepe != "PE") continue;
                                        string sym = $"{leg.OptRoot}{yy}{mmm}{strike}{cepe}";
                                        var opt = fromDb ? _db.GetCandles(sym, "3", day)
                                                         : await _history.GetCandlesAsync(sym, "3", day, accessToken);
                                        if (!fromDb) await Task.Delay(60);
                                        if (opt.Count == 0) continue;
                                        var res = StrategyBacktester.Run(opt, config, rs);
                                        foreach (var s in res.BuySignals)   // option KHARIDNA hamesha BUY
                                            all.Add(new PfTrade(s, leg.Prio, leg.Name, rs, sym));
                                    }
                                }
                            }
                        }
                        cmo++; if (cmo > 12) { cmo = 1; cy++; }
                    }
                }
            }
            catch (Exception ex)
            {
                return Content("Error (data cached nahi + token expired?): " + ex.Message, "text/plain");
            }

            // CROSS-INDEX shared selector — ek hi combined timeline par (live jaisa)
            var decisions = PortfolioSelector.Select(all, maxSlPerDay: maxSl, minGapMinutes: gap, graceMinutes: grace);
            var taken = all.Where(t => !decisions[t].Skipped).ToList();

            sb.AppendLine($"COMBINED PortfolioSim  {fromMonth}/{year} - {toMonth}/{toYear}  | legs: {string.Join("+", legs.Select(l => l.Name))} | refs {refs}");
            sb.AppendLine($"trailing={(trailing ? "ON" : "OFF")}, square-off={(squareOff ? "ON(2:45)" : "OFF")}, ref-max-wait={(refWait ? "ON" : "OFF")}, {gap}-min gap, max {maxSl} SL/din, side={side}, GRACE={grace}min");
            sb.AppendLine(new string('-', 90));
            sb.AppendLine($"{"Leg",-11}{"Raw#",6}{"RawR",9}   {"Taken#",7}{"TakenR",9}{"Win%",7}{"SL",5}{"Sqoff",7}");

            string Fmt(IEnumerable<PfTrade> g)
            {
                var lst = g.ToList();
                int wins = lst.Count(x => x.Sig.RealizedR > 0);
                int losses = lst.Count(x => x.Sig.RealizedR < 0);
                int sl = lst.Count(x => x.Sig.Outcome == TradeOutcome.StopLossHit);
                int sq = lst.Count(x => x.Sig.Outcome == TradeOutcome.TimeExit);
                decimal r = Math.Round(lst.Sum(x => x.Sig.RealizedR), 1);
                int wr = (wins + losses) == 0 ? 0 : (int)Math.Round(100.0 * wins / (wins + losses));
                return $"{lst.Count,7}{r,9}{wr + "%",7}{sl,5}{sq,7}";
            }

            foreach (var leg in legs)
            {
                var rawLeg = all.Where(t => t.Name == leg.Name);
                var takLeg = taken.Where(t => t.Name == leg.Name).ToList();
                sb.AppendLine($"{leg.Name,-11}{rawLeg.Count(),6}{Math.Round(rawLeg.Sum(x => x.Sig.RealizedR), 1),9}   {Fmt(takLeg)}");
            }
            sb.AppendLine(new string('-', 90));
            sb.AppendLine($"{"TOTAL",-11}{all.Count,6}{Math.Round(all.Sum(x => x.Sig.RealizedR), 1),9}   {Fmt(taken)}");
            sb.AppendLine();

            int gracePrio = all.Count(t => decisions[t].SkipReason == "gracePrio");
            int gapSkip = all.Count(t => decisions[t].SkipReason == "gap30");
            int slSkip = all.Count(t => decisions[t].SkipReason == "max2SL");
            sb.AppendLine($"TAKEN net R = {Math.Round(taken.Sum(x => x.Sig.RealizedR), 2)} over {taken.Count} trades " +
                          $"(exp {(taken.Count == 0 ? 0 : Math.Round(taken.Sum(x => x.Sig.RealizedR) / taken.Count, 2))} R/trade).");
            sb.AppendLine($"Skips: gap30={gapSkip}, max2SL={slSkip}, gracePrio(NIFTY-override)={gracePrio}.");
            var takenByLeg = string.Join(", ", legs.Select(l => $"{l.Name} {taken.Count(t => t.Name == l.Name)}"));
            sb.AppendLine($"Taken split: {takenByLeg}.");

            // ---- PER-REFERENCE breakdown (taken) ----
            sb.AppendLine();
            sb.AppendLine($"---- Per-REFERENCE (taken) ----");
            sb.AppendLine($"{"Ref",-8}{"Trades",8}{"R",9}{"Win%",7}{"SL",5}{"Target",8}{"TimeExit",10}");
            foreach (var rs in refStarts.OrderBy(r => r))
            {
                var tr = taken.Where(t => t.Ref == rs).ToList();
                int w = tr.Count(x => x.Sig.RealizedR > 0), lo = tr.Count(x => x.Sig.RealizedR < 0);
                int sl = tr.Count(x => x.Sig.Outcome == TradeOutcome.StopLossHit);
                int tg = tr.Count(x => x.Sig.Outcome == TradeOutcome.TargetHit);
                int te = tr.Count(x => x.Sig.Outcome == TradeOutcome.TimeExit);
                int wr = (w + lo) == 0 ? 0 : (int)Math.Round(100.0 * w / (w + lo));
                string refStr = rs.ToString(@"hh\:mm");
                sb.AppendLine($"{refStr,-8}{tr.Count,8}{Math.Round(tr.Sum(x => x.Sig.RealizedR), 1),9}{wr + "%",7}{sl,5}{tg,8}{te,10}");
            }

            // ---- AVERAGES (per month / per din) ----
            int slTotal = taken.Count(x => x.Sig.Outcome == TradeOutcome.StopLossHit);
            int tgTotal = taken.Count(x => x.Sig.Outcome == TradeOutcome.TargetHit);
            var monthsSet = taken.Select(t => new { t.Sig.EntryTime.Year, t.Sig.EntryTime.Month }).Distinct().Count();
            var daysSet = taken.Select(t => t.Sig.EntryTime.Date).Distinct().Count();
            sb.AppendLine();
            sb.AppendLine($"---- AVERAGES ----");
            sb.AppendLine($"Total taken: {taken.Count} trades  |  SL {slTotal}  |  Target {tgTotal}  over {monthsSet} months, {daysSet} trading-days");
            if (monthsSet > 0) sb.AppendLine($"Per MONTH avg: {Math.Round(taken.Count / (double)monthsSet, 1)} trades  ({Math.Round(slTotal / (double)monthsSet, 1)} SL, {Math.Round(tgTotal / (double)monthsSet, 1)} target)");
            if (daysSet > 0)   sb.AppendLine($"Per DAY  avg (jin dino trade hua): {Math.Round(taken.Count / (double)daysSet, 2)} trades");

            if (list)
            {
                sb.AppendLine();
                sb.AppendLine("---- TAKEN trades (date-wise) ----");
                foreach (var t in taken.OrderBy(x => x.Sig.EntryTime))
                    sb.AppendLine($"{t.Sig.EntryTime:dd-MMM ddd HH:mm}  {(string.IsNullOrEmpty(t.Sym) ? t.Name : t.Sym),-28} {t.Sig.Outcome,-13} R={Math.Round(t.Sig.RealizedR, 2),6}");
                // skipped-by-cap trades bhi dikhao (maxSl ka asar samajhne ke liye)
                var capSkipped = all.Where(x => decisions[x].SkipReason == "max2SL").OrderBy(x => x.Sig.EntryTime).ToList();
                if (capSkipped.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"---- SKIPPED by max{maxSl}SL cap ({capSkipped.Count}) ----");
                    foreach (var t in capSkipped)
                        sb.AppendLine($"{t.Sig.EntryTime:dd-MMM ddd HH:mm}  {(string.IsNullOrEmpty(t.Sym) ? t.Name : t.Sym),-28} would-be {t.Sig.Outcome,-13} R={Math.Round(t.Sig.RealizedR, 2),6}");
                }
            }
            return Content(sb.ToString(), "text/plain");
        }

        // GET /Strategy/Day?symbol=NSE:SBIN-EQ&retracement=50&entryBuffer=5&slBuffer=5&rr=3
        // Pichhle trading din ka real 3-min data laa kar strategy chalata hai, UI me dikhata hai.
        [HttpGet]
        public async Task<IActionResult> Day(string symbol = "NSE:SBIN-EQ", decimal retracement = 50m,
            decimal entryBuffer = 5m, decimal slBuffer = 5m, decimal rr = 3m, string date = "",
            string refStart = "", bool trailing = true)
        {
            var config = new StrategyConfig
            {
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr,
                UseTrailing = trailing
            };
            config.SetRetracementFromPercentage(retracement);

            TimeSpan? onlyRef = TimeSpan.TryParse(refStart, out var ts) ? ts : (TimeSpan?)null;

            var vm = new StrategyDayViewModel
            {
                Symbol = symbol,
                RetracementPercent = config.RetracementPercent,
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr
            };

            // 1) Login token chahiye
            var token = _auth.GetValidToken();
            if (token == null)
            {
                vm.Error = "Koi valid token nahi. Pehle /Auth par jaakar login karo.";
                return View(vm);
            }

            // 2) Candles laao — specific date di ho to wahi, warna last trading day
            List<Candle> threeMin;
            DateTime day;
            try
            {
                if (!string.IsNullOrWhiteSpace(date) &&
                    DateTime.TryParse(date, out var reqDay))
                {
                    day = reqDay.Date;
                    threeMin = await _history.GetCandlesAsync(symbol, "3", day, token.AccessToken);
                }
                else
                {
                    (day, threeMin) = await _history.GetLastTradingDay3MinAsync(symbol, token.AccessToken);
                }
            }
            catch (Exception ex)
            {
                vm.Error = "Data laane me error: " + ex.Message;
                return View(vm);
            }

            vm.TradingDay = day;
            vm.ThreeMinCount = threeMin.Count;

            if (threeMin.Count == 0)
            {
                vm.Error = "Is symbol/din ka koi data nahi mila.";
                return View(vm);
            }

            // 3) Backtest chalao (Long + Short dono, outcome sab isme)
            var result = StrategyBacktester.Run(threeMin, config, onlyRef);

            vm.ReferenceCount = result.ReferenceCount;
            vm.BuySignals = result.BuySignals;
            vm.SellSignals = result.SellSignals;
            vm.Steps = MapSteps(result.LongSteps, config.RetracementPercent, TradeSide.Long);
            vm.ShortSteps = MapSteps(result.ShortSteps, config.RetracementPercent, TradeSide.Short);

            return View(vm);
        }

        private static List<StrategyStepRow> MapSteps(List<BacktestStep> steps, decimal pct, TradeSide side) =>
            steps.Select(s => new StrategyStepRow
            {
                Time = s.Candle.StartTime.ToString("HH:mm"),
                RefWindow = $"{s.Reference.StartTime:HH:mm}-{s.Reference.EndTime:HH:mm}",
                RefHigh = s.Reference.High,
                RefLow = s.Reference.Low,
                // retracement level: long neeche, short upar
                RetrLevel = Math.Round(side == TradeSide.Long
                    ? s.Reference.High - s.Reference.Range * pct
                    : s.Reference.Low + s.Reference.Range * pct, 2),
                Open = s.Candle.Open,
                High = s.Candle.High,
                Low = s.Candle.Low,
                Close = s.Candle.Close,
                State = s.Result.State.ToString(),
                Confirmed = s.Result.JustConfirmed?.ToString(),
                Message = s.Result.Message
            }).ToList();

        // GET /Strategy/Month?symbol=NSE:NIFTY50-INDEX&year=2026&month=7&retracement=50&entryBuffer=5&slBuffer=5&rr=3
        // Poore mahine ke har trading din pe strategy chala kar BUY count + outcomes deta hai.
        [HttpGet]
        public async Task<IActionResult> Month(string symbol = "NSE:NIFTY50-INDEX",
            int year = 0, int month = 0, decimal retracement = 50m,
            decimal entryBuffer = 5m, decimal slBuffer = 5m, decimal rr = 3m,
            string refStart = "", bool trailing = true)
        {
            if (year == 0) year = DateTime.Now.Year;
            if (month == 0) month = DateTime.Now.Month;

            var config = new StrategyConfig
            {
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr,
                UseTrailing = trailing,
                UseSquareOff = false   // raw index edge-exploration — 2:45 clip nahi
            };
            config.SetRetracementFromPercentage(retracement);

            TimeSpan? onlyRef = TimeSpan.TryParse(refStart, out var ts) ? ts : (TimeSpan?)null;

            var vm = new StrategyMonthViewModel
            {
                Symbol = symbol,
                Year = year,
                Month = month,
                RetracementPercent = config.RetracementPercent,
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr
            };

            var token = _auth.GetValidToken();
            if (token == null)
            {
                vm.Error = "Koi valid token nahi. Pehle /Auth par jaakar login karo.";
                return View(vm);
            }

            var firstDay = new DateTime(year, month, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);
            if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date; // future din skip

            try
            {
                for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                {
                    // weekend skip (chhutti/holiday empty data se apne aap skip ho jayega)
                    if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var candles = await _history.GetCandlesAsync(symbol, "3", day, token.AccessToken);
                    if (candles.Count == 0) continue; // holiday / no data

                    var res = StrategyBacktester.Run(candles, config, onlyRef);
                    vm.Days.Add(new StrategyMonthDayRow
                    {
                        Date = day,
                        BuyCount = res.BuySignals.Count,
                        BuyTargetHits = res.BuyTargetHits,
                        BuyTrailStops = res.BuyTrailStops,
                        BuyStopLossHits = res.BuyStopLossHits,
                        BuyOpen = res.BuyOpen,
                        SellCount = res.SellSignals.Count,
                        SellTargetHits = res.SellTargetHits,
                        SellTrailStops = res.SellTrailStops,
                        SellStopLossHits = res.SellStopLossHits,
                        SellOpen = res.SellOpen
                    });

                    await Task.Delay(120); // rate-limit safety
                }
            }
            catch (Exception ex)
            {
                vm.Error = "Data laane me error: " + ex.Message;
            }

            return View(vm);
        }

        // GET /Strategy/AllRefs?symbol=NSE:NIFTY50-INDEX&year=2026&month=7
        // Mahine me HAR 30-min reference ka summary ek table me (har din ka data ek baar fetch).
        [HttpGet]
        public async Task<IActionResult> AllRefs(string symbol = "NSE:NIFTY50-INDEX",
            int year = 0, int month = 0, decimal retracement = 50m,
            decimal entryBuffer = 5m, decimal slBuffer = 5m, decimal rr = 3m, bool trailing = true)
        {
            if (year == 0) year = DateTime.Now.Year;
            if (month == 0) month = DateTime.Now.Month;

            var config = new StrategyConfig
            {
                EntryBufferPoints = entryBuffer,
                StopLossBufferPoints = slBuffer,
                RiskRewardRatio = rr,
                UseTrailing = trailing,
                UseSquareOff = false   // raw index edge-exploration — 2:45 clip nahi
            };
            config.SetRetracementFromPercentage(retracement);

            var vm = new StrategyAllRefsViewModel
            {
                Symbol = symbol,
                Year = year,
                Month = month,
                RetracementPercent = config.RetracementPercent
            };

            var token = _auth.GetValidToken();
            if (token == null)
            {
                vm.Error = "Koi valid token nahi. Pehle /Auth par jaakar login karo.";
                return View(vm);
            }

            // saare 30-min references (09:15 se 14:45 tak, monitoring time ke saath)
            var refStarts = new List<TimeSpan>();
            for (var t = new TimeSpan(9, 15, 0); t <= new TimeSpan(14, 45, 0); t = t.Add(TimeSpan.FromMinutes(30)))
                refStarts.Add(t);
            var acc = refStarts.ToDictionary(t => t, t => new StrategyRefRow { RefStart = t });

            var firstDay = new DateTime(year, month, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);
            if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

            try
            {
                for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                {
                    if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var candles = await _history.GetCandlesAsync(symbol, "3", day, token.AccessToken);
                    if (candles.Count == 0) continue; // holiday
                    vm.TradingDays++;

                    // ek hi din ke data par saare references chalao
                    foreach (var t in refStarts)
                    {
                        var res = StrategyBacktester.Run(candles, config, t);
                        var row = acc[t];
                        row.BuyCount += res.BuySignals.Count;
                        row.BuyTgt += res.BuyTargetHits;
                        row.BuyTrail += res.BuyTrailStops;
                        row.BuySL += res.BuyStopLossHits;
                        row.BuyOpen += res.BuyOpen;
                        row.SellCount += res.SellSignals.Count;
                        row.SellTgt += res.SellTargetHits;
                        row.SellTrail += res.SellTrailStops;
                        row.SellSL += res.SellStopLossHits;
                        row.SellOpen += res.SellOpen;
                    }

                    await Task.Delay(120);
                }
            }
            catch (Exception ex)
            {
                vm.Error = "Data laane me error: " + ex.Message;
            }

            vm.Rows = acc.Values.OrderBy(r => r.RefStart).ToList();
            return View(vm);
        }

        // GET /Strategy/Portfolio -> day-of-week filter ke saath multi-leg portfolio
        // Nifty: Thu/Fri/Mon @1:2 ; SENSEX: Mon/Tue/Wed @1:3 ; April-July, saare references.
        [HttpGet]
        public async Task<IActionResult> Portfolio(int year = 2026, int fromMonth = 4, int toMonth = 7,
            string refs = "10:45,11:15,12:45")
        {
            var vm = new StrategyPortfolioViewModel { Period = $"{fromMonth}/{year} - {toMonth}/{year}" };

            var token = _auth.GetValidToken();
            if (token == null)
            {
                vm.Error = "Koi valid token nahi. Pehle /Auth par jaakar login karo.";
                return View(vm);
            }

            // sirf chuni hui robust references
            var refStarts = refs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => TimeSpan.TryParse(s.Trim(), out var ts) ? ts : (TimeSpan?)null)
                .Where(ts => ts != null).Select(ts => ts!.Value).ToList();
            vm.Period += $" | refs: {refs}";

            // legs: symbol, allowed weekdays, RR
            var legs = new[]
            {
                new { Name = "Nifty (Thu/Fri/Mon, 1:2)", Symbol = "NSE:NIFTY50-INDEX", RR = 2m,
                      Days = new[] { DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Monday } },
                new { Name = "SENSEX (Mon/Tue/Wed, 1:3)", Symbol = "BSE:SENSEX-INDEX", RR = 3m,
                      Days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday } },
            };

            try
            {
                foreach (var leg in legs)
                {
                    var config = new StrategyConfig
                    {
                        EntryBufferPoints = 0m,
                        StopLossBufferPoints = 0m,
                        RiskRewardRatio = leg.RR,
                        UseTrailing = false
                    };
                    config.SetRetracementFromPercentage(50m);

                    var row = new PortfolioLegRow { Name = leg.Name, RR = leg.RR };

                    for (int m = fromMonth; m <= toMonth; m++)
                    {
                        var firstDay = new DateTime(year, m, 1);
                        var lastDay = firstDay.AddMonths(1).AddDays(-1);
                        if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

                        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                        {
                            if (!leg.Days.Contains(day.DayOfWeek)) continue;

                            var candles = await _history.GetCandlesAsync(leg.Symbol, "3", day, token.AccessToken);
                            if (candles.Count == 0) continue; // holiday
                            row.Days++;

                            foreach (var t in refStarts)
                            {
                                var res = StrategyBacktester.Run(candles, config, t);
                                row.Trades += res.BuySignals.Count + res.SellSignals.Count;
                                row.Target += res.BuyTargetHits + res.SellTargetHits;
                                row.Trail += res.BuyTrailStops + res.SellTrailStops;
                                row.SL += res.BuyStopLossHits + res.SellStopLossHits;
                                row.TimeExit += res.BuyTimeExits + res.SellTimeExits;
                                row.TimeExitR += res.BuyTimeExitR + res.SellTimeExitR;
                                row.TimeExitWins += res.TimeExitWins;
                                row.TimeExitLosses += res.TimeExitLosses;
                                row.Open += res.BuyOpen + res.SellOpen;
                            }
                            await Task.Delay(80);
                        }
                    }

                    vm.Legs.Add(row);
                }
            }
            catch (Exception ex)
            {
                vm.Error = "Data laane me error: " + ex.Message;
            }

            return View(vm);
        }

        // GET /Strategy/Options -> poori strategy OPTION chart pe (ATM, monthly expiry)
        // Index se sirf: ATM strike (reference start ke spot se) + day-of-week filter.
        // CE = bullish (long), PE = bearish (long) — dono option KHARIDNA.
        [HttpGet]
        public async Task<IActionResult> Options(int year = 2026, int fromMonth = 7, int toMonth = 7,
            string refs = "10:45,11:15,12:45", decimal niftyRR = 2m, decimal sensexRR = 3m,
            decimal bankRR = 3m, bool sensexOnly = false, bool allDays = false,
            string dailyStop = "none", string only = "", bool save = false)
        {
            var vm = new StrategyOptionsViewModel { Period = $"{fromMonth}/{year}-{toMonth}/{year} | refs {refs}" };

            // Token nahi to bhi chalega agar data cached hai (past dates cache se aate hain).
            // Cache-miss par hi live token chahiye hoga.
            var token = _auth.GetValidToken();
            string accessToken = token?.AccessToken ?? "";

            var refStarts = refs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => TimeSpan.TryParse(s.Trim(), out var ts) ? ts : (TimeSpan?)null)
                .Where(ts => ts != null).Select(ts => ts!.Value).ToList();

            var allWeek = new[]{DayOfWeek.Monday,DayOfWeek.Tuesday,DayOfWeek.Wednesday,DayOfWeek.Thursday,DayOfWeek.Friday};
            var legs = new[]
            {
                new { Code="nifty", Name=$"Nifty CE/PE (Thu/Fri/Mon,1:{niftyRR})", Index="NSE:NIFTY50-INDEX", OptRoot="NSE:NIFTY",
                      RR=niftyRR, Step=50m, Days=new[]{DayOfWeek.Thursday,DayOfWeek.Friday,DayOfWeek.Monday} },
                new { Code="sensex", Name=$"SENSEX CE/PE ({(allDays ? "all days" : "Mon/Tue/Wed")},1:{sensexRR})", Index="BSE:SENSEX-INDEX", OptRoot="BSE:SENSEX",
                      RR=sensexRR, Step=100m,
                      Days = allDays ? allWeek : new[]{DayOfWeek.Monday,DayOfWeek.Tuesday,DayOfWeek.Wednesday} },
                new { Code="bank", Name=$"BankNifty CE/PE (all days,1:{bankRR})", Index="NSE:NIFTYBANK-INDEX", OptRoot="NSE:BANKNIFTY",
                      RR=bankRR, Step=100m, Days=allWeek },
            };

            try
            {
                foreach (var leg in legs)
                {
                    if (sensexOnly && leg.Code != "sensex") continue;
                    if (!string.IsNullOrEmpty(only) && leg.Code != only) continue;
                    var config = new StrategyConfig { EntryBufferPoints=0m, StopLossBufferPoints=0m, RiskRewardRatio=leg.RR, UseTrailing=false };
                    config.SetRetracementFromPercentage(50m);
                    var row = new PortfolioLegRow { Name = leg.Name, RR = leg.RR };

                    for (int m = fromMonth; m <= toMonth; m++)
                    {
                        var firstDay = new DateTime(year, m, 1);
                        var lastDay = firstDay.AddMonths(1).AddDays(-1);
                        if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

                        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                        {
                            if (!leg.Days.Contains(day.DayOfWeek)) continue;

                            var idx = await _history.GetCandlesAsync(leg.Index, "3", day, accessToken);
                            if (idx.Count == 0) continue; // holiday
                            row.Days++;

                            string yy = day.ToString("yy");
                            string mmm = day.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpper();

                            // is din ke saare trades pehle collect karo (rule apply karne ke liye)
                            var dayTrades = new List<(TradeSignal s, string sym, string cepe)>();
                            foreach (var rs in refStarts)
                            {
                                var monStart = rs.Add(TimeSpan.FromMinutes(30));
                                var spotCandle = idx.FirstOrDefault(c => c.StartTime.TimeOfDay == monStart);
                                if (spotCandle == null) continue;
                                decimal spot = spotCandle.Open;
                                long strike = (long)(Math.Round(spot / leg.Step) * leg.Step);

                                foreach (var cepe in new[] { "CE", "PE" })
                                {
                                    string sym = $"{leg.OptRoot}{yy}{mmm}{strike}{cepe}";
                                    var opt = await _history.GetCandlesAsync(sym, "3", day, accessToken);
                                    await Task.Delay(250);
                                    if (opt.Count == 0) continue;

                                    var res = StrategyBacktester.Run(opt, config, rs);
                                    foreach (var s in res.BuySignals)
                                        dayTrades.Add((s, sym, cepe));
                                }
                            }

                            // entry time ke order me, phir daily-stop rule apply
                            dayTrades = dayTrades.OrderBy(x => x.s.EntryTime).ToList();
                            DateTime? stopAfter = null; // is time ke baad koi nayi entry nahi
                            bool hadTarget = false;
                            // same contract par ek waqt me ek hi position — overlap skip
                            var openUntil = new Dictionary<string, DateTime>();

                            foreach (var (s, sym, cepe) in dayTrades)
                            {
                                if (stopAfter != null && s.EntryTime >= stopAfter) continue; // rule se skip
                                // ek underlying+direction pe ek waqt me ek hi position — adjacent
                                // strike (77400PE vs 77500PE same time) bhi duplicate mano.
                                string dkey = leg.OptRoot + "|" + cepe;
                                if (openUntil.TryGetValue(dkey, out var busyTill) && s.EntryTime <= busyTill) continue;

                                // trade liya — is underlying+side ko exit (ya EOD) tak busy maano
                                openUntil[dkey] = s.OutcomeTime ?? day.AddHours(15).AddMinutes(30);
                                row.Trades++;
                                decimal r = s.RealizedR;   // TargetHit=RR, SL=-1, TimeExit=actual, Open=0
                                if (s.Outcome == TradeOutcome.TargetHit) row.Target++;
                                else if (s.Outcome == TradeOutcome.StopLossHit) row.SL++;
                                else if (s.Outcome == TradeOutcome.TimeExit)
                                {
                                    row.TimeExit++; row.TimeExitR += r;
                                    if (r > 0) row.TimeExitWins++; else if (r < 0) row.TimeExitLosses++;
                                }
                                else row.Open++;

                                if (save)
                                    try { _db.SaveSignal(sym, cepe, s.Reference.StartTime.ToString("HH:mm"), s, "backtest"); }
                                    catch { }

                                vm.Trades.Add(new OptionTradeRow
                                {
                                    Date = day.ToString("dd-MMM ddd"),
                                    Symbol = sym, Side = cepe,
                                    EntryTime = s.EntryTime.ToString("HH:mm"),
                                    EntryPrem = Math.Round(s.EntryPrice, 2),
                                    SL = Math.Round(s.StopLoss, 2),
                                    Target = Math.Round(s.Target, 2),
                                    Outcome = s.Outcome.ToString(), R = r,
                                    PnlPts = Math.Round((s.OutcomePrice ?? s.EntryPrice) - s.EntryPrice, 2)
                                });

                                // rule: kab din band karein
                                if (s.Outcome == TradeOutcome.TargetHit)
                                {
                                    hadTarget = true;
                                    if (dailyStop == "firstTarget")   // pehla target -> band
                                        stopAfter = s.OutcomeTime ?? s.EntryTime;
                                }
                                else if (s.Outcome == TradeOutcome.StopLossHit)
                                {
                                    if (dailyStop == "targetSL" && hadTarget) // target ke baad SL -> band
                                        stopAfter = s.OutcomeTime ?? s.EntryTime;
                                }
                            }
                        }
                    }
                    vm.Legs.Add(row);
                }
            }
            catch (Exception ex)
            {
                vm.Error = "Error: " + ex.Message;
            }

            return View(vm);
        }

        // GET /Strategy/Weekday -> kaunsa weekday har mahine acha (index, all references)
        [HttpGet]
        public async Task<IActionResult> Weekday(string symbol = "NSE:NIFTYBANK-INDEX",
            int year = 2026, int fromMonth = 4, int toMonth = 7, decimal rr = 3m,
            string refs = "10:45,11:15,12:45")
        {
            var vm = new StrategyWeekdayViewModel { Symbol = symbol, RR = rr };

            var token = _auth.GetValidToken();
            if (token == null) { vm.Error = "Koi valid token nahi. /Auth par login karo."; return View(vm); }

            var refStarts = refs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => TimeSpan.TryParse(s.Trim(), out var ts) ? ts : (TimeSpan?)null)
                .Where(ts => ts != null).Select(ts => ts!.Value).ToList();

            var config = new StrategyConfig { EntryBufferPoints = 0m, StopLossBufferPoints = 0m, RiskRewardRatio = rr, UseTrailing = false, UseSquareOff = false };
            config.SetRetracementFromPercentage(50m);

            var order = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            var rows = order.ToDictionary(d => d, d => new WeekdayRow { Day = d.ToString().Substring(0, 3) });

            try
            {
                for (int m = fromMonth; m <= toMonth; m++)
                {
                    vm.Months.Add(m);
                    var firstDay = new DateTime(year, m, 1);
                    var lastDay = firstDay.AddMonths(1).AddDays(-1);
                    if (lastDay > DateTime.Now.Date) lastDay = DateTime.Now.Date;

                    for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                    {
                        if (!order.Contains(day.DayOfWeek)) continue;
                        var candles = await _history.GetCandlesAsync(symbol, "3", day, token.AccessToken);
                        if (candles.Count == 0) continue;

                        int tgt = 0, sl = 0, open = 0;
                        foreach (var t in refStarts)
                        {
                            var res = StrategyBacktester.Run(candles, config, t);
                            tgt += res.BuyTargetHits + res.SellTargetHits;
                            sl += res.BuyStopLossHits + res.SellStopLossHits;
                            open += res.BuyOpen + res.SellOpen;
                        }
                        var row = rows[day.DayOfWeek];
                        row.Trades += tgt + sl + open;
                        row.Target += tgt; row.SL += sl; row.Open += open;
                        decimal net = tgt * rr - sl;
                        row.MonthNet[m] = (row.MonthNet.TryGetValue(m, out var cur) ? cur : 0) + net;
                        await Task.Delay(40);
                    }
                }
                vm.Rows = order.Select(d => rows[d]).ToList();
            }
            catch (Exception ex) { vm.Error = "Error: " + ex.Message; }

            return View(vm);
        }

        // GET /Strategy/Dashboard -> live signals dashboard (auto-refresh)
        [HttpGet]
        public IActionResult Dashboard(string mode = "all", bool portfolio = true)
        {
            var now = DateTime.Now;
            var dash = new DashboardViewModel
            {
                Now = now,
                MarketOpen = now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday
                             && now.TimeOfDay >= new TimeSpan(9, 15, 0) && now.TimeOfDay <= new TimeSpan(15, 35, 0),
                TokenValid = _auth.GetValidToken() != null,
                ModeFilter = (mode == "live" || mode == "backtest") ? mode : "all",
                UsePortfolio = portfolio
            };
            try
            {
                dash.Signals = _db.GetRecentSignals(300);
                dash.DataFreshness = _db.LatestCandleTimes();
                dash.BotPositions = _mgr.Positions
                    .OrderByDescending(p => p.EntryTime).ToList();   // paper bot live positions
            }
            catch (Exception ex) { dash.Error = "DB error: " + ex.Message; }
            return View(dash);
        }

        // GET /Strategy/DbStatus -> DB me kitna data hai
        [HttpGet]
        public IActionResult DbStatus()
        {
            try { return Content(_db.Status(), "text/plain"); }
            catch (Exception ex) { return Content("DB error: " + ex.Message, "text/plain"); }
        }

        // GET /Strategy/ClearSignals?mode=backtest&apply=false -> us mode ke saare signals delete
        // (fresh re-run se pehle clean slate — stale post-2:45 / purane outcome rows hat jayen)
        [HttpGet]
        public IActionResult ClearSignals(string mode = "backtest", bool apply = false)
        {
            var all = _db.GetRecentSignals(100000).Where(s => s.Mode == mode).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Mode '{mode}' signals: {all.Count}   (apply={apply})");
            if (apply)
            {
                int n = 0;
                foreach (var s in all) n += _db.DeleteSignal(s.Symbol, s.Side, s.EntryTime);
                sb.AppendLine($"{n} rows DELETED. Ab /Strategy/Options?...&save=true se fresh bharo.");
            }
            else
            {
                sb.AppendLine("Preview only. Delete karne ke liye: ?apply=true");
            }
            return Content(sb.ToString(), "text/plain");
        }

        // GET /Strategy/CleanOverlaps?apply=false -> DB me pade overlap signals dhoondo/hata do
        // Rule: same contract par jab tak pichhla trade open hai (same 3-min candle bhi), naya = overlap.
        // Dashboard sirf display pe hide karta hai; yeh action DB se sach me delete karta hai.
        [HttpGet]
        public IActionResult CleanOverlaps(bool apply = false)
        {
            var all = _db.GetRecentSignals(100000);
            var toRemove = new List<Models.DashSignal>();
            // underlying+side (exact strike nahi) — adjacent-strike same-side duplicate bhi pakdo
            foreach (var g in all.GroupBy(s => s.DedupKey))
            {
                DateTime? openUntil = null;
                foreach (var s in g.OrderBy(x => x.EntryTime))
                {
                    if (openUntil != null && s.EntryTime <= openUntil.Value)
                        toRemove.Add(s);              // pichhla abhi open tha -> duplicate
                    else
                        openUntil = s.ExitTime;       // ab yeh position exit (ya EOD) tak busy
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Total signals in DB: {all.Count}");
            sb.AppendLine($"Overlap signals: {toRemove.Count}   (apply={apply})");
            sb.AppendLine(new string('-', 60));
            foreach (var s in toRemove.OrderBy(s => s.Symbol).ThenBy(s => s.EntryTime))
                sb.AppendLine($"{(apply ? "DELETED " : "would remove")}: {s.CleanSymbol} {s.Side} " +
                              $"{s.EntryTime:dd-MMM HH:mm} [{s.Mode}] {s.Outcome}");

            if (apply)
            {
                int n = 0;
                foreach (var s in toRemove) n += _db.DeleteSignal(s.Symbol, s.Side, s.EntryTime);
                sb.AppendLine(new string('-', 60));
                sb.AppendLine($"{n} rows DELETED from dbo.Signals.");
            }
            else
            {
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("Yeh sirf preview hai. Actually delete karne ke liye: /Strategy/CleanOverlaps?apply=true");
            }
            return Content(sb.ToString(), "text/plain");
        }

        // GET /Strategy/BotSeedDemo -> shared PositionManager me sample positions daalo (dashboard UI preview).
        // Sirf demo/preview — app restart se clear ho jayenge. Live se pehle restart karein.
        [HttpGet]
        public async Task<IActionResult> BotSeedDemo()
        {
            var day = DateTime.Now.Date;
            DateTime T(int h, int m) => day.AddHours(h).AddMinutes(m);

            async Task Seed(string sym, string side, decimal e, decimal sl, decimal tg,
                DateTime entryTime, (decimal h, decimal l, decimal last)[] marks)
            {
                await _mgr.OnSignal(sym, side, 75, e, sl, tg, entryTime);
                foreach (var m in marks) { _paper.MarkPrice(sym, m.h, m.l, m.last, DateTime.Now); await _mgr.Tick(DateTime.Now); }
            }

            // target hit
            await Seed("NSE:NIFTY26JUL24000CE", "CE", 100m, 92m, 116m, T(10, 45),
                new[] { (100.5m, 99m, 100m), (118m, 110m, 117m) });
            // SL hit
            await Seed("BSE:SENSEX26JUL77000PE", "PE", 120m, 108m, 156m, T(11, 15),
                new[] { (121m, 110m, 115m), (112m, 105m, 106m) });
            // abhi OPEN (entry filled, koi exit nahi)
            await Seed("NSE:BANKNIFTY26JUL57000CE", "CE", 90m, 82m, 114m, T(12, 45),
                new[] { (91m, 89m, 90m) });

            return Redirect("/Strategy/Dashboard");
        }

        // GET /Strategy/PaperDemo -> exit-management ka poora flow (paper mode) prove karo:
        // teeno paths — Target hit, SL hit, aur 2:45 square-off. Market band hone par bhi chalta hai.
        [HttpGet]
        public async Task<IActionResult> PaperDemo()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("PAPER EXIT-MANAGEMENT DEMO — koi REAL order nahi. Manual-OCO: entry -> SL-M + target, watcher manage.");
            report.AppendLine(new string('=', 88));

            DateTime D(int h, int m) => new DateTime(2026, 7, 27, h, m, 0);

            // Scenario 1: Target hit (1:2) — premium chadhti hai
            await RunPaperScenario(report, "1) TARGET HIT (1:2)", "NSE:NIFTY26JUL24000CE", "CE", 75, 100m, 92m, 116m, new[]
            {
                (D(11,33), 100.5m, 99m, 100m),   // buy fill @100 -> OPEN (SL-M 92, target 116)
                (D(11,36), 108m, 101m, 107m),    // kuch nahi
                (D(11,39), 118m, 110m, 117m),    // high 118 >= 116 -> target fill, SL cancel
            });

            // Scenario 2: SL hit — premium girti hai
            await RunPaperScenario(report, "2) STOP-LOSS HIT (SL-M)", "NSE:NIFTY26JUL24000PE", "PE", 75, 100m, 92m, 116m, new[]
            {
                (D(11,33), 100.5m, 99m, 100m),   // buy fill -> OPEN
                (D(11,36), 102m, 96m, 98m),      // low 96 > 92, safe
                (D(11,39), 99m, 90m, 91m),       // low 90 <= 92 -> SL-M fill @92, target cancel
            });

            // Scenario 3: 2:45 square-off — na target na SL
            await RunPaperScenario(report, "3) 2:45 SQUARE-OFF (TimeExit)", "BSE:SENSEX26JUL77000CE", "CE", 10, 100m, 92m, 116m, new[]
            {
                (D(13,30), 100.5m, 99m, 100m),   // buy fill -> OPEN
                (D(14,0),  110m, 96m, 104m),     // kuch nahi
                (D(14,45), 106m, 100m, 103m),    // 2:45 -> dono cancel + market sell (fill @103)
            });

            return Content(report.ToString(), "text/plain");
        }

        private async Task RunPaperScenario(System.Text.StringBuilder sb, string name, string sym, string side,
            int qty, decimal entry, decimal sl, decimal target, (DateTime t, decimal h, decimal l, decimal last)[] candles)
        {
            var cap = new System.Text.StringBuilder();
            var logger = new CaptureLogger(cap);
            var broker = new PaperBrokerOrders(logger);
            var mgr = new PositionManager(broker, logger, new TimeSpan(14, 45, 0));

            await mgr.OnSignal(sym, side, qty, entry, sl, target, candles[0].t);
            foreach (var c in candles)
            {
                broker.MarkPrice(sym, c.h, c.l, c.last, c.t);
                await mgr.Tick(c.t);
            }
            // ek extra tick — 2:45 market-sell ka fill catch karne ke liye
            var lc = candles[^1];
            broker.MarkPrice(sym, lc.h, lc.l, lc.last, lc.t);
            await mgr.Tick(lc.t);

            var p = mgr.Positions[0];
            sb.AppendLine();
            sb.AppendLine($"{name}   [{sym} entry {entry} SL {sl} target {target}]");
            sb.AppendLine(new string('-', 88));
            foreach (var line in cap.ToString().TrimEnd().Split('\n'))
                sb.AppendLine("   " + line.TrimEnd());
            sb.AppendLine($"   >> FINAL: state={p.State}  outcome={p.Outcome}  exit={p.ExitPrice}  R={p.RealizedR:+0.##;-0.##}");
            sb.AppendLine();
        }

        // demo ke logs capture karne ke liye chhota logger
        private sealed class CaptureLogger : ILogger
        {
            private readonly System.Text.StringBuilder _sb;
            public CaptureLogger(System.Text.StringBuilder sb) { _sb = sb; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
                => _sb.AppendLine(fmt(state, ex));
        }

        // GET /Strategy/Demo  -> fixed sample (data ke bina bhi logic check)
        [HttpGet]
        public IActionResult Demo()
        {
            var day = new DateTime(2026, 7, 27);
            DateTime T(int h, int m) => day.AddHours(h).AddMinutes(m);

            var manager = new StrategyManager(new StrategyConfig { RetracementPercent = 0.50m });
            var log = new List<object>();
            manager.OnSignal += s => log.Add(new { Ev = "BUY", Signal = s.ToString() });

            manager.OnThirtyMinuteCandleClosed(new Candle(T(9, 15), T(9, 45), 82, 100, 80, 95));

            var threeMin = new[]
            {
                new Candle(T(9,45), T(9,48), 96, 98,  95, 97),
                new Candle(T(9,48), T(9,51), 98, 101, 97, 100),
                new Candle(T(9,54), T(9,57), 99, 99,  89, 90),
                new Candle(T(10,0), T(10,3), 96, 102, 95, 101),
            };
            foreach (var c in threeMin)
            {
                var r = manager.OnThreeMinuteCandleClosed(c);
                log.Add(new { Time = c.StartTime.ToString("HH:mm"), State = r.State.ToString(), r.Message });
            }
            return Json(new { FinalState = manager.State.ToString(), Steps = log });
        }

        // IndexSim ke liye: TradeSignal ko portfolio selector (IPortfolioTrade) me adapt karta hai
        private sealed class IndexTrade : IPortfolioTrade
        {
            public TradeSignal Sig { get; }
            public IndexTrade(TradeSignal s) { Sig = s; }
            public DateTime EntryTime => Sig.EntryTime;
            public DateTime ExitTime => Sig.OutcomeTime ?? Sig.EntryTime.Date.AddHours(15).AddMinutes(30);
            public int Priority => 0;   // single symbol -> priority moot
            public bool IsStopLoss => Sig.Outcome == TradeOutcome.StopLossHit;
        }

        // PortfolioSim ke liye: cross-index trade with settable priority + leg label (+ option symbol)
        private sealed class PfTrade : IPortfolioTrade
        {
            public TradeSignal Sig { get; }
            public string Name { get; }
            public string Sym { get; }
            public TimeSpan Ref { get; }
            public PfTrade(TradeSignal s, int prio, string name, TimeSpan refv, string sym = "") { Sig = s; Priority = prio; Name = name; Ref = refv; Sym = sym; }
            public DateTime EntryTime => Sig.EntryTime;
            public DateTime ExitTime => Sig.OutcomeTime ?? Sig.EntryTime.Date.AddHours(15).AddMinutes(30);
            public int Priority { get; }
            public bool IsStopLoss => Sig.Outcome == TradeOutcome.StopLossHit;
        }
    }
}
