using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using FyersLoginWeb.Models;
using FyersLoginWeb.Strategy;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace FyersLoginWeb.Services
{
    // Fyers v3 History API se historical candles laata hai.
    public class FyersHistoryService
    {
        private const string HistoryUrl = "https://api-t1.fyers.in/data/history";
        private static readonly TimeSpan IstOffset = new TimeSpan(5, 30, 0); // IST = UTC+5:30

        private readonly HttpClient _http;
        private readonly FyersSettings _cfg;
        private readonly DbService _db;

        public FyersHistoryService(HttpClient http, IOptions<FyersSettings> cfg, DbService db)
        {
            _http = http;
            _cfg = cfg.Value;
            _db = db;
        }

        /// <summary>
        /// Ek din ke candles laao. resolution: "3" = 3-min, "30" = 30-min, "1" = 1-min.
        /// Time IST wall-clock me convert hokar aata hai.
        /// </summary>
        private static readonly string CacheDir =
            Path.Combine(AppContext.BaseDirectory, "datacache");

        public async Task<List<Candle>> GetCandlesAsync(
            string symbol, string resolution, DateTime day, string accessToken)
        {
            // ---- Disk cache: ek baar fetch, phir file se load ----
            Directory.CreateDirectory(CacheDir);
            string safe = symbol.Replace(":", "_").Replace("/", "_");
            string cacheFile = Path.Combine(CacheDir, $"{safe}_{resolution}_{day:yyyy-MM-dd}.json");
            // sirf past dates cache karo (aaj ka data abhi ban raha hoga)
            bool cacheable = day.Date < DateTime.Now.Date;
            if (cacheable && File.Exists(cacheFile))
            {
                var cached = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Candle>>(
                    File.ReadAllText(cacheFile)) ?? new List<Candle>();
                SaveDb(symbol, resolution, day, cached); // cache se bhi DB me daalo (agar nahi hai)
                return cached;
            }

            string from = day.ToString("yyyy-MM-dd");
            string url = $"{HistoryUrl}?symbol={Uri.EscapeDataString(symbol)}" +
                         $"&resolution={resolution}&date_format=1" +
                         $"&range_from={from}&range_to={from}&cont_flag=1";

            HttpResponseMessage response = null!;
            string result = "";
            // 429 (rate limit) par retry + backoff
            for (int attempt = 0; attempt < 6; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Authorization", $"{_cfg.ClientId}:{accessToken}");
                response = await _http.SendAsync(request);
                result = await response.Content.ReadAsStringAsync();

                if ((int)response.StatusCode == 429)
                {
                    await Task.Delay(1000 * (attempt + 1)); // 1s,2s,3s...
                    continue;
                }
                break;
            }

            // expired/invalid option contract -> skip (khaali), abort nahi
            if (result.Contains("Invalid symbol", StringComparison.OrdinalIgnoreCase))
            {
                if (cacheable) File.WriteAllText(cacheFile, "[]");
                return new List<Candle>();
            }

            if (!response.IsSuccessStatusCode)
                throw new Exception($"History HTTP {(int)response.StatusCode}: {result}");

            var json = JObject.Parse(result);
            string status = json["s"]?.ToString() ?? "";
            var candles = new List<Candle>();

            // holiday / us din data nahi -> khaali list cache karke return
            if (status == "no_data")
            {
                if (cacheable) File.WriteAllText(cacheFile, "[]");
                return candles;
            }
            if (status != "ok")
                throw new Exception("History error: " + result);

            var arr = json["candles"] as JArray;
            if (arr == null) return candles;

            int resMinutes = int.TryParse(resolution, out int rm) ? rm : 3;

            foreach (var row in arr)
            {
                // [ epoch(UTC seconds), open, high, low, close, volume ]
                long epoch = row[0]!.Value<long>();
                DateTime startIst = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.Add(IstOffset);

                candles.Add(new Candle
                {
                    StartTime = startIst,
                    EndTime = startIst.AddMinutes(resMinutes),
                    Open = row[1]!.Value<decimal>(),
                    High = row[2]!.Value<decimal>(),
                    Low = row[3]!.Value<decimal>(),
                    Close = row[4]!.Value<decimal>()
                });
            }

            // cache (file) + DB save
            if (cacheable)
                File.WriteAllText(cacheFile, Newtonsoft.Json.JsonConvert.SerializeObject(candles));
            SaveDb(symbol, resolution, day, candles);

            return candles;
        }

        // DB me candles daalo agar us din ke pehle se nahi hain (idempotent, non-fatal)
        private void SaveDb(string symbol, string resolution, DateTime day, List<Candle> candles)
        {
            if (candles.Count == 0) return;
            try
            {
                // AAJ (live) ka data intraday badhta rehta hai -> hamesha upsert karo
                // (SaveCandles idempotent: sirf naye StartTime add karta). Purane POORE din
                // ka data ek hi baar save; DayExists se redundant re-save bachao.
                bool isToday = day.Date >= DateTime.Now.Date;
                if (isToday || !_db.DayExists(symbol, resolution, day))
                    _db.SaveCandles(symbol, resolution, candles, day.Date < DateTime.Now.Date ? "hist" : "live");
            }
            catch { /* DB optional */ }
        }

        /// <summary>
        /// Aaj se pichhle trading day ke 3-min candles dhoondo (weekend/holiday skip karke,
        /// max 7 din peeche). (day, candles) return karta hai.
        /// </summary>
        public async Task<(DateTime day, List<Candle> candles)> GetLastTradingDay3MinAsync(
            string symbol, string accessToken)
        {
            for (int back = 1; back <= 7; back++)
            {
                var day = DateTime.Now.Date.AddDays(-back);
                var candles = await GetCandlesAsync(symbol, "3", day, accessToken);
                if (candles.Count > 0)
                    return (day, candles);
            }
            return (DateTime.Now.Date.AddDays(-1), new List<Candle>());
        }
    }
}
