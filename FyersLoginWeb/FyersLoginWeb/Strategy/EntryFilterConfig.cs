using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Data-driven entry filters (Aug 2025–Jul 2026 index study) to cut SL rate.
    ///
    /// LIVE recommended = reduce-sl-strict (see RecommendedLiveConfig):
    ///  reduce-sl:        skip 10:45 + lag≥15 + skip Tue
    ///  reduce-sl-strict: + lag≥30 + skip Sensex + min risk bps (LIVE default)
    ///  reduce-sl-ultra:  tighter min risk (fewer trades)
    ///
    /// Note: Live Legs already drop Sensex + 10:45; strict still belt-and-suspenders.
    /// </summary>
    public class EntryFilterConfig
    {
        public HashSet<TimeSpan> SkipRefStarts { get; set; } = new();
        public int MinMinutesAfterRef { get; set; } = 0;
        public HashSet<DayOfWeek> SkipWeekdays { get; set; } = new();
        public TradeSide? OnlySide { get; set; }

        /// <summary>Skip these underlyings (match against name or symbol, case-insensitive contains).</summary>
        public List<string> SkipNameContains { get; set; } = new();

        /// <summary>
        /// Minimum stop distance in bps of entry (Risk/Entry*10000).
        /// Key = name fragment ("Nifty", "BankNifty", "Sensex"); first match wins.
        /// Tight SLs (tiny risk) meanwhipped more on Nifty Q4 / Bank Q2 in the study —
        /// requiring ≥ Q1 risk avoided many quick stops.
        /// </summary>
        public Dictionary<string, decimal> MinRiskBpsByName { get; set; } = new();

        public static EntryFilterConfig ReduceStopLossPreset() => new()
        {
            SkipRefStarts = new HashSet<TimeSpan> { new TimeSpan(10, 45, 0) },
            MinMinutesAfterRef = 15,
            SkipWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Tuesday }
        };

        /// <summary>
        /// Stricter preset — best SL cut that still held up on Apr–Jul 2026 holdout
        /// (test SL% ~30, avgR ~0.65).
        /// </summary>
        public static EntryFilterConfig ReduceStopLossStrictPreset() => new()
        {
            SkipRefStarts = new HashSet<TimeSpan> { new TimeSpan(10, 45, 0) },
            MinMinutesAfterRef = 30,
            SkipWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Tuesday },
            SkipNameContains = new List<string> { "Sensex", "SENSEX" },
            // RAW Q1 riskBps from the study (slightly rounded)
            MinRiskBpsByName = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["BankNifty"] = 7.5m,
                ["NIFTYBANK"] = 7.5m,
                ["Nifty"] = 6.0m,
                ["NIFTY50"] = 6.0m,
            }
        };

        /// <summary>Ultra-tight: same as strict but risk ≥ median (~fewer trades, SL%~32).</summary>
        public static EntryFilterConfig ReduceStopLossUltraPreset() => new()
        {
            SkipRefStarts = new HashSet<TimeSpan> { new TimeSpan(10, 45, 0) },
            MinMinutesAfterRef = 30,
            SkipWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Tuesday },
            SkipNameContains = new List<string> { "Sensex", "SENSEX" },
            MinRiskBpsByName = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["BankNifty"] = 10.0m,
                ["NIFTYBANK"] = 10.0m,
                ["Nifty"] = 8.0m,
                ["NIFTY50"] = 8.0m,
            }
        };

        public bool Allows(TradeSignal signal, string? symbolOrName = null)
        {
            if (signal == null) return false;

            if (SkipRefStarts.Count > 0 &&
                SkipRefStarts.Contains(signal.Reference.StartTime.TimeOfDay))
                return false;

            if (MinMinutesAfterRef > 0)
            {
                double mins = (signal.EntryTime - signal.Reference.EndTime).TotalMinutes;
                if (mins < MinMinutesAfterRef) return false;
            }

            if (SkipWeekdays.Count > 0 &&
                SkipWeekdays.Contains(signal.EntryTime.DayOfWeek))
                return false;

            if (OnlySide != null && signal.Side != OnlySide.Value)
                return false;

            string label = symbolOrName ?? "";
            if (SkipNameContains.Count > 0 && !string.IsNullOrEmpty(label))
            {
                foreach (var frag in SkipNameContains)
                    if (label.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
            }

            if (MinRiskBpsByName.Count > 0 && signal.EntryPrice > 0)
            {
                decimal riskBps = (signal.Risk / signal.EntryPrice) * 10000m;
                decimal? min = null;
                // longer keys first so BankNifty wins over Nifty
                foreach (var kv in MinRiskBpsByName.OrderByDescending(k => k.Key.Length))
                {
                    if (label.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    { min = kv.Value; break; }
                }
                if (min != null && riskBps < min.Value)
                    return false;
            }

            return true;
        }

        public IEnumerable<TradeSignal> Apply(IEnumerable<TradeSignal> signals)
            => signals.Where(s => Allows(s));

        public override string ToString()
        {
            string refs = SkipRefStarts.Count == 0 ? "-" :
                string.Join(",", SkipRefStarts.OrderBy(t => t).Select(t => t.ToString(@"hh\:mm")));
            string days = SkipWeekdays.Count == 0 ? "-" :
                string.Join(",", SkipWeekdays.OrderBy(d => d));
            string side = OnlySide?.ToString() ?? "both";
            string skip = SkipNameContains.Count == 0 ? "-" : string.Join("|", SkipNameContains);
            string risk = MinRiskBpsByName.Count == 0 ? "-" :
                string.Join(";", MinRiskBpsByName.Select(kv => $"{kv.Key}>={kv.Value}"));
            return $"skipRefs={refs} minLag={MinMinutesAfterRef}m skipDays={days} side={side} skipNames={skip} minRiskBps={risk}";
        }
    }
}
