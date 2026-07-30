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
    ///   - Retracement: &gt; 20% of ref range
    ///   - Second BO: entry on breakout touch (wick) — close wait nahi
    ///   - SL: ref 50% | Target: 1:3 | no trail / no filters / no prior / no retr-first / no freshness
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

        public const int MaxSlPerDay = 2;
        public const int MinGapMinutes = 30;
        public const int MaxReferenceWaitMinutes = 90;
        public const int FreshSignalMaxAgeMinutes = 6;
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
                UseSquareOff = true,
                SquareOffTime = SquareOffTime,
                UseReferenceMaxWait = false,
                FirstBreakoutRequireClose = true,
                RequireCloseConfirm = false, // 2nd BO: buy on touch
                ConfirmRetrFirstOnly = false,
                UseRetracementFirst = false,
                SoftRetracementConfirm = false,
                UsePriorLevelBreak = false,
                UseSetupFreshness = false,
                StopLossRetracementPercent = 0.50m, // SL @ 50%
            };
            cfg.SetRetracementFromPercentage(20m); // confirm >20%
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
