namespace FyersLoginWeb.Models
{
    // Option-chart pe chali strategy ka result (leg-wise). PortfolioLegRow reuse.
    public class StrategyOptionsViewModel
    {
        public string? Error { get; set; }
        public string Period { get; set; } = "";
        public System.Collections.Generic.List<PortfolioLegRow> Legs { get; set; } = new();

        public System.Collections.Generic.List<OptionTradeRow> Trades { get; set; } = new();

        public int TotalTrades => System.Linq.Enumerable.Sum(Legs, l => l.Trades);
        public int TotalTarget => System.Linq.Enumerable.Sum(Legs, l => l.Target);
        public int TotalSL => System.Linq.Enumerable.Sum(Legs, l => l.SL);
        public int TotalTimeExit => System.Linq.Enumerable.Sum(Legs, l => l.TimeExit);
        public int TotalOpen => System.Linq.Enumerable.Sum(Legs, l => l.Open);
        public decimal TotalNetR => System.Linq.Enumerable.Sum(Legs, l => l.NetR);
    }

    public class OptionTradeRow
    {
        public string Date { get; set; } = "";
        public string Symbol { get; set; } = "";   // option symbol
        public string Side { get; set; } = "";      // CE/PE
        public string EntryTime { get; set; } = "";
        public decimal EntryPrem { get; set; }
        public decimal SL { get; set; }
        public decimal Target { get; set; }
        public string Outcome { get; set; } = "";
        public decimal R { get; set; }              // +RR / -1 / 0
        public decimal PnlPts { get; set; }         // premium points (exit-entry approx)
    }
}
