using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Strategy ke configurable parameters.
    /// </summary>
    public class StrategyConfig
    {
        private decimal _retracementPercent = 0.50m;

        /// <summary>
        /// Retracement level fraction (0..1). Default 0.50 = 50%.
        /// Retracement Level = 30m High - (Range * RetracementPercent).
        /// </summary>
        public decimal RetracementPercent
        {
            get => _retracementPercent;
            set
            {
                if (value <= 0m || value >= 1m)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        "RetracementPercent 0 aur 1 ke beech hona chahiye (e.g. 0.50 = 50%).");
                _retracementPercent = value;
            }
        }

        /// <summary>Convenience: 50 jaisa percentage set karne ke liye (0.50 me convert).</summary>
        public void SetRetracementFromPercentage(decimal percentage)
            => RetracementPercent = percentage / 100m;

        /// <summary>
        /// SL distance as fraction of ref range (separate from confirm retracement).
        /// Default 0.50 = SL at 50% of reference candle.
        /// </summary>
        public decimal StopLossRetracementPercent { get; set; } = 0.50m;

        /// <summary>
        /// First breakout needs candle CLOSE beyond EffLevel (not just wick).
        /// Default false (legacy High/Low wick). Simple rule: true.
        /// </summary>
        public bool FirstBreakoutRequireClose { get; set; } = false;

        /// <summary>
        /// Freshness / stale-retest timer on/off. Simple rule: false (band).
        /// </summary>
        public bool UseSetupFreshness { get; set; } = true;

        /// <summary>
        /// Entry zone buffer (points). BUY sirf [30m High .. 30m High + EntryBufferPoints]
        /// ke beech me hi hoga. Agar price zyada upar chala jaye to price wapas is zone me
        /// aane par entry milegi. Default 5.
        /// </summary>
        public decimal EntryBufferPoints { get; set; } = 5m;

        /// <summary>SL = 50% retracement level - StopLossBufferPoints. Default 5.</summary>
        public decimal StopLossBufferPoints { get; set; } = 5m;

        /// <summary>Risk:Reward ratio. Target = Entry + (Risk * RiskRewardRatio). Default 3 (1:3).</summary>
        public decimal RiskRewardRatio { get; set; } = 3m;

        // ---- Trailing rule ----
        /// <summary>Trailing on/off. Default true.</summary>
        public bool UseTrailing { get; set; } = true;

        /// <summary>Kitne R par trail activate ho. Default 2 (1:2).</summary>
        public decimal TrailActivateRR { get; set; } = 2m;

        /// <summary>Activate hone par SL entry se kitne point aage (long=+, short=-). Default 1.</summary>
        public decimal TrailStopOffsetPoints { get; set; } = 1m;

        /// <summary>Activate hone par naya target R. Default 2.5 (1:2.5).</summary>
        public decimal TrailTargetRR { get; set; } = 2.5m;

        /// <summary>
        /// Retest freshness (minutes). Retracement aur triggering breakout ke beech itne
        /// minute se zyada gap na ho (stale retest par entry nahi). First breakout aur
        /// retracement ke beech bhi yahi limit. Default 15.
        /// </summary>
        public int SetupFreshnessMinutes { get; set; } = 15;

        /// <summary>
        /// Breakout sirf reference high nahi, prior high (resistance) bhi cross kare tabhi valid.
        /// (Short: prior low bhi break ho.) Isse "lower high ka jhootha breakout" avoid hota hai.
        /// Default true.
        /// </summary>
        public bool UsePriorLevelBreak { get; set; } = true;

        /// <summary>Kitne pichhle 30-min candles ka high/low prior level ke liye dekha jaye. Default 1.</summary>
        public int PriorLevelLookback { get; set; } = 1;

        /// <summary>
        /// Retracement confirm mode after first breakout:
        /// false (default historically) = must touch RetracementPercent level (e.g. 50% mid).
        /// true = koi bhi pullback back to/through breakout level (EffLevel) enough —
        /// "ek baar retracement ho gaya" — full 50% complete zaroori nahi.
        /// SL ab bhi RetracementPercent level par rehta hai (risk structure).
        /// </summary>
        public bool SoftRetracementConfirm { get; set; } = false;

        /// <summary>
        /// "Retracement-first" path: agar pehle breakout se PEHLE hi price 50% level tak aa jaye,
        /// to seedha 2nd-breakout ka wait (bina asli pehle breakout ke). Default true (current).
        /// false karne par SIRF asli "pehle breakout -> retracement -> dobara breakout" wale setups.
        /// </summary>
        public bool UseRetracementFirst { get; set; } = true;

        /// <summary>
        /// 2nd breakout par entry ke liye candle-CLOSE confirm zaroori (jhoothe wick filter).
        /// Default true = confirmed-close (LIVE recommended + backtest). false = WICK mode:
        /// price ne level intra-candle (High/Low) chhua toh turant entry — recommended NAHI.
        /// Live bot history 3m CLOSED candles use karta hai; tick-by-tick nahi.
        /// </summary>
        public bool RequireCloseConfirm { get; set; } = true;

        /// <summary>
        /// Close-confirm SIRF retracement-first setups par (jab asli pehla breakout hua hi nahi).
        /// RequireCloseConfirm=false + ye true -> normal setup wick (turant), retr-first confirmed.
        /// </summary>
        public bool ConfirmRetrFirstOnly { get; set; } = false;

        // ---- Intraday square-off ----
        /// <summary>
        /// Square-off / no-entry cutoff. Is time ke baad koi NAYI entry nahi, aur is time tak
        /// jo bhi trade open ho use is candle ke price par force-close (TimeExit) kar dete hain.
        /// Default 14:45 (2:45 PM). UseSquareOff false karke band kar sakte ho.
        /// </summary>
        public TimeSpan SquareOffTime { get; set; } = new TimeSpan(14, 45, 0);

        /// <summary>Square-off rule on/off. Default true.</summary>
        public bool UseSquareOff { get; set; } = true;

        // ---- Reference max-wait (per reference lifetime) ----
        /// <summary>
        /// Ek reference (jaise 10:45 wali 30-min candle) ko monitoring shuru hone (window close,
        /// yaani reference EndTime) ke baad max itne minute tak hi setup complete hone ka
        /// mauka do. Uske baad us reference se koi entry nahi (reference chhod do). Default 90 (1:30 ghante).
        /// e.g. 10:45 ref -> window 10:45-11:15 -> entry cutoff 12:45.
        /// </summary>
        public int MaxReferenceWaitMinutes { get; set; } = 90;

        /// <summary>Reference max-wait rule on/off. Default true.</summary>
        public bool UseReferenceMaxWait { get; set; } = true;
    }
}
