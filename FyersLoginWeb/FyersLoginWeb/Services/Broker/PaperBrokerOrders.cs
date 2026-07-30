using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FyersLoginWeb.Services.Broker
{
    /// <summary>
    /// PAPER broker — koi REAL order nahi jaata. Resting orders (SL-M / limit) ko fed candle
    /// prices ke against simulate karke fill karta hai, bilkul real broker jaisa — taaki
    /// PositionManager/ExitWatcher ka logic paper aur real me identical rahe.
    /// Fills evaluate karne ke liye har poll par MarkPrice(...) call karo.
    /// </summary>
    public class PaperBrokerOrders : IBrokerOrders
    {
        private readonly ILogger _log;
        private readonly List<BrokerOrder> _orders = new();
        private readonly Dictionary<string, BrokerPosition> _pos = new();
        private readonly object _lock = new();
        private int _seq = 0;

        public bool IsPaper => true;

        public PaperBrokerOrders(ILogger log) { _log = log; }

        private string NewId() => $"PAPER-{++_seq:D5}";

        public Task<string> PlaceBuyLimit(string s, int q, decimal p) => Add(s, OrderSide.Buy, OrderKind.Limit, q, p, 0);
        public Task<string> PlaceLimitSell(string s, int q, decimal p) => Add(s, OrderSide.Sell, OrderKind.Limit, q, p, 0);
        public Task<string> PlaceSlMarketSell(string s, int q, decimal t) => Add(s, OrderSide.Sell, OrderKind.StopLossMarket, q, 0, t);
        public Task<string> PlaceMarketSell(string s, int q) => Add(s, OrderSide.Sell, OrderKind.Market, q, 0, 0);

        private Task<string> Add(string sym, OrderSide side, OrderKind kind, int qty, decimal price, decimal trig)
        {
            lock (_lock)
            {
                var o = new BrokerOrder
                {
                    Id = NewId(), Symbol = sym, Side = side, Kind = kind,
                    Qty = qty, Price = price, Trigger = trig, PlacedAt = DateTime.Now
                };
                _orders.Add(o);
                string at = kind == OrderKind.StopLossMarket ? $"trig {trig}"
                          : kind == OrderKind.Market ? "MKT" : $"{price}";
                _log.LogInformation($"[PAPER] PLACE {o.Id} {side} {kind} {sym} x{qty} @ {at}");
                return Task.FromResult(o.Id);
            }
        }

        public Task CancelOrder(string id)
        {
            lock (_lock)
            {
                var o = _orders.FirstOrDefault(x => x.Id == id);
                if (o != null && o.Status == OrderStatus.Pending)
                {
                    o.Status = OrderStatus.Cancelled;
                    _log.LogInformation($"[PAPER] CANCEL {id} ({o.Kind} {o.Symbol})");
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerOrder>> GetOrders()
        {
            lock (_lock) { return Task.FromResult((IReadOnlyList<BrokerOrder>)_orders.Select(Clone).ToList()); }
        }

        public Task<IReadOnlyList<BrokerPosition>> GetPositions()
        {
            lock (_lock)
            {
                return Task.FromResult((IReadOnlyList<BrokerPosition>)_pos.Values
                    .Select(p => new BrokerPosition { Symbol = p.Symbol, NetQty = p.NetQty, AvgPrice = p.AvgPrice }).ToList());
            }
        }

        /// <summary>
        /// PAPER-only: naya 3-min candle aane par uske high/low/last se pending orders evaluate karo.
        /// - Buy limit: entry (backtest me ho chuka) → agle mark par limit price par fill.
        /// - Sell limit (target): high >= target par fill.
        /// - SL-M sell: low <= trigger par fill (market, trigger price par).
        /// - Market sell: turant last par fill.
        /// </summary>
        public void MarkPrice(string sym, decimal high, decimal low, decimal last, DateTime t)
        {
            lock (_lock)
            {
                foreach (var o in _orders.Where(x => x.Symbol == sym && x.Status == OrderStatus.Pending).ToList())
                {
                    bool fill = false; decimal fp = 0;
                    switch (o.Kind)
                    {
                        case OrderKind.Market:
                            fill = true; fp = last; break;
                        case OrderKind.Limit:
                            if (o.Side == OrderSide.Buy) { fill = true; fp = o.Price; }       // entry fills at signal price
                            else if (high >= o.Price) { fill = true; fp = o.Price; }          // target
                            break;
                        case OrderKind.StopLossMarket:
                            if (low <= o.Trigger) { fill = true; fp = o.Trigger; }            // SL hit
                            break;
                    }
                    if (fill)
                    {
                        o.Status = OrderStatus.Filled; o.FillPrice = fp; o.FillTime = t;
                        ApplyFill(o);
                        _log.LogInformation($"[PAPER] FILL  {o.Id} {o.Side} {o.Symbol} x{o.Qty} @ {fp}");
                    }
                }
            }
        }

        private void ApplyFill(BrokerOrder o)
        {
            if (!_pos.TryGetValue(o.Symbol, out var p)) { p = new BrokerPosition { Symbol = o.Symbol }; _pos[o.Symbol] = p; }
            if (p.NetQty == 0) p.AvgPrice = o.FillPrice;
            p.NetQty += o.Side == OrderSide.Buy ? o.Qty : -o.Qty;
            if (p.NetQty == 0) p.AvgPrice = 0;
        }

        private static BrokerOrder Clone(BrokerOrder o) => new BrokerOrder
        {
            Id = o.Id, Symbol = o.Symbol, Side = o.Side, Kind = o.Kind, Qty = o.Qty,
            Price = o.Price, Trigger = o.Trigger, Status = o.Status, FillPrice = o.FillPrice,
            FillTime = o.FillTime, PlacedAt = o.PlacedAt
        };
    }
}
