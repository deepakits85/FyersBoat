using System;
using System.Collections.Generic;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// EMA failed-break opener (first session candle ignored).
    ///
    /// BUY (below EMA9+EMA15, optional near day-low) — Bank 15m 5-Jun style only:
    ///   prior High broken by a GREEN candle → next candle must be RED → entry on that close.
    ///   Break + entry clear of EMA. No entry after 14:45.
    ///   Ignore if broken candle itself already broke prior high/low (2nd consecutive break).
    ///   Day proximity uses H/L through break only (pre-entry) — entry cannot fake day extreme.
    /// SELL (above both EMAs, optional near day-high):
    ///   prior Low broken by a RED candle → next candle must be GREEN → entry on that close.
    ///
    /// SL = lowest/highest of prior 1–2 candles; if risk &gt; 50% of candle just before broken,
    ///      cap SL distance to that 50%. Target 1:3.
    /// </summary>
    public static class EmaFailedBreakSignal
    {
        public const decimal DefaultRiskReward = 3m;
        public const decimal DefaultDayProximityPct = 0.10m; // 10% of day range
        public const int DefaultSlLookback = 2; // 1 or 2 prior candles
        /// <summary>Last allowed entry candle start (IST). After this — no trade.</summary>
        public static readonly TimeSpan EntryCutoff = new(14, 45, 0);

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

            // First candle [0] fully ignored — not used as broken reference either.
            // breakIdx starts at 2 ⇒ broken index >= 1.
            int i = 2;
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
            // Need: pre[broken-1], broken, break, next — so breakIdx >= 2
            if (breakIdx < 2 || breakIdx + 1 >= bars.Count)
                return null;

            var brk = bars[breakIdx];
            var broken = bars[breakIdx - 1]; // jiska high/low break ho raha hai
            var next = bars[breakIdx + 1];   // confirm candle (required)

            if (isBuy)
            {
                // High break + green break candle + next red → entry on next close
                if (!(brk.High > broken.High)) return null;
                if (!IsGreen(brk)) return null;
                if (!IsRed(next)) return null;
                // Continuous high-break ignore: broken candle khud pehle wale ka high
                // break kar chuka ho to ye 2nd break hai — setup skip
                if (breakIdx >= 2 && broken.High > bars[breakIdx - 2].High)
                    return null;
            }
            else
            {
                // Low break + red break candle + next green → entry on next close
                if (!(brk.Low < broken.Low)) return null;
                if (!IsRed(brk)) return null;
                if (!IsGreen(next)) return null;
                // Continuous low-break ignore (2nd consecutive breakdown)
                if (breakIdx >= 2 && broken.Low < bars[breakIdx - 2].Low)
                    return null;
            }

            int entryIdx = breakIdx + 1;
            var entryBar = bars[entryIdx];

            // No entry after 14:45 IST
            if (entryBar.StartTime.TimeOfDay > EntryCutoff)
                return null;

            // Break + entry both clear of EMA (wick/body touch nahi)
            if (isBuy)
            {
                if (!ClearBelow(brk, brk.Ema9) || !ClearBelow(brk, brk.Ema15))
                    return null;
                if (!ClearBelow(entryBar, entryBar.Ema9) || !ClearBelow(entryBar, entryBar.Ema15))
                    return null;
            }
            else
            {
                if (!ClearAbove(brk, brk.Ema9) || !ClearAbove(brk, brk.Ema15))
                    return null;
                if (!ClearAbove(entryBar, entryBar.Ema9) || !ClearAbove(entryBar, entryBar.Ema15))
                    return null;
            }

            // Pre-entry day range (through break). Entry cannot invent day-low/high
            // to fake proximity — Bank Jun5 day-low was already set before entry.
            var (dayHigh, dayLow) = DayRange(bars, breakIdx);
            decimal dayRange = dayHigh - dayLow;
            if (cfg.UseDayProximity && dayRange > 0m)
            {
                if (isBuy)
                {
                    // bottom 10% of already-established day range
                    decimal prox = (entryBar.Close - dayLow) / dayRange;
                    if (prox < 0m || prox > cfg.DayProximityPct)
                        return null;
                }
                else
                {
                    decimal prox = (dayHigh - entryBar.Close) / dayRange;
                    if (prox < 0m || prox > cfg.DayProximityPct)
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
