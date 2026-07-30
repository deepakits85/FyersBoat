using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// SIMPLE live rule — bas itna:
    ///   - Refs: 10:45 + 11:15
    ///   - Agar 10:45 ki trade abhi running hai (same index) to 11:15 skip
    ///   - Indices: Nifty + BankNifty + Sensex
    ///   - First BO: 3m CLOSE &gt; ref High (short: CLOSE &lt; ref Low)
    ///   - Retracement: &gt; 50% of ref range
    ///   - Second BO: entry on breakout touch (wick) — close wait nahi
    ///   - SL: ref Low (long) / ref High (short) | Target: 1:3
    ///   - KOI AUR RULE NAHI: no trail, prior, filters, gap, maxSL, freshness, 90m, square-off
    /// </summary>
    public static class RecommendedLiveConfig
    {
        public static readonly TimeSpan[] Refs =
        {
            new TimeSpan(10, 45, 0),
            new TimeSpan(11, 15, 0),
        };

        public static readonly TimeSpan Ref1045 = new TimeSpan(10, 45, 0);
        public static readonly TimeSpan Ref1115 = new TimeSpan(11, 15, 0);

        public const decimal NiftyRr = 3m;
        public const decimal BankRr = 3m;
        public const decimal SensexRr = 3m;
        public const decimal StrikeStepNifty = 50m;
        public const decimal StrikeStepBank = 100m;
        public const decimal StrikeStepSensex = 100m;

        // Portfolio limits OFF (user: sirf strategy rule)
        public const int MaxSlPerDay = 999;
        public const int MinGapMinutes = 0;
        public const int MaxReferenceWaitMinutes = 90;
        public const int FreshSignalMaxAgeMinutes = 999; // stale skip OFF
        public const int CandleMinutes = 3;

        public static TimeSpan SquareOffTime { get; } = new TimeSpan(14, 45, 0);

        public static EntryFilterConfig EntryFilters()
            => new EntryFilterConfig();

        /// <summary>
        /// 11:15 tabhi lo jab usi index pe 10:45 wali trade already band ho.
        /// Same-index pe 10:45 abhi open → 11:15 skip.
        /// </summary>
        public static List<T> Skip1115If1045Running<T>(
            IEnumerable<T> trades,
            Func<T, string> indexKey,
            Func<T, TimeSpan> refStart,
            Func<T, DateTime> entryTime,
            Func<T, DateTime> exitTime)
        {
            var ordered = trades.OrderBy(entryTime).ThenBy(t => refStart(t)).ToList();
            var kept = new List<T>();
            // indexKey -> exit time of last kept 10:45 trade that could still be open
            var open1045Until = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in ordered)
            {
                var key = indexKey(t);
                var rs = refStart(t);
                var ent = entryTime(t);
                var exit = exitTime(t);

                if (rs == Ref1115 &&
                    open1045Until.TryGetValue(key, out var busyTill) &&
                    ent < busyTill)
                {
                    // 10:45 still running → skip 11:15
                    continue;
                }

                kept.Add(t);
                if (rs == Ref1045)
                    open1045Until[key] = exit;
            }
            return kept;
        }

        public static string IndexKeyFromSymbol(string sym)
        {
            var u = (sym ?? "").ToUpperInvariant();
            if (u.Contains("BANKNIFTY") || u.Contains("NIFTYBANK")) return "BankNifty";
            if (u.Contains("SENSEX")) return "Sensex";
            if (u.Contains("NIFTY")) return "Nifty";
            return u;
        }

        public static StrategyConfig MakeConfig(decimal riskReward)
        {
            var cfg = new StrategyConfig
            {
                EntryBufferPoints = 0m,
                StopLossBufferPoints = 0m,
                RiskRewardRatio = riskReward,
                UseTrailing = false,
                UseSquareOff = false,
                SquareOffTime = SquareOffTime,
                UseReferenceMaxWait = false,
                FirstBreakoutRequireClose = true,
                RequireCloseConfirm = false, // 2nd BO: buy on touch
                ConfirmRetrFirstOnly = false,
                UseRetracementFirst = false,
                SoftRetracementConfirm = false,
                UsePriorLevelBreak = false,
                UseSetupFreshness = false,
                // SL = ref Low (long) / ref High (short) — full range, not mid
                StopLossRetracementPercent = 1.00m,
            };
            cfg.SetRetracementFromPercentage(50m); // confirm >50%
            return cfg;
        }

        public static List<Candle> ClosedOnly(List<Candle> candles, DateTime now)
        {
            if (candles == null || candles.Count == 0)
                return new List<Candle>();
            return candles.Where(c => c.EndTime <= now).ToList();
        }
    }
}
