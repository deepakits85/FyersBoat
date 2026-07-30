using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FyersLoginWeb.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace FyersLoginWeb.Services
{
    // Fyers API v3 ka poora auth flow (direct REST, SDK ki zaroorat nahi)
    public class FyersAuthService
    {
        private const string AuthCodeUrl = "https://api-t1.fyers.in/api/v3/generate-authcode";
        private const string TokenUrl = "https://api-t1.fyers.in/api/v3/validate-authcode";

        private readonly HttpClient _http;
        private readonly FyersSettings _cfg;

        public FyersAuthService(HttpClient http, IOptions<FyersSettings> cfg)
        {
            _http = http;
            _cfg = cfg.Value;
        }

        // 1) Login URL banao jispe user ko bheja jayega
        public string GetLoginUrl(string state = "sample")
        {
            return $"{AuthCodeUrl}" +
                   $"?client_id={Uri.EscapeDataString(_cfg.ClientId)}" +
                   $"&redirect_uri={Uri.EscapeDataString(_cfg.RedirectUri)}" +
                   $"&response_type=code" +
                   $"&state={Uri.EscapeDataString(state)}";
        }

        // 2) Callback me mila auth_code -> access token (JSON body zaroori hai)
        public async Task<TokenModel> GenerateAccessTokenAsync(string authCode)
        {
            string appIdHash = GenerateAppHashId(_cfg.ClientId, _cfg.SecretKey);

            var payload = new JObject
            {
                ["grant_type"] = "authorization_code",
                ["appIdHash"] = appIdHash,
                ["code"] = authCode
            };

            using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = content };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await _http.SendAsync(request);
            string result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)response.StatusCode}: {result}");

            var json = JObject.Parse(result);
            if (json["s"]?.ToString() != "ok")
                throw new Exception("Token Error: " + result);

            var token = new TokenModel
            {
                AccessToken = json["access_token"]?.ToString() ?? "",
                RefreshToken = json["refresh_token"]?.ToString() ?? "",
                CreatedAt = DateTime.Now
            };

            TokenStorage.Save(token);
            return token;
        }

        // 3) Saved token do agar valid hai warna null
        public TokenModel? GetValidToken()
        {
            var token = TokenStorage.Load();
            return TokenHelper.IsTokenExpired(token) ? null : token;
        }

        // appIdHash = sha256(clientId:secretKey) -> lowercase hex
        public static string GenerateAppHashId(string clientId, string secretKey)
        {
            string input = clientId + ":" + secretKey;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
