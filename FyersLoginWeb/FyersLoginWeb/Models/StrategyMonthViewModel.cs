using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FyersLoginWeb.Models
{
    // Poore mahine ka strategy summary
    public class StrategyMonthViewModel
    {
        public string Symbol { get; set; } = "";
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal RetracementPercent { get; set; }
        public decimal EntryBufferPoints { get; set; }
        public decimal StopLossBufferPoints { get; set; }
        public decimal RiskRewardRatio { get; set; }
        public string? Error { get; set; }

        public List<StrategyMonthDayRow> Days { get; set; } = new();

        public string MonthName =>
            CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(Month == 0 ? 1 : Month);

        public int TradingDays => Days.Count;

        public int TotalBuy => Days.Sum(d => d.BuyCount);
        public int BuyTargetHits => Days.Sum(d => d.BuyTargetHits);
        public int BuyTrailStops => Days.Sum(d => d.BuyTrailStops);
        public int BuyStopLossHits => Days.Sum(d => d.BuyStopLossHits);
        public int BuyOpen => Days.Sum(d => d.BuyOpen);

        public int TotalSell => Days.Sum(d => d.SellCount);
        public int SellTargetHits => Days.Sum(d => d.SellTargetHits);
        public int SellTrailStops => Days.Sum(d => d.SellTrailStops);
        public int SellStopLossHits => Days.Sum(d => d.SellStopLossHits);
        public int SellOpen => Days.Sum(d => d.SellOpen);
    }

    public class StrategyMonthDayRow
    {
        public DateTime Date { get; set; }

        public int BuyCount { get; set; }
        public int BuyTargetHits { get; set; }
        public int BuyTrailStops { get; set; }
        public int BuyStopLossHits { get; set; }
        public int BuyOpen { get; set; }

        public int SellCount { get; set; }
        public int SellTargetHits { get; set; }
        public int SellTrailStops { get; set; }
        public int SellStopLossHits { get; set; }
        public int SellOpen { get; set; }
    }
}
