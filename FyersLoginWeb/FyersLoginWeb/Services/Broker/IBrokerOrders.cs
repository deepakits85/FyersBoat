using System.Collections.Generic;
using System.Threading.Tasks;

namespace FyersLoginWeb.Services.Broker
{
    /// <summary>
    /// Broker order API abstraction. Paper (simulate) aur real Fyers dono isko implement karenge —
    /// isliye ExitWatcher/PositionManager logic dono modes me BILKUL same rehta hai.
    /// </summary>
    public interface IBrokerOrders
    {
        bool IsPaper { get; }

        Task<string> PlaceBuyLimit(string symbol, int qty, decimal price);      // entry
        Task<string> PlaceSlMarketSell(string symbol, int qty, decimal trigger); // SL-M safety
        Task<string> PlaceLimitSell(string symbol, int qty, decimal price);      // target
        Task<string> PlaceMarketSell(string symbol, int qty);                    // 2:45 square-off

        Task CancelOrder(string orderId);
        Task<IReadOnlyList<BrokerOrder>> GetOrders();
        Task<IReadOnlyList<BrokerPosition>> GetPositions();
    }
}
