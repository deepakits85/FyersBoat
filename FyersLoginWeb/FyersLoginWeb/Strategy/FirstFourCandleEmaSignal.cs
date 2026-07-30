using System;
using System.Collections.Generic;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// First-4-candle pattern (ignore candle[0]) + EMA9/EMA15.
    /// Volume filter OFF. c3 wick/body must NOT touch EMA (Low&gt;EMAs / High&lt;EMAs).
    /// </summary>
    public static class FirstFourCandleEmaSignal
    {
        /// <summary>
        /// candles[0]=ignored, [1]=c1, [2]=c2, [3]=c3. Each candle must have EMA9/EMA15 set.
        /// Returns "BUY", "SELL", or "".
        /// </summary>
        public static string Apply(IReadOnlyList<EmaCandle> candles)
        {
            if (candles == null || candles.Count < 4)
                return "";

            var c1 = candles[1];
            var c2 = candles[2];
            var c3 = candles[3];

            bool c2Red = c2.Close < c2.Open;
            bool c2Green = c2.Close > c2.Open;
            bool c3Red = c3.Close < c3.Open;
            bool c3Green = c3.Close > c3.Open;

            // Poora candle clear of EMA (wick + body touch nahi)
            bool aboveEMA = c3.Low > c3.Ema9 && c3.Low > c3.Ema15;
            bool belowEMA = c3.High < c3.Ema9 && c3.High < c3.Ema15;

            bool breakout = c2.High > c1.High;
            if (breakout && aboveEMA)
            {
                // skip Green+Green
                if (!(c2Green && c3Green))
                {
                    if (c2Red) return "BUY";
                    if (c2Green && c3Red) return "BUY";
                }
            }

            bool breakdown = c2.Low < c1.Low;
            if (breakdown && belowEMA)
            {
                // skip Red+Red
                if (!(c2Red && c3Red))
                {
                    if (c2Green) return "SELL";
                    if (c2Red && c3Green) return "SELL";
                }
            }

            return "";
        }

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

    public class EmaCandle
    {
        public DateTime StartTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal Ema9 { get; set; }
        public decimal Ema15 { get; set; }
    }
}
