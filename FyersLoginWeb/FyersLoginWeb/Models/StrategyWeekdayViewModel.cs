using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Models
{
    // Weekday x Month net-R matrix (kaunsa din har mahine acha)
    public class StrategyWeekdayViewModel
    {
        public string Symbol { get; set; } = "";
        public decimal RR { get; set; }
        public string? Error { get; set; }
        public List<int> Months { get; set; } = new();
        public List<WeekdayRow> Rows { get; set; } = new();
    }

    public class WeekdayRow
    {
        public string Day { get; set; } = "";
        public Dictionary<int, decimal> MonthNet { get; set; } = new(); // month -> netR
        public int Trades { get; set; }
        public int Target { get; set; }
        public int SL { get; set; }
        public int Open { get; set; }
        public decimal TotalNet => MonthNet.Values.Sum();
    }
}
