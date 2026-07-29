using System;
using System.IO;
using FyersLoginWeb.Models;
using Newtonsoft.Json;

namespace FyersLoginWeb.Services
{
    // Token ko local JSON file me save/load karta hai (single-user setup ke liye theek)
    public static class TokenStorage
    {
        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "token.json");

        public static void Save(TokenModel token)
        {
            string json = JsonConvert.SerializeObject(token, Formatting.Indented);
            File.WriteAllText(FilePath, json);
        }

        public static TokenModel? Load()
        {
            if (!File.Exists(FilePath))
                return null;

            string json = File.ReadAllText(FilePath);
            return JsonConvert.DeserializeObject<TokenModel>(json);
        }
    }
}
