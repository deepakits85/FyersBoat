using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>Portfolio execution ke liye ek trade candidate (dashboard + live dono adapt karte hain).</summary>
    public interface IPortfolioTrade
    {
        DateTime EntryTime { get; }
        DateTime ExitTime { get; }     // OutcomeTime ?? EOD
        int Priority { get; }          // 0=Nifty, 1=SENSEX, 2=BankNifty
        bool IsStopLoss { get; }       // outcome == StopLoss (max-2-SL count ke liye)
    }

    public sealed class PortfolioDecision
    {
        public bool Skipped { get; set; }
        public string SkipReason { get; set; } = "";   // "busy" | "max2SL"
    }

    /// <summary>
    /// Portfolio execution rules — SINGLE SOURCE OF TRUTH (dashboard display AND live decision
    /// dono yahi use karte hain, taaki historical aur live kabhi diverge na hon):
    ///  1) consecutive entries ke beech min gap (default 30 min) — pichhle entry ke 30 min
    ///     ke andar naya trade nahi (one-at-a-time ka relaxed version).
    ///  2) same-time tie -> priority Nifty(0) > SENSEX(1) > BankNifty(2)
    ///  3) din me max N SL (default 2) -> uske baad us din koi naya trade nahi
    ///  4) NIFTY-priority GRACE window (graceMinutes, default 0 = OFF): jab ek slot khulta hai,
    ///     us se agle `graceMinutes` ke andar agar koi HIGHER-priority (lower Priority number)
    ///     candidate hai to usko prefer karo — taaki SENSEX/BankNifty thoda pehle signal de dein
    ///     tab bhi NIFTY(0) jeete. grace=0 par bilkul purana behaviour (exact-time tie only).
    /// Pure logic — koi UI/DB/API nahi.
    /// </summary>
    public static class PortfolioSelector
    {
        public static Dictionary<T, PortfolioDecision> Select<T>(
            IEnumerable<T> trades, int maxSlPerDay = 2, int minGapMinutes = 30, int graceMinutes = 0)
            where T : class, IPortfolioTrade
        {
            var result = new Dictionary<T, PortfolioDecision>();
            foreach (var t in trades) result[t] = new PortfolioDecision();

            // rules PER DIN lagti hain
            foreach (var dayGrp in trades.GroupBy(t => t.EntryTime.Date))
            {
                var ordered = dayGrp.OrderBy(x => x.EntryTime).ThenBy(x => x.Priority).ToList();
                DateTime? nextAllowed = null;   // is time se pehle naya entry nahi (last entry + gap)
                int slCount = 0;                // aaj ke SL

                int i = 0;
                while (i < ordered.Count)
                {
                    var t = ordered[i];

                    if (slCount >= maxSlPerDay)
                    { result[t].Skipped = true; result[t].SkipReason = "max2SL"; i++; continue; }

                    if (nextAllowed != null && t.EntryTime < nextAllowed.Value)
                    { result[t].Skipped = true; result[t].SkipReason = "gap30"; i++; continue; }

                    // t eligible hai. GRACE window [t, t+grace] me sabse high-priority (lowest
                    // Priority number) candidate dhoondo — wahi lo (t se pehle wale nahi ho sakte).
                    int bestIdx = i;
                    if (graceMinutes > 0)
                    {
                        var windowEnd = t.EntryTime.AddMinutes(graceMinutes);
                        for (int j = i + 1; j < ordered.Count; j++)
                        {
                            var u = ordered[j];
                            if (u.EntryTime > windowEnd) break;
                            // u bhi eligible hai (u.EntryTime > t.EntryTime >= nextAllowed; slCount same)
                            if (u.Priority < ordered[bestIdx].Priority) bestIdx = j;
                        }
                    }

                    // chosen se pehle ke lower-priority candidates ko skip (grace ne inhe overtake kiya)
                    for (int k = i; k < bestIdx; k++)
                    { result[ordered[k]].Skipped = true; result[ordered[k]].SkipReason = "gracePrio"; }

                    var chosen = ordered[bestIdx];
                    // chosen liya — agla entry kam se kam gap baad
                    nextAllowed = chosen.EntryTime.AddMinutes(minGapMinutes);
                    if (chosen.IsStopLoss) slCount++;
                    i = bestIdx + 1;
                }
            }
            return result;
        }
    }
}
