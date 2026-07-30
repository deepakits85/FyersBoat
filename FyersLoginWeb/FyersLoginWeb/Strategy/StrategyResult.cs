namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Har 3-min candle process karne par return hone wala result.
    /// </summary>
    public class StrategyResult
    {
        /// <summary>Process ke baad machine ka current (persistent) state.</summary>
        public StrategyState State { get; init; }

        /// <summary>
        /// Is candle par kya confirm hua (agar kuch hua):
        /// FirstBreakoutConfirmed / RetracementConfirmed / BuySignal. Warna null.
        /// </summary>
        public StrategyState? JustConfirmed { get; init; }

        /// <summary>Sirf tab set hota hai jab signal (BUY/SELL) generate ho.</summary>
        public TradeSignal? Signal { get; init; }

        /// <summary>Human-readable explanation (logging ke liye).</summary>
        public string Message { get; init; } = "";

        public bool HasSignal => Signal != null;
    }
}
