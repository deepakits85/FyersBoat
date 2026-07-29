using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FyersCSharpSDK;
using HyperSyncLib;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FyersLoginWeb.Services
{
    // LIVE tick feed via official Fyers C# SDK (fyers-api-v3) — HSM WebSocket, LITE mode (LTP).
    // Reference: E:\Project\Backup\FyersTradeApp (working per-tick). Ticks OnScrips me aate hain.
    // History API (3-min candle lag) ki jagah — real-time entry ke liye seedha live LTP.
    public class FyersLiveFeed : FyersSocketDelegate
    {
        private readonly ILogger<FyersLiveFeed> _log;
        private FyersSocket? _client;

        // symbol -> latest LTP (thread-safe; OnScrips background thread se aata hai)
        private readonly ConcurrentDictionary<string, decimal> _ltp =
            new(StringComparer.OrdinalIgnoreCase);

        // har tick pe callback (entry-logic isse level-cross check karega). symbol, ltp.
        public event Action<string, decimal>? OnTick;

        // ---- LEVEL WATCH: ek strike ka level watch karo, live LTP cross karte hi ek baar fire ----
        // Above=true -> ltp >= level pe fire (breakout up/CE); Above=false -> ltp <= level (down/PE).
        public sealed record LevelWatch(string Symbol, decimal Level, bool Above, DateTime ArmedAt,
                                        Action<string, decimal> OnCross);
        private readonly ConcurrentDictionary<string, LevelWatch> _watches =
            new(StringComparer.OrdinalIgnoreCase);
        public string LastCrossInfo { get; private set; } = "";
        public IEnumerable<LevelWatch> ActiveWatches => _watches.Values;

        public bool Connected { get; private set; }
        public DateTime LastTickAt { get; private set; }
        public long TickCount { get; private set; }

        public FyersLiveFeed(ILogger<FyersLiveFeed> log) { _log = log; }

        public decimal? Ltp(string symbol) => _ltp.TryGetValue(symbol, out var v) ? v : (decimal?)null;
        public IReadOnlyDictionary<string, decimal> AllLtp => _ltp;

        /// <summary>Feed shuru karo — connect + given symbols subscribe (LITE/LTP mode).</summary>
        public async Task StartAsync(string clientId, string accessToken, List<string> symbols)
        {
            try
            {
                var fyers = FyersClass.Instance;
                fyers.ClientId = clientId;
                fyers.AccessToken = accessToken;

                _client = new FyersSocket();
                _client.webSocketDelegate = this;

                await _client.Connect();
                _client.ConnectHSM(ChannelModes.LITE);   // LITE = sirf LTP (halka)
                _client.SubscribeData(symbols);

                _log.LogInformation($"LiveFeed: subscribe {symbols.Count} symbols -> {string.Join(",", symbols)}");
            }
            catch (Exception ex)
            {
                _log.LogError($"LiveFeed StartAsync error: {ex.Message}");
            }
        }

        public void Subscribe(List<string> symbols)
        {
            try { _client?.SubscribeData(symbols); _log.LogInformation($"LiveFeed: +subscribe {string.Join(",", symbols)}"); }
            catch (Exception ex) { _log.LogError($"LiveFeed Subscribe error: {ex.Message}"); }
        }

        /// <summary>
        /// Ek strike ka LEVEL watch karo — jaise hi live LTP level cross kare, ek baar OnCross fire.
        /// Symbol ko live feed pe subscribe bhi kar deta hai. (Real-time entry ka core trigger.)
        /// </summary>
        public void WatchLevel(string symbol, decimal level, bool above, Action<string, decimal> onCross)
        {
            _watches[symbol] = new LevelWatch(symbol, level, above, DateTime.Now, onCross);
            Subscribe(new List<string> { symbol });
            _log.LogInformation($"LiveFeed WATCH armed: {symbol} {(above ? ">=" : "<=")} {level}");
        }

        public void ClearWatch(string symbol) => _watches.TryRemove(symbol, out _);

        // ---- FyersSocketDelegate ----
        public void OnOpen(string status) { Connected = true; _log.LogInformation($"LiveFeed OPEN: {status}"); }
        public void OnClose(string status) { Connected = false; _log.LogInformation($"LiveFeed CLOSE: {status}"); }
        public void OnError(JObject error) => _log.LogWarning($"LiveFeed ERROR: {error}");
        public void OnMessage(JObject response) { }

        public void OnScrips(JObject scrips)
        {
            try
            {
                var data = scrips["data"];
                if (data == null) return;
                string? symbol = data["symbol"]?.ToString();
                var lpTok = data["ltp"];
                if (string.IsNullOrEmpty(symbol) || lpTok == null) return;
                decimal ltp = lpTok.ToObject<decimal>();
                if (ltp <= 0) return;

                _ltp[symbol!] = ltp;
                LastTickAt = DateTime.Now;
                TickCount++;
                OnTick?.Invoke(symbol!, ltp);

                // LEVEL WATCH: cross hua? -> ek baar fire karke watch hata do
                if (_watches.TryGetValue(symbol!, out var w))
                {
                    bool crossed = w.Above ? ltp >= w.Level : ltp <= w.Level;
                    if (crossed && _watches.TryRemove(symbol!, out _))
                    {
                        double waitSec = (DateTime.Now - w.ArmedAt).TotalSeconds;
                        LastCrossInfo = $"{DateTime.Now:HH:mm:ss} {symbol} CROSSED {(w.Above ? ">=" : "<=")}{w.Level} @ LTP {ltp} (armed {waitSec:F1}s pehle)";
                        _log.LogInformation($"LiveFeed CROSS -> ENTER {symbol} @ {ltp} (level {w.Level}, {waitSec:F1}s)");
                        try { w.OnCross(symbol!, ltp); } catch (Exception ex) { _log.LogError($"OnCross error: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning($"LiveFeed OnScrips parse error: {ex.Message}"); }
        }

        // (baaki delegate members — abhi zaroorat nahi)
        public void OnOrder(JObject orders) { }
        public void OnTrade(JObject trades) { }
        public void OnPosition(JObject positions) { }
        public void OnIndex(JObject index) { }
        public void OnDepth(JObject depths) { }
    }
}
