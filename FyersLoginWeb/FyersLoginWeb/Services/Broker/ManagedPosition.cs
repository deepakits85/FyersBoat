using System;

namespace FyersLoginWeb.Services.Broker
{
    /// <summary>Ek active trade ka state + broker order-ids (exit design ka state machine).</summary>
    public class ManagedPosition
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";        // CE / PE
        public int Qty { get; set; }
        public decimal Entry { get; set; }
        public decimal SL { get; set; }
        public decimal Target { get; set; }
        public DateTime EntryTime { get; set; }

        public string State { get; set; } = "ENTRY_PENDING";  // ENTRY_PENDING / OPEN / CLOSED
        public string? BuyId { get; set; }
        public string? SlId { get; set; }
        public string? TargetId { get; set; }
        public string? ExitId { get; set; }               // 2:45 market sell

        public string? Outcome { get; set; }              // TargetHit / StopLossHit / TrailStopHit / TimeExit / EntryFail
        public decimal ExitPrice { get; set; }
        public DateTime? ClosedAt { get; set; }

        // ---- trailing state ----
        public decimal InitRisk { get; set; }             // ORIGINAL risk (entry - pehla SL); trailing SL badle bhi R isi se
        public bool Trailed { get; set; }                 // 2R reach hone par SL entry+1 pe move ho chuka

        // realized R (long premium): (exit - entry) / ORIGINAL risk (trailing ke baad SL badalta hai isliye InitRisk)
        public decimal RealizedR
        {
            get
            {
                decimal risk = InitRisk > 0 ? InitRisk : Entry - SL;
                if (Outcome == null || Outcome == "EntryFail" || risk == 0) return 0m;
                return Math.Round((ExitPrice - Entry) / risk, 2);
            }
        }
    }
}
