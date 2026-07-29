using System;
using FyersLoginWeb.Models;

namespace FyersLoginWeb.Services
{
    // Token expiry check
    public static class TokenHelper
    {
        public static bool IsTokenExpired(TokenModel? token)
        {
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                return true;

            // Reference jaisa: 8 ghante validity
            return (DateTime.Now - token.CreatedAt).TotalHours > 8;
        }
    }
}
