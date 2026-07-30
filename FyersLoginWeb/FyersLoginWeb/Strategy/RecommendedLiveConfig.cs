using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// SIMPLE live rule — bas itna:
    ///   - Refs: 11:15 only
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
            new TimeSpan(11, 15, 0),
        };

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
