using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Ek generic OHLC candle (30-min reference ya 3-min dono ke liye).
    /// Prices decimal me taaki precision sahi rahe.
    /// </summary>
    public class Candle
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }

        public Candle() { }

        public Candle(DateTime start, DateTime end,
            decimal open, decimal high, decimal low, decimal close)
        {
            StartTime = start;
            EndTime = end;
            Open = open;
            High = high;
            Low = low;
            Close = close;
        }

        public override string ToString() =>
            $"[{StartTime:HH:mm}-{EndTime:HH:mm}] O:{Open} H:{High} L:{Low} C:{Close}";
    }
}
