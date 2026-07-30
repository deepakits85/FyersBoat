using System;
using System.Collections.Generic;
using System.Linq;
using FyersLoginWeb.Strategy;

namespace FyersLoginWeb.Models
{
    // Strategy day-run ka result UI ke liye
    public class StrategyDayViewModel
    {
        public string Symbol { get; set; } = "";
        public DateTime TradingDay { get; set; }
        public decimal RetracementPercent { get; set; }
        public decimal EntryBufferPoints { get; set; }
        public decimal StopLossBufferPoints { get; set; }
        public decimal RiskRewardRatio { get; set; }
        public string? Error { get; set; }

        public List<TradeSignal> BuySignals { get; set; } = new();   // Long
        public List<TradeSignal> SellSignals { get; set; } = new();  // Short
        public List<StrategyStepRow> Steps { get; set; } = new();       // Long steps
        public List<StrategyStepRow> ShortSteps { get; set; } = new();  // Short steps
        public int ReferenceCount { get; set; }
        public int ThreeMinCount { get; set; }

        // Option chart (CE/PE) par sirf LONG side lete hain — premium hamesha BUY hota hai
        // (BUY signal → CE kharido, SELL signal → PE kharido; option ko SHORT kabhi nahi karte).
        // Isliye option symbol par SELL/Short section chhupा dete hain.
        public bool IsOption => Symbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase)
                             || Symbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase);

        public int BuyTargetHits => BuySignals.Count(s => s.Outcome == TradeOutcome.TargetHit);
        public int BuyStopLossHits => BuySignals.Count(s => s.Outcome == TradeOutcome.StopLossHit);
        public int BuyTrailStops => BuySignals.Count(s => s.Outcome == TradeOutcome.TrailStopHit);
        public int SellTargetHits => SellSignals.Count(s => s.Outcome == TradeOutcome.TargetHit);
        public int SellStopLossHits => SellSignals.Count(s => s.Outcome == TradeOutcome.StopLossHit);
        public int SellTrailStops => SellSignals.Count(s => s.Outcome == TradeOutcome.TrailStopHit);
    }

    // Har 3-min candle ka ek row
    public class StrategyStepRow
    {
        public string Time { get; set; } = "";
        public string RefWindow { get; set; } = "";
        public decimal RefHigh { get; set; }
        public decimal RefLow { get; set; }
        public decimal RetrLevel { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public string State { get; set; } = "";
        public string? Confirmed { get; set; }
        public string Message { get; set; } = "";

        public bool IsFirstBreakout => Confirmed == nameof(StrategyState.FirstBreakoutConfirmed);
        public bool IsRetracement => Confirmed == nameof(StrategyState.RetracementConfirmed);
        public bool IsSecondBreakout => Confirmed == nameof(StrategyState.SecondBreakoutConfirmed);
        public bool IsBuy => Confirmed == nameof(StrategyState.BuySignal);
    }
}
