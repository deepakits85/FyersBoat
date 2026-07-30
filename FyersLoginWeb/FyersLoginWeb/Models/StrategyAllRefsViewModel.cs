using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Models
{
    // Ek mahine me har 30-min reference ka summary (sab ek table me)
    public class StrategyAllRefsViewModel
    {
        public string Symbol { get; set; } = "";
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal RetracementPercent { get; set; }
        public string? Error { get; set; }
        public int TradingDays { get; set; }

        public List<StrategyRefRow> Rows { get; set; } = new();

        // totals
        public int TotBuy => Rows.Sum(r => r.BuyCount);
        public int TotBuyTgt => Rows.Sum(r => r.BuyTgt);
        public int TotBuyTrail => Rows.Sum(r => r.BuyTrail);
        public int TotBuySL => Rows.Sum(r => r.BuySL);
        public int TotBuyOpen => Rows.Sum(r => r.BuyOpen);
        public int TotSell => Rows.Sum(r => r.SellCount);
        public int TotSellTgt => Rows.Sum(r => r.SellTgt);
        public int TotSellTrail => Rows.Sum(r => r.SellTrail);
        public int TotSellSL => Rows.Sum(r => r.SellSL);
        public int TotSellOpen => Rows.Sum(r => r.SellOpen);
    }

    public class StrategyRefRow
    {
        public TimeSpan RefStart { get; set; }
        public string Label => $"{RefStart:hh\\:mm}-{RefStart.Add(TimeSpan.FromMinutes(30)):hh\\:mm}";

        public int BuyCount { get; set; }
        public int BuyTgt { get; set; }
        public int BuyTrail { get; set; }
        public int BuySL { get; set; }
        public int BuyOpen { get; set; }

        public int SellCount { get; set; }
        public int SellTgt { get; set; }
        public int SellTrail { get; set; }
        public int SellSL { get; set; }
        public int SellOpen { get; set; }

        // wins = target + trail ; losses = SL
        public int Wins => BuyTgt + BuyTrail + SellTgt + SellTrail;
        public int Losses => BuySL + SellSL;
    }
}
