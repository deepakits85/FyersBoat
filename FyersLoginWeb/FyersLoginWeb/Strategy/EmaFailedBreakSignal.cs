using System;
using System.Collections.Generic;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// EMA failed-break opener (first session candle ignored).
    ///
    /// BUY (price below EMA9+EMA15, optional near day-low):
    ///   prior candle High break → break candle RED, else next candle RED → entry on that close.
    /// SELL (price above both EMAs, optional near day-high): opposite (Low break + green).
    ///
    /// SL = lowest/highest of prior 1–2 candles; if risk &gt; 50% of candle just before broken,
    ///      cap SL distance to that 50%. Target 1:3.
    /// </summary>
    public static class EmaFailedBreakSignal
    {
        public const decimal DefaultRiskReward = 3m;
        public const decimal DefaultDayProximityPct = 0.10m; // 10% of day range
        public const int DefaultSlLookback = 2; // 1 or 2 prior candles

        public static EmaFailedBreakConfig DefaultConfig() => new();

        /// <summary>
        /// Scan one session (IST bars already filtered to the trading day).
        /// candles[0] = first session bar (ignored for signals, used for day H/L + EMA continuity).
        /// EMAs must already be attached on each candle.
        /// </summary>
        public static List<EmaFailedBreakTrade> ScanDay(
            IReadOnlyList<EmaCandle> dayBars,
            EmaFailedBreakConfig? config = null)
        {
            config ??= DefaultConfig();
            var trades = new List<EmaFailedBreakTrade>();
            if (dayBars == null || dayBars.Count < 3)
                return trades;

            int i = 1; // skip first candle for signal start
            while (i < dayBars.Count)
            {
                var buy = TrySignal(dayBars, i, isBuy: true, config);
                if (buy != null)
                {
                    trades.Add(buy);
                    i = buy.EntryIndex + 1;
                    continue;
                }

                var sell = TrySignal(dayBars, i, isBuy: false, config);
                if (sell != null)
                {
                    trades.Add(sell);
                    i = sell.EntryIndex + 1;
                    continue;
                }

                i++;
            }

            return trades;
        }

        static EmaFailedBreakTrade? TrySignal(
            IReadOnlyList<EmaCandle> bars, int breakIdx, bool isBuy, EmaFailedBreakConfig cfg)
        {
            if (breakIdx < 1 || breakIdx >= bars.Count)
                return null;

            var brk = bars[breakIdx];
            var broken = bars[breakIdx - 1]; // jiska high/low break ho raha hai

            if (isBuy)
            {
                if (!(brk.High > broken.High))
                    return null;
            }
            else
            {
                if (!(brk.Low < broken.Low))
                    return null;
            }

            // Confirm: break candle color, else next candle
            int entryIdx;
            if (isBuy)
            {
                if (IsRed(brk))
                    entryIdx = breakIdx;
                else if (breakIdx + 1 < bars.Count && IsRed(bars[breakIdx + 1]))
                    entryIdx = breakIdx + 1;
                else
                    return null;
            }
            else
            {
                if (IsGreen(brk))
                    entryIdx = breakIdx;
                else if (breakIdx + 1 < bars.Count && IsGreen(bars[breakIdx + 1]))
                    entryIdx = breakIdx + 1;
                else
                    return null;
            }

            var entryBar = bars[entryIdx];

            // Context on entry candle: clear of both EMAs + optional day extreme
            if (isBuy)
            {
                if (!ClearBelow(entryBar, entryBar.Ema9) || !ClearBelow(entryBar, entryBar.Ema15))
                    return null;
            }
            else
            {
                if (!ClearAbove(entryBar, entryBar.Ema9) || !ClearAbove(entryBar, entryBar.Ema15))
                    return null;
            }

            var (dayHigh, dayLow) = DayRange(bars, entryIdx);
            decimal dayRange = dayHigh - dayLow;
            if (cfg.UseDayProximity && dayRange > 0m)
            {
                if (isBuy)
                {
                    // bottom 10% of day range
                    if ((entryBar.Close - dayLow) / dayRange > cfg.DayProximityPct)
                        return null;
                }
                else
                {
                    if ((dayHigh - entryBar.Close) / dayRange > cfg.DayProximityPct)
                        return null;
                }
            }

            decimal entry = entryBar.Close;
            decimal sl = ComputeStop(bars, entryIdx, breakIdx - 1, entry, isBuy, cfg);
            decimal risk = Math.Abs(entry - sl);
            if (risk <= 0m)
                return null;
            // SL must be on the correct side of entry
            if (isBuy && sl >= entry) return null;
            if (!isBuy && sl <= entry) return null;

            decimal rr = cfg.RiskRewardRatio;
            decimal target = isBuy ? entry + risk * rr : entry - risk * rr;

            return new EmaFailedBreakTrade
            {
                Side = isBuy ? "BUY" : "SELL",
                EntryPrice = entry,
                StopLoss = sl,
                Target = target,
                Risk = risk,
                RiskRewardRatio = rr,
                EntryIndex = entryIdx,
                BreakIndex = breakIdx,
                BrokenIndex = breakIdx - 1,
                EntryTime = entryBar.StartTime,
                BreakTime = brk.StartTime,
                BrokenTime = broken.StartTime,
                DayHigh = dayHigh,
                DayLow = dayLow,
                UsedDayProximity = cfg.UseDayProximity
            };
        }

        /// <summary>
        /// Primary SL = min/max of prior 1–2 candles before entry.
        /// If risk &gt; 50% of (candle just before broken), cap distance to that 50%.
        /// </summary>
        static decimal ComputeStop(
            IReadOnlyList<EmaCandle> bars,
            int entryIdx,
            int brokenIdx,
            decimal entry,
            bool isBuy,
            EmaFailedBreakConfig cfg)
        {
            int lookback = Math.Clamp(cfg.SlLookbackCandles, 1, 2);
            decimal extreme = isBuy ? decimal.MaxValue : decimal.MinValue;
            int found = 0;
            for (int k = 1; k <= lookback; k++)
            {
                int idx = entryIdx - k;
                if (idx < 0) break;
                found++;
                if (isBuy)
                    extreme = Math.Min(extreme, bars[idx].Low);
                else
                    extreme = Math.Max(extreme, bars[idx].High);
            }

            if (found == 0)
                extreme = isBuy ? entry - 0.01m : entry + 0.01m;

            decimal primaryRisk = Math.Abs(entry - extreme);

            // Cap candle = broken se just pehle
            decimal? capRisk = null;
            int capIdx = brokenIdx - 1;
            if (capIdx >= 0)
            {
                var cap = bars[capIdx];
                decimal rng = cap.High - cap.Low;
                if (rng > 0m)
                    capRisk = rng * 0.50m;
            }

            if (capRisk is decimal cap && primaryRisk > cap)
                return isBuy ? entry - cap : entry + cap;

            return extreme;
        }

        static (decimal high, decimal low) DayRange(IReadOnlyList<EmaCandle> bars, int throughIdx)
        {
            decimal hi = decimal.MinValue, lo = decimal.MaxValue;
            for (int i = 0; i <= throughIdx && i < bars.Count; i++)
            {
                hi = Math.Max(hi, bars[i].High);
                lo = Math.Min(lo, bars[i].Low);
            }
            return (hi, lo);
        }

        public static bool IsRed(EmaCandle c) => c.Close < c.Open;
        public static bool IsGreen(EmaCandle c) => c.Close > c.Open;

        public static bool ClearAbove(EmaCandle c, decimal ema) =>
            c.Open > ema && c.High > ema && c.Low > ema && c.Close > ema;

        public static bool ClearBelow(EmaCandle c, decimal ema) =>
            c.Open < ema && c.High < ema && c.Low < ema && c.Close < ema;

        public static void AttachEma(List<EmaCandle> series)
        {
            if (series == null || series.Count == 0) return;
            decimal e9 = series[0].Close, e15 = series[0].Close;
            decimal k9 = 2m / (9m + 1m), k15 = 2m / (15m + 1m);
            series[0].Ema9 = e9; series[0].Ema15 = e15;
            for (int i = 1; i < series.Count; i++)
            {
                e9 = series[i].Close * k9 + e9 * (1m - k9);
                e15 = series[i].Close * k15 + e15 * (1m - k15);
                series[i].Ema9 = e9;
                series[i].Ema15 = e15;
            }
        }
    }

    public class EmaFailedBreakConfig
    {
        /// <summary>Require price in bottom/top DayProximityPct of day range. Default true (10%).</summary>
        public bool UseDayProximity { get; set; } = true;
        public decimal DayProximityPct { get; set; } = EmaFailedBreakSignal.DefaultDayProximityPct;
        /// <summary>Prior candles for SL extreme: 1 or 2. Default 2.</summary>
        public int SlLookbackCandles { get; set; } = EmaFailedBreakSignal.DefaultSlLookback;
        public decimal RiskRewardRatio { get; set; } = EmaFailedBreakSignal.DefaultRiskReward;
    }

    public class EmaFailedBreakTrade
    {
        public string Side { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Target { get; set; }
        public decimal Risk { get; set; }
        public decimal RiskRewardRatio { get; set; }
        public int EntryIndex { get; set; }
        public int BreakIndex { get; set; }
        public int BrokenIndex { get; set; }
        public DateTime EntryTime { get; set; }
        public DateTime BreakTime { get; set; }
        public DateTime BrokenTime { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public bool UsedDayProximity { get; set; }
    }
}
