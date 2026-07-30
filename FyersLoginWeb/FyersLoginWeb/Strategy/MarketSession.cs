using System;
using System.Collections.Generic;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// NSE fixed 30-min reference windows: 09:15-09:45, 09:45-10:15, ... 15:00-15:30.
    /// </summary>
    public static class MarketSession
    {
        public static readonly TimeSpan SessionStart = new TimeSpan(9, 15, 0);
        public static readonly TimeSpan SessionEnd = new TimeSpan(15, 30, 0);
        public static readonly TimeSpan ReferenceSize = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Diye gaye time ke liye us 30-min reference window ka (start,end) do.
        /// Agar time market hours ke bahar hai to null.
        /// </summary>
        public static (DateTime start, DateTime end)? GetReferenceWindow(DateTime time)
        {
            var day = time.Date;
            var start = day + SessionStart;
            var end = day + SessionEnd;

            if (time < start || time >= end)
                return null;

            long minutesFromStart = (long)(time - start).TotalMinutes;
            long blocks = minutesFromStart / 30;
            var windowStart = start.AddMinutes(blocks * 30);
            var windowEnd = windowStart.Add(ReferenceSize);
            return (windowStart, windowEnd);
        }

        /// <summary>Pure din ke saare 30-min reference windows.</summary>
        public static IEnumerable<(DateTime start, DateTime end)> GetAllWindows(DateTime day)
        {
            var start = day.Date + SessionStart;
            var end = day.Date + SessionEnd;
            for (var s = start; s < end; s = s.Add(ReferenceSize))
                yield return (s, s.Add(ReferenceSize));
        }
    }
}
