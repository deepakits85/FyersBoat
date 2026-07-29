using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FyersLoginWeb.Models;
using FyersLoginWeb.Strategy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Usage:
//   FYERS_PIN=xxxx dotnet run --project BacktestToday -- --date 2026-07-29
//   FYERS_ACCESS_TOKEN=... dotnet run --project BacktestToday -- --date 2026-07-29
// Optional: --mode index|options  (default options)  --trailing true|false

var argsList = args.ToList();
string GetArg(string name, string def)
{
    int i = argsList.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : def;
}

var day = DateTime.Parse(GetArg("--date", DateTime.Now.ToString("yyyy-MM-dd"))).Date;
var mode = GetArg("--mode", "options").ToLowerInvariant();
bool trailing = !GetArg("--trailing", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
var refs = GetArg("--refs", "10:45,11:15,12:45")
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(s => TimeSpan.Parse(s.Trim()))
    .ToList();

string root = FindAppRoot();
string appsettingsPath = Path.Combine(root, "FyersLoginWeb", "appsettings.json");
string tokenPath = Path.Combine(root, "FyersLoginWeb", "bin", "Debug", "net9.0", "token.json");
if (!File.Exists(tokenPath))
    tokenPath = Path.Combine(root, "token.json");

var cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(appsettingsPath))
    ?? throw new Exception("appsettings.json parse fail");
var fyers = cfg.Fyers ?? throw new Exception("Fyers section missing");

using var http = new HttpClient();
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.UserAgent.ParseAdd("FyersBoat-BacktestToday/1.0");

string access = Environment.GetEnvironmentVariable("FYERS_ACCESS_TOKEN") ?? "";
string refresh = "";
TokenModel? saved = null;
if (File.Exists(tokenPath))
{
    saved = JsonConvert.DeserializeObject<TokenModel>(File.ReadAllText(tokenPath));
    if (string.IsNullOrEmpty(access)) access = saved?.AccessToken ?? "";
    refresh = saved?.RefreshToken ?? "";
}

if (string.IsNullOrEmpty(access) || !await ProbeAsync(http, fyers.ClientId, access))
{
    string pin = Environment.GetEnvironmentVariable("FYERS_PIN") ?? GetArg("--pin", "");
    if (string.IsNullOrEmpty(refresh))
        Fail("Access token invalid/expired aur refresh token nahi mila. Local /Auth login karke token.json push karo.");
    if (string.IsNullOrEmpty(pin))
        Fail("Access token expire. Refresh token valid hai — FYERS_PIN=yourpin set karke dubara chalao, ya naya access token do.");

    Console.WriteLine("Refreshing access token via refresh_token...");
    var refreshed = await RefreshAsync(http, fyers, refresh, pin);
    access = refreshed.AccessToken;
    refresh = refreshed.RefreshToken;
    Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
    File.WriteAllText(tokenPath, JsonConvert.SerializeObject(refreshed, Formatting.Indented));
    Console.WriteLine($"Token refreshed + saved -> {tokenPath}");
}

Console.WriteLine($"=== Backtest {day:yyyy-MM-dd}  mode={mode}  trailing={trailing}  refs={string.Join(",", refs)} ===\n");

if (mode == "index")
    await RunIndexAsync(http, fyers.ClientId, access, day, refs, trailing);
else
    await RunOptionsAsync(http, fyers.ClientId, access, day, refs, trailing);

static void Fail(string msg)
{
    Console.Error.WriteLine("ERROR: " + msg);
    Environment.Exit(2);
}

static string FindAppRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "FyersLoginWeb")) &&
            File.Exists(Path.Combine(dir.FullName, "FyersLoginWeb", "appsettings.json")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "appsettings.json")) &&
            dir.Name == "FyersLoginWeb")
            return dir.Parent!.FullName;
        dir = dir.Parent;
    }
    // repo layout: /workspace/FyersLoginWeb/BacktestToday
    var cwd = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(cwd, "FyersLoginWeb", "appsettings.json")))
        return cwd;
    if (File.Exists(Path.Combine(cwd, "..", "FyersLoginWeb", "appsettings.json")))
        return Path.GetFullPath(Path.Combine(cwd, ".."));
    if (File.Exists(Path.Combine(cwd, "appsettings.json")))
        return Path.GetFullPath(Path.Combine(cwd, ".."));
    return "/workspace/FyersLoginWeb";
}

static async Task<bool> ProbeAsync(HttpClient http, string clientId, string access)
{
    try
    {
        var c = await GetCandlesAsync(http, clientId, access, "NSE:NIFTY50-INDEX", "3", DateTime.Today);
        return c.Count >= 0; // auth ok even on holiday empty
    }
    catch (Exception ex)
    {
        Console.WriteLine("Probe failed: " + ex.Message);
        return false;
    }
}

static async Task<TokenModel> RefreshAsync(HttpClient http, FyersSettings fyers, string refreshToken, string pin)
{
    string hash = Sha256Hex($"{fyers.ClientId}:{fyers.SecretKey}");
    var payload = new JObject
    {
        ["grant_type"] = "refresh_token",
        ["appIdHash"] = hash,
        ["refresh_token"] = refreshToken,
        ["pin"] = pin
    };
    using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
    using var resp = await http.PostAsync("https://api-t1.fyers.in/api/v3/validate-refresh-token", content);
    string body = await resp.Content.ReadAsStringAsync();
    var json = JObject.Parse(body);
    if (json["s"]?.ToString() != "ok")
        throw new Exception("Refresh failed: " + body);
    return new TokenModel
    {
        AccessToken = json["access_token"]?.ToString() ?? "",
        RefreshToken = json["refresh_token"]?.ToString() ?? refreshToken,
        CreatedAt = DateTime.Now
    };
}

static string Sha256Hex(string input)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static async Task<List<Candle>> GetCandlesAsync(HttpClient http, string clientId, string access,
    string symbol, string resolution, DateTime day)
{
    string from = day.ToString("yyyy-MM-dd");
    string url = "https://api-t1.fyers.in/data/history" +
                 $"?symbol={Uri.EscapeDataString(symbol)}&resolution={resolution}&date_format=1" +
                 $"&range_from={from}&range_to={from}&cont_flag=1";

    for (int attempt = 0; attempt < 6; attempt++)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"{clientId}:{access}");
        using var resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        if ((int)resp.StatusCode == 429)
        {
            await Task.Delay(1000 * (attempt + 1));
            continue;
        }
        if (body.Contains("Invalid symbol", StringComparison.OrdinalIgnoreCase))
            return new List<Candle>();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"History HTTP {(int)resp.StatusCode}: {body}");

        var json = JObject.Parse(body);
        string status = json["s"]?.ToString() ?? "";
        if (status == "no_data") return new List<Candle>();
        if (status != "ok") throw new Exception("History error: " + body);

        var arr = json["candles"] as JArray ?? new JArray();
        int resMinutes = int.TryParse(resolution, out int rm) ? rm : 3;
        var ist = TimeSpan.FromHours(5.5);
        var list = new List<Candle>();
        foreach (var row in arr)
        {
            long epoch = row[0]!.Value<long>();
            DateTime startIst = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.Add(ist);
            list.Add(new Candle
            {
                StartTime = startIst,
                EndTime = startIst.AddMinutes(resMinutes),
                Open = row[1]!.Value<decimal>(),
                High = row[2]!.Value<decimal>(),
                Low = row[3]!.Value<decimal>(),
                Close = row[4]!.Value<decimal>()
            });
        }
        return list;
    }
    throw new Exception("History rate-limited");
}

static async Task RunIndexAsync(HttpClient http, string clientId, string access, DateTime day,
    List<TimeSpan> refs, bool trailing)
{
    var legs = new[]
    {
        ("NSE:NIFTY50-INDEX", 2m),
        ("BSE:SENSEX-INDEX", 3m),
        ("NSE:NIFTYBANK-INDEX", 2m),
    };

    var all = new List<IndexRow>();
    foreach (var (sym, rr) in legs)
    {
        var candles = await GetCandlesAsync(http, clientId, access, sym, "3", day);
        Console.WriteLine($"{sym}: {candles.Count} x 3m candles");
        if (candles.Count == 0) continue;

        var config = new StrategyConfig
        {
            EntryBufferPoints = 0m,
            StopLossBufferPoints = 0m,
            RiskRewardRatio = rr,
            UseTrailing = trailing,
            UseSquareOff = true,
            UseReferenceMaxWait = true
        };
        config.SetRetracementFromPercentage(50m);

        foreach (var rs in refs)
        {
            var res = StrategyBacktester.Run(candles, config, rs);
            foreach (var s in res.BuySignals.Concat(res.SellSignals))
                all.Add(new IndexRow(sym, s));
        }
        await Task.Delay(200);
    }

    PrintTrades(all.OrderBy(t => t.Sig.EntryTime).Select(t =>
        $"{t.Sig.EntryTime:HH:mm}  {t.Sym,-22} {(t.Sig.IsLong ? "LONG " : "SHORT")}  " +
        $"entry {t.Sig.EntryPrice,8:F2}  SL {t.Sig.StopLoss,8:F2}  T {t.Sig.Target,8:F2}  " +
        $"{t.Sig.Outcome,-12} R={t.Sig.RealizedR,6:F2}  ref {t.Sig.Reference.StartTime:HH:mm}"));

    Summarize(all.Select(a => a.Sig));
}

static async Task RunOptionsAsync(HttpClient http, string clientId, string access, DateTime day,
    List<TimeSpan> refs, bool trailing)
{
    // Live bot style: ATM CE/PE on monitoring-start spot, refs 10:45/11:15/12:45
    var legs = new[]
    {
        new Leg("nifty", "NSE:NIFTY50-INDEX", "NSE:NIFTY", 2m, 50m, 0),
        new Leg("sensex", "BSE:SENSEX-INDEX", "BSE:SENSEX", 3m, 100m, 1),
        new Leg("bank", "NSE:NIFTYBANK-INDEX", "NSE:BANKNIFTY", 2m, 100m, 2),
    };

    string yy = day.ToString("yy");
    string mmm = day.ToString("MMM", CultureInfo.InvariantCulture).ToUpper();

    var raw = new List<OptRow>();
    foreach (var leg in legs)
    {
        var idx = await GetCandlesAsync(http, clientId, access, leg.Index, "3", day);
        Console.WriteLine($"{leg.Index}: {idx.Count} candles");
        if (idx.Count == 0) continue;

        var config = new StrategyConfig
        {
            EntryBufferPoints = 0m,
            StopLossBufferPoints = 0m,
            RiskRewardRatio = leg.RR,
            UseTrailing = trailing,
            UseSquareOff = true,
            UseReferenceMaxWait = true
        };
        config.SetRetracementFromPercentage(50m);

        foreach (var rs in refs)
        {
            var monStart = rs.Add(TimeSpan.FromMinutes(30));
            var spotCandle = idx.FirstOrDefault(c => c.StartTime.TimeOfDay == monStart);
            if (spotCandle == null)
            {
                Console.WriteLine($"  skip ref {rs:hh\\:mm} — no spot candle at {monStart:hh\\:mm}");
                continue;
            }
            long strike = (long)(Math.Round(spotCandle.Open / leg.Step) * leg.Step);
            Console.WriteLine($"  ref {rs:hh\\:mm} spot@{monStart:hh\\:mm}={spotCandle.Open:F0} -> strike {strike}");

            foreach (var cepe in new[] { "CE", "PE" })
            {
                string sym = $"{leg.OptRoot}{yy}{mmm}{strike}{cepe}";
                var opt = await GetCandlesAsync(http, clientId, access, sym, "3", day);
                await Task.Delay(250);
                if (opt.Count == 0)
                {
                    Console.WriteLine($"    {sym}: no data");
                    continue;
                }
                var res = StrategyBacktester.Run(opt, config, rs);
                foreach (var s in res.BuySignals) // options: long premium (BUY signals)
                    raw.Add(new OptRow(sym, cepe, leg.OptRoot + "|" + cepe, leg.Priority, s));
                Console.WriteLine($"    {sym}: {opt.Count} bars, buys={res.BuySignals.Count}");
            }
        }
    }

    // overlap dedup (same underlying+CE/PE)
    var openUntil = new Dictionary<string, DateTime>();
    var survivors = new List<OptRow>();
    foreach (var t in raw.OrderBy(x => x.Sig.EntryTime))
    {
        if (openUntil.TryGetValue(t.Dkey, out var busy) && t.Sig.EntryTime <= busy) continue;
        openUntil[t.Dkey] = t.Sig.OutcomeTime ?? day.AddHours(15).AddMinutes(30);
        survivors.Add(t);
    }

    var cands = survivors.Select(x => new LiveCand(x.Sig, x.Priority)).ToList();
    var decisions = PortfolioSelector.Select(cands, maxSlPerDay: 4, minGapMinutes: 30);
    var taken = new List<OptRow>();
    for (int i = 0; i < survivors.Count; i++)
        if (!decisions[cands[i]].Skipped) taken.Add(survivors[i]);

    Console.WriteLine("\n--- RAW signals (after overlap dedup) ---");
    PrintTrades(survivors.Select(FormatOpt));
    Console.WriteLine("\n--- TAKEN (portfolio: 30m gap, max 4 SL/day, Nifty>Sensex>Bank) ---");
    PrintTrades(taken.Select(FormatOpt));
    Console.WriteLine("\nRAW summary:");
    Summarize(survivors.Select(s => s.Sig));
    Console.WriteLine("\nTAKEN summary:");
    Summarize(taken.Select(s => s.Sig));
}

static string FormatOpt(OptRow t) =>
    $"{t.Sig.EntryTime:HH:mm}  {t.Sym,-28} {t.Cepe}  entry {t.Sig.EntryPrice,7:F2}  " +
    $"SL {t.Sig.StopLoss,7:F2}  T {t.Sig.Target,7:F2}  {t.Sig.Outcome,-12} R={t.Sig.RealizedR,6:F2}  " +
    $"ref {t.Sig.Reference.StartTime:HH:mm}";

static void PrintTrades(IEnumerable<string> lines)
{
    var list = lines.ToList();
    if (list.Count == 0) { Console.WriteLine("(none)"); return; }
    foreach (var l in list) Console.WriteLine(l);
}

static void Summarize(IEnumerable<TradeSignal> signals)
{
    var list = signals.ToList();
    int n = list.Count;
    decimal net = list.Sum(s => s.RealizedR);
    int tgt = list.Count(s => s.Outcome == TradeOutcome.TargetHit);
    int sl = list.Count(s => s.Outcome == TradeOutcome.StopLossHit);
    int trail = list.Count(s => s.Outcome == TradeOutcome.TrailStopHit);
    int sq = list.Count(s => s.Outcome == TradeOutcome.TimeExit);
    int open = list.Count(s => s.Outcome == TradeOutcome.Open);
    int wins = list.Count(s => s.RealizedR > 0);
    int losses = list.Count(s => s.RealizedR < 0);
    int wr = (wins + losses) == 0 ? 0 : (int)Math.Round(100.0 * wins / (wins + losses));
    Console.WriteLine($"Trades={n}  NetR={net:F2}  Win%={wr}  Target={tgt} SL={sl} Trail={trail} SqOff={sq} Open={open}");
}

sealed class AppConfig
{
    public FyersSettings? Fyers { get; set; }
}

sealed record Leg(string Code, string Index, string OptRoot, decimal RR, decimal Step, int Priority);
sealed record IndexRow(string Sym, TradeSignal Sig);
sealed record OptRow(string Sym, string Cepe, string Dkey, int Priority, TradeSignal Sig);

sealed class LiveCand : IPortfolioTrade
{
    private readonly TradeSignal _s;
    public LiveCand(TradeSignal s, int prio) { _s = s; Priority = prio; }
    public DateTime EntryTime => _s.EntryTime;
    public DateTime ExitTime => _s.OutcomeTime ?? _s.EntryTime.Date.AddHours(15).AddMinutes(30);
    public int Priority { get; }
    public bool IsStopLoss => _s.Outcome == TradeOutcome.StopLossHit;
}
