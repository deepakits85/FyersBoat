using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Ek completed 30-minute reference candle. Iske High/Low se range aur
    /// retracement level nikalte hain.
    /// </summary>
    public class ReferenceCandle
    {
        public DateTime StartTime { get; }
        public DateTime EndTime { get; }
        public decimal High { get; }
        public decimal Low { get; }

        /// <summary>Long breakout jise cross karna hai (= max(High, prior high)). Default High.</summary>
        public decimal BreakoutLevel { get; }
        /// <summary>Short breakdown jise cross karna hai (= min(Low, prior low)). Default Low.</summary>
        public decimal BreakdownLevel { get; }

        public ReferenceCandle(DateTime startTime, DateTime endTime, decimal high, decimal low,
            decimal? breakoutLevel = null, decimal? breakdownLevel = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            High = high;
            Low = low;
            BreakoutLevel = breakoutLevel ?? high;
            BreakdownLevel = breakdownLevel ?? low;
        }

        /// <summary>Range = 30m High - 30m Low.</summary>
        public decimal Range => High - Low;

        /// <summary>
        /// Retracement Level = High - (Range * percent). percent 0..1 (default 0.50).
        /// Example: High=100, Low=80, Range=20, 50% => 100 - 10 = 90.
        /// </summary>
        public decimal GetRetracementLevel(decimal retracementPercent)
            => High - (Range * retracementPercent);

        /// <summary>Ek 30-min OHLC candle se ReferenceCandle banao (prior high/low ke saath).</summary>
        public static ReferenceCandle FromCandle(Candle c, decimal? priorHigh = null, decimal? priorLow = null)
        {
            decimal bo = priorHigh.HasValue ? Math.Max(c.High, priorHigh.Value) : c.High;
            decimal bd = priorLow.HasValue ? Math.Min(c.Low, priorLow.Value) : c.Low;
            return new ReferenceCandle(c.StartTime, c.EndTime, c.High, c.Low, bo, bd);
        }

        public override string ToString() =>
            $"Ref[{StartTime:HH:mm}-{EndTime:HH:mm}] H:{High} L:{Low} Range:{Range}";
    }
}
