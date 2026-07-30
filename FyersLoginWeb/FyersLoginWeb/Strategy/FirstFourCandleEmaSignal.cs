using System;
using System.Collections.Generic;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// First-4-candle pattern (ignore candle[0]) + EMA9/EMA15.
    /// Volume OFF. c3 O/H/L/C EMA touch nahi.
    /// SL = jis candle ka H/L break ho (c1) uska opposite extreme.
    /// Target = 1:1.6 (Entry ± 1.6 * risk).
    /// </summary>
    public static class FirstFourCandleEmaSignal
    {
        public const decimal RiskRewardRatio = 1.6m;

        /// <summary>
        /// candles[0]=ignored, [1]=c1 (break level), [2]=c2, [3]=c3.
        /// Returns null if no signal.
        /// </summary>
        public static FourCandleEmaTrade? Apply(IReadOnlyList<EmaCandle> candles)
        {
            if (candles == null || candles.Count < 4)
                return null;

            var c1 = candles[1];
            var c2 = candles[2];
            var c3 = candles[3];

            bool c2Red = c2.Close < c2.Open;
            bool c2Green = c2.Close > c2.Open;
            bool c3Red = c3.Close < c3.Open;
            bool c3Green = c3.Close > c3.Open;

            bool aboveEMA = ClearAbove(c3, c3.Ema9) && ClearAbove(c3, c3.Ema15);
            bool belowEMA = ClearBelow(c3, c3.Ema9) && ClearBelow(c3, c3.Ema15);

            bool breakout = c2.High > c1.High;
            if (breakout && aboveEMA && !(c2Green && c3Green))
            {
                if (c2Red || (c2Green && c3Red))
                    return BuildTrade("BUY", c1, c3);
            }

            bool breakdown = c2.Low < c1.Low;
            if (breakdown && belowEMA && !(c2Red && c3Red))
            {
                if (c2Green || (c2Red && c3Green))
                    return BuildTrade("SELL", c1, c3);
            }

            return null;
        }

        /// <summary>Side string only — "BUY" / "SELL" / "".</summary>
        public static string ApplySide(IReadOnlyList<EmaCandle> candles) =>
            Apply(candles)?.Side ?? "";

        /// <summary>
        /// BUY: break c1.High → Entry=c1.High, SL=c1.Low (lowest of break candle).
        /// SELL: break c1.Low → Entry=c1.Low, SL=c1.High (highest of break candle).
        /// Target 1:6.
        /// </summary>
        static FourCandleEmaTrade BuildTrade(string side, EmaCandle breakCandle, EmaCandle signalCandle)
        {
            bool buy = side == "BUY";
            decimal entry = buy ? breakCandle.High : breakCandle.Low;
            decimal sl = buy ? breakCandle.Low : breakCandle.High;
            decimal risk = Math.Abs(entry - sl);
            if (risk <= 0m)
                risk = 0.01m; // guard degenerate candle
            decimal target = buy ? entry + risk * RiskRewardRatio : entry - risk * RiskRewardRatio;

            return new FourCandleEmaTrade
            {
                Side = side,
                EntryPrice = entry,
                StopLoss = sl,
                Target = target,
                Risk = risk,
                RiskRewardRatio = RiskRewardRatio,
                BreakCandleHigh = breakCandle.High,
                BreakCandleLow = breakCandle.Low,
                SignalTime = signalCandle.StartTime,
                BreakCandleTime = breakCandle.StartTime
            };
        }

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

    public class FourCandleEmaTrade
    {
        public string Side { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Target { get; set; }
        public decimal Risk { get; set; }
        public decimal RiskRewardRatio { get; set; }
        public decimal BreakCandleHigh { get; set; }
        public decimal BreakCandleLow { get; set; }
        public DateTime SignalTime { get; set; }
        public DateTime BreakCandleTime { get; set; }
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
