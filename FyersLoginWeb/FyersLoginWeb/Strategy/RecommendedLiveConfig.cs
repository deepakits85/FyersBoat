using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Live product (user R&amp;D):
    ///   - Index: Nifty only
    ///   - Refs: 11:15 only
    ///   - Breakout / entry level: REFERENCE High/Low (UsePriorLevelBreak=false)
    ///   - Sequence: pehle asli first breakout, phir 50% retrace, phir 2nd breakout → entry
    ///     (UseRetracementFirst=false — "pehle hi retracement" path OFF)
    ///   - Entry: close confirm @ EffLevel; same-candle fill if Low ≤ Cap; else pullback wait
    ///   - Filters: OFF
    ///   - Risk: RR=2, trail, 90m wait, 14:45 square-off
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
                // Strict sequence: first BO → 50% → 2nd BO (no retracement-before-breakout)
                UseRetracementFirst = false,
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
