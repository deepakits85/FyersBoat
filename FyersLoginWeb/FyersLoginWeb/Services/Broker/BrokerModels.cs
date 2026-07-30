using System;

namespace FyersLoginWeb.Services.Broker
{
    public enum OrderSide { Buy, Sell }
    public enum OrderKind { Limit, Market, StopLossMarket }   // SL-M = stop-loss market
    public enum OrderStatus { Pending, Filled, Cancelled, Rejected }

    /// <summary>Ek broker order (paper ya real — dono ka same shape).</summary>
    public class BrokerOrder
    {
        public string Id { get; set; } = "";
        public string Symbol { get; set; } = "";
        public OrderSide Side { get; set; }
        public OrderKind Kind { get; set; }
        public int Qty { get; set; }
        public decimal Price { get; set; }      // limit price (0 for market)
        public decimal Trigger { get; set; }    // SL-M trigger price
        public OrderStatus Status { get; set; } = OrderStatus.Pending;
        public decimal FillPrice { get; set; }
        public DateTime? FillTime { get; set; }
        public DateTime PlacedAt { get; set; }
    }

    /// <summary>Net position (long premium: NetQty > 0).</summary>
    public class BrokerPosition
    {
        public string Symbol { get; set; } = "";
        public int NetQty { get; set; }
        public decimal AvgPrice { get; set; }
    }
}
