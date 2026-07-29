using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Data-driven entry filters (Aug 2025–Jul 2026 index study) to cut SL rate
    /// without killing expectancy.
    ///
    /// Findings (portfolio TAKEN, same gap/maxSL rules):
    ///  - ref 10:45 had highest SL% (~57%)
    ///  - entries in first 15 min after reference close were noisier (SL ~55%)
    ///  - Tuesday was the worst weekday (SL ~59%, negative NetR)
    /// Recommended combo improved walk-forward test SL% 46%→40% and avgR 0.42→0.52.
    /// </summary>
    public class EntryFilterConfig
    {
        /// <summary>Skip these reference window starts (e.g. 10:45).</summary>
        public HashSet<TimeSpan> SkipRefStarts { get; set; } = new();

        /// <summary>
        /// Entry must be at least this many minutes after reference EndTime.
        /// 0 = off. Recommended 15.
        /// </summary>
        public int MinMinutesAfterRef { get; set; } = 0;

        /// <summary>Skip entries on these weekdays.</summary>
        public HashSet<DayOfWeek> SkipWeekdays { get; set; } = new();

        /// <summary>If set, only allow this side (Long/Short). Null = both.</summary>
        public TradeSide? OnlySide { get; set; }

        /// <summary>
        /// Recommended production preset from the full-year study.
        /// skip 10:45 + wait 15m after ref + skip Tuesday.
        /// </summary>
        public static EntryFilterConfig ReduceStopLossPreset() => new()
        {
            SkipRefStarts = new HashSet<TimeSpan> { new TimeSpan(10, 45, 0) },
            MinMinutesAfterRef = 15,
            SkipWeekdays = new HashSet<DayOfWeek> { DayOfWeek.Tuesday }
        };

        public bool Allows(TradeSignal signal)
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

            return true;
        }

        public IEnumerable<TradeSignal> Apply(IEnumerable<TradeSignal> signals)
            => signals.Where(Allows);

        public override string ToString()
        {
            string refs = SkipRefStarts.Count == 0 ? "-" :
                string.Join(",", SkipRefStarts.OrderBy(t => t).Select(t => t.ToString(@"hh\:mm")));
            string days = SkipWeekdays.Count == 0 ? "-" :
                string.Join(",", SkipWeekdays.OrderBy(d => d));
            string side = OnlySide?.ToString() ?? "both";
            return $"skipRefs={refs} minLag={MinMinutesAfterRef}m skipDays={days} side={side}";
        }
    }
}
