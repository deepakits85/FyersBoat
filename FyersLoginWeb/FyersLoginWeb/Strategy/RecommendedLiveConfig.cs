using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Final live defaults (A/B backed):
    /// Nifty + BankNifty only, refs 11:15 + 12:45, strict filters, RR 1:2,
    /// 90-min ref max-wait ON, close-confirm ON, trailing ON, 30m gap, max 2 SL/day.
    /// Sensex OFF. 10:45 OFF.
    /// </summary>
    public static class RecommendedLiveConfig
    {
        public static readonly TimeSpan[] Refs =
        {
            new TimeSpan(11, 15, 0),
            new TimeSpan(12, 45, 0),
        };

        public const decimal NiftyRr = 2m;
        public const decimal BankRr = 2m;
        public const decimal StrikeStepNifty = 50m;
        public const decimal StrikeStepBank = 100m;

        public const int MaxSlPerDay = 2;
        public const int MinGapMinutes = 30;
        public const int MaxReferenceWaitMinutes = 90;
        public const int FreshSignalMaxAgeMinutes = 6; // catch signal within ~2 live polls after 3m close

        public static TimeSpan SquareOffTime { get; } = new TimeSpan(14, 45, 0);

        public static EntryFilterConfig EntryFilters()
            => EntryFilterConfig.ReduceStopLossStrictPreset();

        public static StrategyConfig MakeConfig(decimal riskReward)
        {
            var cfg = new StrategyConfig
            {
                EntryBufferPoints = 0m,
                StopLossBufferPoints = 0m,
                RiskRewardRatio = riskReward,
                UseTrailing = true,
                TrailActivateRR = 2m,
                TrailTargetRR = 2.5m,
                UseSquareOff = true,
                SquareOffTime = SquareOffTime,
                UseReferenceMaxWait = true,
                MaxReferenceWaitMinutes = MaxReferenceWaitMinutes,
                RequireCloseConfirm = true,   // 3m CLOSE confirm — wick-only entry nahi
                ConfirmRetrFirstOnly = false,
                UseRetracementFirst = true,
                UsePriorLevelBreak = true,
            };
            cfg.SetRetracementFromPercentage(50m);
            return cfg;
        }

        /// <summary>
        /// History API kabhi forming (incomplete) 3m candle bhi de sakti hai.
        /// Strategy sirf CLOSED candles pe chalti hai — EndTime &gt; now wali drop.
        /// </summary>
        public static System.Collections.Generic.List<Candle> ClosedOnly(
            System.Collections.Generic.List<Candle> candles, DateTime now)
        {
            if (candles == null || candles.Count == 0)
                return new System.Collections.Generic.List<Candle>();
            return candles.Where(c => c.EndTime <= now).ToList();
        }
    }
}
