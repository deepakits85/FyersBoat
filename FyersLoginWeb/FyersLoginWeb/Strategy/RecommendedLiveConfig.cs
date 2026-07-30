using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// DATA-BACKED live defaults (R&amp;D): jo 11m index pe jeeta woh.
    ///
    /// Winner (Aug 2025–Jul 2026 index, strict): ~150 trades, ~+63.8R, ~5.3R/mo.
    /// Entry model: Close confirm @ EffLevel, SAME candle pe fill agar Low&lt;=cap
    /// (sirf gap hone pe pullback wait). "Next-bar @ refHigh only" R&amp;D pe haar gaya (~+1R).
    ///
    /// Nifty only, ref 11:15 only, RR 1:2, 90m wait ON, trail ON.
    /// Breakout/entry = reference High (manual match). Prior-high OFF.
    /// </summary>
    public static class RecommendedLiveConfig
    {
        public static readonly TimeSpan[] Refs =
        {
            new TimeSpan(11, 15, 0),
        };

        public const decimal NiftyRr = 2m;
        public const decimal BankRr = 2m; // unused when Bank leg off
        public const decimal StrikeStepNifty = 50m;
        public const decimal StrikeStepBank = 100m;

        public const int MaxSlPerDay = 2;
        public const int MinGapMinutes = 30;
        public const int MaxReferenceWaitMinutes = 90;
        public const int FreshSignalMaxAgeMinutes = 6;
        public const int CandleMinutes = 3;

        public static TimeSpan SquareOffTime { get; } = new TimeSpan(14, 45, 0);

        /// <summary>R&amp;D: filters off — user ne skip mat karo kaha.</summary>
        public static EntryFilterConfig EntryFilters()
            => new EntryFilterConfig();

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
                // DATA winner: close hold @ EffLevel, fill same candle if Low<=cap
                RequireCloseConfirm = true,
                ConfirmRetrFirstOnly = false,
                UseRetracementFirst = true,
                // Manual-match: breakout/entry = ref High only (prior-high OFF)
                UsePriorLevelBreak = false,
            };
            cfg.SetRetracementFromPercentage(50m);
            return cfg;
        }

        public static System.Collections.Generic.List<Candle> ClosedOnly(
            System.Collections.Generic.List<Candle> candles, DateTime now)
        {
            if (candles == null || candles.Count == 0)
                return new System.Collections.Generic.List<Candle>();
            return candles.Where(c => c.EndTime <= now).ToList();
        }
    }
}
