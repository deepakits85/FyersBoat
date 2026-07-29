using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>Trade ki direction.</summary>
    public enum TradeSide { Long, Short }

    /// <summary>Trade ka result.</summary>
    public enum TradeOutcome
    {
        Open,           // na target na SL
        TargetHit,      // final target (trailing ho to 2.5R) laga
        StopLossHit,    // original SL laga (loss)
        TrailStopHit,   // 2R tak gaya phir trailed SL (entry±1) laga (chhota profit)
        TimeExit        // square-off time (2:45) par force-close (jo bhi P&L us waqt tha)
    }

    /// <summary>
    /// Ek trade signal (BUY=Long / SELL=Short) ka poora context:
    /// setup + entry + SL + target + outcome. FYERS order execution isi ko consume karega.
    /// </summary>
    public class TradeSignal
    {
        public TradeSide Side { get; init; }
        public ReferenceCandle Reference { get; init; } = null!;

        // Step 1: pehla breach (long=breakout above High, short=breakdown below Low)
        public decimal FirstBreachPrice { get; init; }
        public DateTime FirstBreachTime { get; init; }

        // Step 2: retracement
        public decimal RetracementLevel { get; init; }
        public decimal RetracementExtreme { get; init; } // long=low reached, short=high reached
        public DateTime RetracementTime { get; init; }

        // Step 3: doosra breach
        public decimal SecondBreachPrice { get; init; }
        public DateTime SecondBreachTime { get; init; }

        // Step 4: actual ENTRY
        public decimal EntryPrice { get; init; }
        public DateTime EntryTime { get; init; }

        // Risk management
        public decimal StopLoss { get; init; }
        public decimal Target { get; init; }
        public decimal Risk { get; init; }
        public decimal RiskRewardRatio { get; init; }

        // Outcome (backtest se bhara jata hai)
        public TradeOutcome Outcome { get; set; } = TradeOutcome.Open;
        public DateTime? OutcomeTime { get; set; }
        public decimal? OutcomePrice { get; set; }

        public DateTime SignalTime => EntryTime;
        public bool IsLong => Side == TradeSide.Long;

        /// <summary>
        /// Realized R-multiple (outcome ke hisaab se). TargetHit=+RR, StopLossHit=-1,
        /// TimeExit/TrailStopHit=actual (exit-entry)/risk (direction-aware), Open=0.
        /// </summary>
        public decimal RealizedR
        {
            get
            {
                switch (Outcome)
                {
                    case TradeOutcome.TargetHit: return RiskRewardRatio;
                    case TradeOutcome.StopLossHit: return -1m;
                    case TradeOutcome.TimeExit:
                    case TradeOutcome.TrailStopHit:
                        if (!OutcomePrice.HasValue || Risk == 0) return 0m;
                        decimal move = IsLong ? OutcomePrice.Value - EntryPrice
                                              : EntryPrice - OutcomePrice.Value;
                        return Math.Round(move / Risk, 3);
                    default: return 0m;
                }
            }
        }

        /// <summary>Breach level jahan se trade shuru hua (long=RefHigh, short=RefLow).</summary>
        public decimal BreachLevel => IsLong ? Reference.High : Reference.Low;

        public override string ToString() =>
            $"{(IsLong ? "BUY" : "SELL")} @ {EntryPrice} ({EntryTime:HH:mm}) | SL {StopLoss} " +
            $"| Target {Target} (1:{RiskRewardRatio}) | {Outcome}";
    }
}
