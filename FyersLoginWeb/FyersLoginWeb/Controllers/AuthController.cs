using System;
using System.Threading.Tasks;
using FyersLoginWeb.Models;
using FyersLoginWeb.Services;
using Microsoft.AspNetCore.Mvc;

namespace FyersLoginWeb.Controllers
{
    public class AuthController : Controller
    {
        private readonly FyersAuthService _auth;

        public AuthController(FyersAuthService auth)
        {
            _auth = auth;
        }

        // GET /Auth  -> login page (Fyers login link + manual paste form + token status)
        [HttpGet]
        public IActionResult Index()
        {
            ViewBag.LoginUrl = _auth.GetLoginUrl();
            var token = _auth.GetValidToken();
            return View(token);
        }

        // GET /Auth/Login -> user ko Fyers login page par bhej do
        [HttpGet]
        public IActionResult Login()
        {
            return Redirect(_auth.GetLoginUrl());
        }

        // POST /Auth/Manual -> user ne redirect URL se auth_code copy karke paste kiya
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manual(string authCode)
        {
            authCode = (authCode ?? "").Trim();
            if (string.IsNullOrEmpty(authCode))
            {
                TempData["Error"] = "Pehle redirect URL se mila 'auth_code' paste karo.";
                return RedirectToAction(nameof(Index));
            }

            await ExchangeAndStore(authCode);
            return RedirectToAction(nameof(Index));
        }

        // GET /Auth/Callback?auth_code=...  -> AUTO consume
        // (Sirf tab chalega jab redirect URI is app par point kare / localhost register ho)
        [HttpGet]
        public async Task<IActionResult> Callback(string? auth_code, string? code)
        {
            string authCode = (auth_code ?? code ?? "").Trim();
            if (string.IsNullOrEmpty(authCode))
            {
                TempData["Error"] = "Callback me auth_code nahi mila. Manual option use karo.";
                return RedirectToAction(nameof(Index));
            }

            await ExchangeAndStore(authCode);
            return RedirectToAction(nameof(Index));
        }

        // auth_code -> access token, phir message set karo
        private async Task ExchangeAndStore(string authCode)
        {
            try
            {
                await _auth.GenerateAccessTokenAsync(authCode);
                TempData["Message"] = "Login successful! Token save ho gaya (token.json).";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Token error: " + ex.Message;
            }
        }
    }
}
