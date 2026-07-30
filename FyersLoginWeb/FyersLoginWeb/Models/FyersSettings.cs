namespace FyersLoginWeb.Models
{
    // appsettings.json ke "Fyers" section se bind hota hai
    public class FyersSettings
    {
        public string ClientId { get; set; } = "";
        public string SecretKey { get; set; } = "";
        public string RedirectUri { get; set; } = "";
    }
}
