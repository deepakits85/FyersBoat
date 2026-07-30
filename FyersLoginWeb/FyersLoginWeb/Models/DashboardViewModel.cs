using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FyersLoginWeb.Strategy;

namespace FyersLoginWeb.Models
{
    public class DashSignal : IPortfolioTrade
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public string RefWindow { get; set; } = "";
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Target { get; set; }
        public decimal RR { get; set; }
        public string Outcome { get; set; } = "";
        public DateTime? OutcomeTime { get; set; }
        public decimal? OutcomePrice { get; set; }
        public string Mode { get; set; } = "";

        // same contract pe pichhla trade abhi open tha jab yeh enter hua → skip (real me nahi lete)
        public bool IsOverlapSkipped { get; set; }

        // portfolio execution: ek waqt me ek hi trade — jo skip hua
        public bool PortfolioSkipped { get; set; }
        public string PortfolioSkipReason { get; set; } = "";   // "busy" ya "max2SL"

        // priority: Nifty(0) > SENSEX(1) > BankNifty(2) — same-time tie-break ke liye
        public int Priority
        {
            get
            {
                var u = Underlying.ToUpperInvariant();
                if (u.Contains("BANKNIFTY")) return 2;
                if (u.Contains("NIFTY")) return 0;
                if (u.Contains("SENSEX")) return 1;
                return 9;
            }
        }

        // is trade ka exit time — closed hai to OutcomeTime, warna EOD (15:30) tak "open" maano
        public DateTime ExitTime => OutcomeTime ?? EntryTime.Date.AddHours(15).AddMinutes(30);

        // underlying (strike/expiry hata ke): "BSE:SENSEX26JUL77400PE" -> "BSE:SENSEX"
        public string Underlying => Regex.Replace(Symbol, @"\d{2}[A-Z]{3}\d+(CE|PE)$", "");
        // dedup key: ek underlying + ek direction (CE/PE) = ek position ek waqt me
        // (adjacent strike 77400 vs 77500 same-side ko bhi ek hi maano).
        public string DedupKey => Underlying + "|" + Side;

        public decimal PnlPts => Math.Round(((OutcomePrice ?? EntryPrice) - EntryPrice), 2);

        // R-multiple: outcome normalize karta hai taaki alag-alag symbol/premium fair compare ho.
        // TargetHit => +RR, StopLossHit => -1, TimeExit => actual (exit-entry)/risk, Open/other => 0.
        public decimal RMultiple
        {
            get
            {
                if (Outcome == "TargetHit") return RR;
                if (Outcome == "StopLossHit") return -1m;
                if (Outcome == "TimeExit" && OutcomePrice.HasValue)
                {
                    decimal risk = EntryPrice - StopLoss;   // long premium (CE/PE dono buy)
                    return risk == 0 ? 0 : Math.Round((OutcomePrice.Value - EntryPrice) / risk, 2);
                }
                return 0m;
            }
        }
        public bool IsClosed => Outcome == "TargetHit" || Outcome == "StopLossHit" || Outcome == "TimeExit";

        // IPortfolioTrade: max-2-SL count ke liye
        public bool IsStopLoss => Outcome == "StopLossHit";

        public string CleanSymbol => Symbol.Replace("NSE:", "").Replace("BSE:", "");
    }

    public class SymbolStat
    {
        public string Symbol { get; set; } = "";        // clean (display)
        public string FullSymbol { get; set; } = "";    // NSE:/BSE: prefixed (Day link)
        public string DetailDate { get; set; } = "";    // yyyy-MM-dd (latest signal ka din)
        public string RefStart { get; set; } = "";      // latest signal ka ref window
        public decimal Rr { get; set; }                 // latest signal ka RR
        public int Count { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int ClosedCount { get; set; }               // target+SL+timeexit (decided)
        public decimal NetR { get; set; }
        public int Decided => ClosedCount;
        public int OpenCount => Count - Decided;
        // Win% ab TOTAL trades par (open bhi denominator me) — "decided X/Y" alag se dikhता hai
        public int WinRate => Count == 0 ? 0 : (int)Math.Round(100.0 * Wins / Count);

        // /Strategy/Day link jo is symbol ke apne 3-min chart ka detail dikhata hai.
        // IMPORTANT: signals buffer 0/0, retr 50, trailing off se generate/save hote hain
        // (Options backtest + LiveTradingService dono) — isliye wahi config pass karo,
        // warna Day default buf 5/5 se dobara chala kar ALAG entry/SL/target dikhega.
        public string DetailUrl =>
            $"/Strategy/Day?symbol={Uri.EscapeDataString(FullSymbol)}&date={DetailDate}" +
            $"&rr={Rr}&refStart={RefStart}&entryBuffer=0&slBuffer=0&retracement=50&trailing=false";
    }

    public class DashboardViewModel
    {
        public string? Error { get; set; }
        public bool MarketOpen { get; set; }
        public DateTime Now { get; set; }
        public bool TokenValid { get; set; }
        public string ModeFilter { get; set; } = "all";        // all | live | backtest
        public List<DashSignal> Signals { get; set; } = new();  // raw loaded (recent N, all modes)
        public List<(string sym, DateTime last)> DataFreshness { get; set; } = new();

        // paper bot ki live positions (LiveTradingService ke PositionManager se)
        public List<FyersLoginWeb.Services.Broker.ManagedPosition> BotPositions { get; set; } = new();

        // selected mode ke hisaab se filtered set
        public List<DashSignal> Filtered => ModeFilter == "all"
            ? Signals
            : Signals.Where(s => s.Mode == ModeFilter).ToList();

        // overlap detection: har symbol par jab tak pichhla trade open hai, us dauran
        // enter hua naya trade "skip" mark hota hai (ek contract pe ek hi position).
        private void ComputeOverlaps(List<DashSignal> list)
        {
            // group by underlying+side (exact strike nahi) — taaki same-direction adjacent
            // strikes (77400PE vs 77500PE same time) bhi ek hi position mane jayen.
            foreach (var g in list.GroupBy(s => s.DedupKey))
            {
                DateTime? openUntil = null;
                foreach (var s in g.OrderBy(x => x.EntryTime))
                {
                    if (openUntil != null && s.EntryTime <= openUntil.Value)
                    {
                        s.IsOverlapSkipped = true;   // pichhla abhi chal raha tha (same candle bhi overlap)
                    }
                    else
                    {
                        s.IsOverlapSkipped = false;
                        openUntil = s.ExitTime;       // ab yeh position exit tak "open"
                    }
                }
            }
        }

        public bool UsePortfolio { get; set; } = true;   // ek-waqt-ek-trade + priority + max-2-SL

        // Filtered + overlap marks lagaye hue (table isko use karta hai — overlap rows dikhte hain par marked)
        public List<DashSignal> FilteredMarked
        {
            get { var f = Filtered; ComputeOverlaps(f); return f; }
        }

        // Portfolio execution rules (overlap-survivors par, per din):
        //  - ek waqt me ek hi trade (koi bhi symbol) — jab tak position open, naya skip
        //  - same-time tie -> priority Nifty > SENSEX > BankNifty
        //  - din me max 2 SL -> uske baad us din koi naya trade nahi
        private void ApplyPortfolio(List<DashSignal> overlapSurvivors)
        {
            foreach (var s in overlapSurvivors) { s.PortfolioSkipped = false; s.PortfolioSkipReason = ""; }
            if (!UsePortfolio) return;

            // SHARED selector — live decision path bhi yahi use karta hai (kabhi diverge na ho)
            var decisions = PortfolioSelector.Select(overlapSurvivors, maxSlPerDay: 4);
            foreach (var kv in decisions)
            {
                kv.Key.PortfolioSkipped = kv.Value.Skipped;
                kv.Key.PortfolioSkipReason = kv.Value.SkipReason;
            }
        }

        // overlap-survivors with portfolio marks
        public List<DashSignal> OverlapSurvivors
        {
            get
            {
                var s = FilteredMarked.Where(x => !x.IsOverlapSkipped).ToList();
                ApplyPortfolio(s);
                return s;
            }
        }

        // saare rows (overlap + portfolio marks lage hue) — recent table isko use karta hai
        public List<DashSignal> TableRows
        {
            get { var f = FilteredMarked; ApplyPortfolio(f.Where(x => !x.IsOverlapSkipped).ToList()); return f; }
        }

        // Active/Taken = jo trades ACTUALLY liye jaate hain (overlap + portfolio skip nikaal ke) — SAARI stats isi par
        public List<DashSignal> Active => OverlapSurvivors.Where(s => !s.PortfolioSkipped).ToList();
        public int OverlapSkipped => FilteredMarked.Count(s => s.IsOverlapSkipped);
        public int PortfolioSkipped => OverlapSurvivors.Count(s => s.PortfolioSkipped);

        // ---- aaj ke signals ----
        public List<DashSignal> Today => Active.Where(s => s.EntryTime.Date == Now.Date).ToList();
        public int TodayCount => Today.Count;
        public int TodayTarget => Today.Count(s => s.Outcome == "TargetHit");
        public int TodaySL => Today.Count(s => s.Outcome == "StopLossHit");
        public int TodayOpen => Today.Count(s => s.Outcome == "Open");
        public int LiveCount => Signals.Count(s => s.Mode == "live");

        // ---- aggregate performance (closed trades par, overlap skip ke bina) ----
        public List<DashSignal> Closed => Active.Where(s => s.IsClosed).ToList();
        public int ClosedCount => Closed.Count;
        // win/loss R-sign se (TargetHit +, SL −, TimeExit apne P&L ke sign se)
        public int Wins => Closed.Count(s => s.RMultiple > 0);
        public int Losses => Closed.Count(s => s.RMultiple < 0);
        public int TotalCount => Active.Count;
        public int OpenCount => Active.Count(s => s.Outcome == "Open");
        // Win% TOTAL trades par (open denominator me shaamil) — header me "decided X/Y" bhi dikhता hai
        public int WinRate => TotalCount == 0 ? 0 : (int)Math.Round(100.0 * Wins / TotalCount);
        public decimal TotalR => Math.Round(Closed.Sum(s => s.RMultiple), 2);
        public decimal Expectancy => ClosedCount == 0 ? 0 : Math.Round(TotalR / ClosedCount, 2);

        // profit factor = gross win R / gross loss R
        public decimal ProfitFactor
        {
            get
            {
                decimal grossWin = Closed.Where(s => s.RMultiple > 0).Sum(s => s.RMultiple);
                decimal grossLoss = Closed.Where(s => s.RMultiple < 0).Sum(s => -s.RMultiple);
                if (grossLoss == 0) return grossWin == 0 ? 0 : 999m;
                return Math.Round(grossWin / grossLoss, 2);
            }
        }

        // ---- equity curve: chronological cumulative R ----
        public List<decimal> EquityCurve
        {
            get
            {
                decimal cum = 0;
                var pts = new List<decimal>();
                foreach (var s in Closed.OrderBy(s => s.OutcomeTime ?? s.EntryTime))
                {
                    cum += s.RMultiple;
                    pts.Add(Math.Round(cum, 2));
                }
                return pts;
            }
        }

        // ---- per-symbol breakdown (overlap skip ke bina) ----
        public List<SymbolStat> BySymbol => Active
            .GroupBy(s => s.CleanSymbol)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.EntryTime).First();
                return new SymbolStat
                {
                    Symbol = g.Key,
                    FullSymbol = latest.Symbol,
                    DetailDate = latest.EntryTime.ToString("yyyy-MM-dd"),
                    RefStart = latest.RefWindow,
                    Rr = latest.RR,
                    Count = g.Count(),
                    Wins = g.Count(x => x.RMultiple > 0),
                    Losses = g.Count(x => x.RMultiple < 0),
                    ClosedCount = g.Count(x => x.IsClosed),
                    NetR = Math.Round(g.Sum(x => x.RMultiple), 2)
                };
            })
            .OrderByDescending(x => x.NetR)
            .ToList();
    }
}
