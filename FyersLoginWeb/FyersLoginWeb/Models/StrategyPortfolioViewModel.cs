using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Models
{
    // Day-of-week filter ke saath multi-leg portfolio ka result
    public class StrategyPortfolioViewModel
    {
        public string? Error { get; set; }
        public string Period { get; set; } = "";
        public List<PortfolioLegRow> Legs { get; set; } = new();

        public int TotalTrades => Legs.Sum(l => l.Trades);
        public int TotalTarget => Legs.Sum(l => l.Target);
        public int TotalTrail => Legs.Sum(l => l.Trail);
        public int TotalSL => Legs.Sum(l => l.SL);
        public int TotalTimeExit => Legs.Sum(l => l.TimeExit);
        public int TotalOpen => Legs.Sum(l => l.Open);
        public decimal TotalNetR => Legs.Sum(l => l.NetR);
    }

    public class PortfolioLegRow
    {
        public string Name { get; set; } = "";
        public decimal RR { get; set; }
        public int Days { get; set; }      // scanned trading days
        public int Trades { get; set; }
        public int Target { get; set; }
        public int Trail { get; set; }
        public int SL { get; set; }
        public int TimeExit { get; set; }        // 2:45 square-off par band
        public decimal TimeExitR { get; set; }   // in trades ka actual R total
        public int Open { get; set; }

        // net R: target/trail wins * RR - SL + time-exit ka actual R
        public decimal NetR => (Target + Trail) * RR - SL + TimeExitR;
        // win = target + trail + profit-wale time-exit ; loss = SL + loss-wale time-exit
        public int TimeExitWins { get; set; }
        public int TimeExitLosses { get; set; }
        public int Wins => Target + Trail + TimeExitWins;
        public int Losses => SL + TimeExitLosses;
        public string WinRate => (Wins + Losses) == 0 ? "-" : $"{100.0 * Wins / (Wins + Losses):F0}%";
    }
}
