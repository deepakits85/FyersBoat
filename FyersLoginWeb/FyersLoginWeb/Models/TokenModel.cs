using System;

namespace FyersLoginWeb.Models
{
    // Fyers se mila token + metadata
    public class TokenModel
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
