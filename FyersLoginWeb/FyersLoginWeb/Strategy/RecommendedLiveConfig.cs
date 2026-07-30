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
    /// Nifty + BankNifty, refs 11:15 + 12:45 (10:45 filter-skip), RR 1:2,
    /// 90m wait ON, strict filters, trail ON.
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
        public const int FreshSignalMaxAgeMinutes = 6;
        public const int CandleMinutes = 3;

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
