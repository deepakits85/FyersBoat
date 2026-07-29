using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FyersLoginWeb.Services.Broker
{
    /// <summary>
    /// Executor + ExitWatcher ek jagah. Signal aane par entry order deta hai, aur har poll (Tick)
    /// par exit design ke hisaab se manage karta hai:
    ///   ENTRY_PENDING: buy fill hua? -> SL-M (pehle, safety) + target limit place, State=OPEN
    ///   OPEN: target fill -> SL-M cancel (TargetHit); SL fill -> target cancel (StopLossHit);
    ///         2:45 -> dono cancel + market sell (TimeExit)
    /// Broker-agnostic — paper ya real, dono ke saath same.
    /// </summary>
    public class PositionManager
    {
        private readonly IBrokerOrders _broker;
        private readonly ILogger _log;
        private readonly TimeSpan _squareOff;
        private readonly List<ManagedPosition> _positions = new();
        private readonly object _lock = new();

        public PositionManager(IBrokerOrders broker, ILogger log, TimeSpan? squareOff = null)
        {
            _broker = broker;
            _log = log;
            _squareOff = squareOff ?? new TimeSpan(14, 45, 0);
        }

        public IReadOnlyList<ManagedPosition> Positions
        {
            get { lock (_lock) return _positions.ToList(); }
        }

        /// <summary>Naya taken signal -> entry order. Idempotent (same symbol+entryTime dobara nahi).</summary>
        public async Task OnSignal(string symbol, string side, int qty, decimal entry, decimal sl, decimal target, DateTime entryTime)
        {
            lock (_lock)
            {
                if (_positions.Any(p => p.Symbol == symbol && p.EntryTime == entryTime)) return; // already handled
            }
            var mp = new ManagedPosition
            {
                Symbol = symbol, Side = side, Qty = qty, Entry = entry, SL = sl, Target = target,
                EntryTime = entryTime, State = "ENTRY_PENDING"
            };
            mp.BuyId = await _broker.PlaceBuyLimit(symbol, qty, entry);
            lock (_lock) _positions.Add(mp);
            _log.LogInformation($"[EXEC] BUY {symbol} {side} x{qty} @ {entry}  (SL {sl}  T {target})");
        }

        /// <summary>Har poll par call karo — order/position status reconcile.</summary>
        public async Task Tick(DateTime now)
        {
            var orders = await _broker.GetOrders();
            var byId = orders.Where(o => o.Id != null).ToDictionary(o => o.Id);

            List<ManagedPosition> active;
            lock (_lock) active = _positions.Where(p => p.State != "CLOSED").ToList();

            foreach (var p in active)
            {
                if (p.State == "ENTRY_PENDING")
                {
                    if (p.BuyId != null && byId.TryGetValue(p.BuyId, out var bo))
                    {
                        if (bo.Status == OrderStatus.Filled)
                        {
                            // SAFETY PEHLE: SL-M, phir target
                            p.SlId = await _broker.PlaceSlMarketSell(p.Symbol, p.Qty, p.SL);
                            p.TargetId = await _broker.PlaceLimitSell(p.Symbol, p.Qty, p.Target);
                            p.State = "OPEN";
                            _log.LogInformation($"[EXEC] OPEN {p.Symbol}: SL-M @ {p.SL} + target @ {p.Target}");
                        }
                        else if (bo.Status == OrderStatus.Cancelled || bo.Status == OrderStatus.Rejected)
                        {
                            Close(p, "EntryFail", 0, now);
                        }
                    }
                }
                else if (p.State == "OPEN")
                {
                    bool tgt = p.TargetId != null && byId.TryGetValue(p.TargetId, out var to) && to.Status == OrderStatus.Filled;
                    bool sl = p.SlId != null && byId.TryGetValue(p.SlId, out var so) && so.Status == OrderStatus.Filled;

                    if (tgt)
                    {
                        await CancelIfPending(p.SlId, byId);
                        Close(p, "TargetHit", byId[p.TargetId!].FillPrice, now);
                    }
                    else if (sl)
                    {
                        await CancelIfPending(p.TargetId, byId);
                        Close(p, "StopLossHit", byId[p.SlId!].FillPrice, now);
                    }
                    else if (now.TimeOfDay >= _squareOff)
                    {
                        await CancelIfPending(p.SlId, byId);
                        await CancelIfPending(p.TargetId, byId);
                        p.ExitId = await _broker.PlaceMarketSell(p.Symbol, p.Qty);   // fill agle mark par
                        p.State = "EXITING";
                        _log.LogInformation($"[EXEC] 2:45 SQUARE-OFF {p.Symbol}: market sell placed");
                    }
                }
                else if (p.State == "EXITING")
                {
                    // square-off market sell fill hone ka intezaar
                    if (p.ExitId != null && byId.TryGetValue(p.ExitId, out var eo) && eo.Status == OrderStatus.Filled)
                        Close(p, "TimeExit", eo.FillPrice, now);
                }
            }
        }

        private async Task CancelIfPending(string? orderId, Dictionary<string, BrokerOrder> byId)
        {
            if (orderId != null && byId.TryGetValue(orderId, out var o) && o.Status == OrderStatus.Pending)
                await _broker.CancelOrder(orderId);
        }

        private void Close(ManagedPosition p, string outcome, decimal exitPrice, DateTime now)
        {
            p.State = "CLOSED"; p.Outcome = outcome; p.ExitPrice = exitPrice; p.ClosedAt = now;
            _log.LogInformation($"[EXEC] CLOSED {p.Symbol} -> {outcome}" +
                (outcome is "TargetHit" or "StopLossHit" or "TimeExit" ? $" @ {exitPrice} ({p.RealizedR:+0.##;-0.##}R)" : ""));
        }
    }
}
