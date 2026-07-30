using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FyersLoginWeb.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace FyersLoginWeb.Services
{
    // Fyers v3 Quotes API se LIVE last-traded-price (LTP) laata hai.
    // GET https://api-t1.fyers.in/data/quotes?symbols=SYM1,SYM2  (auth: {ClientId}:{token})
    // Response: { s:"ok", d:[ { n:"<sym>", v:{ lp:<ltp>, ... } }, ... ] }
    // Real-time entry ke liye — history API (3-min candle lag) ke bajaye seedha live price.
    public class FyersQuotesService
    {
        private const string QuotesUrl = "https://api-t1.fyers.in/data/quotes";
        private readonly HttpClient _http;
        private readonly FyersSettings _cfg;

        public FyersQuotesService(HttpClient http, IOptions<FyersSettings> cfg)
        {
            _http = http;
            _cfg = cfg.Value;
        }

        /// <summary>
        /// Diye gaye symbols ke live LTP laao -> symbol -> lp map.
        /// Fyers ek call me multiple symbols (comma-separated) leta hai.
        /// Koi symbol na mile / lp missing -> us symbol ko map me nahi daalte (caller skip kare).
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetLtpAsync(IEnumerable<string> symbols, string accessToken)
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var list = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (list.Count == 0) return result;

            string joined = string.Join(",", list);
            string url = $"{QuotesUrl}?symbols={Uri.EscapeDataString(joined)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"{_cfg.ClientId}:{accessToken}");
            var response = await _http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Quotes HTTP {(int)response.StatusCode}: {body}");

            var json = JObject.Parse(body);
            if ((json["s"]?.ToString() ?? "") != "ok") return result;

            if (json["d"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    string? name = item["n"]?.ToString();
                    var lpTok = item["v"]?["lp"];
                    if (string.IsNullOrEmpty(name) || lpTok == null || lpTok.Type == JTokenType.Null) continue;
                    decimal lp = lpTok.Value<decimal>();
                    if (lp > 0) result[name!] = lp;
                }
            }
            return result;
        }

        /// <summary>Ek symbol ka live LTP (na mile to null).</summary>
        public async Task<decimal?> GetLtpAsync(string symbol, string accessToken)
        {
            var map = await GetLtpAsync(new[] { symbol }, accessToken);
            return map.TryGetValue(symbol, out var lp) ? lp : (decimal?)null;
        }
    }
}
