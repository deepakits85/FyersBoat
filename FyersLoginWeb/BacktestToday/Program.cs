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
var fromArg = GetArg("--from", "");
var toArg = GetArg("--to", "");
DateTime? fromDate = string.IsNullOrEmpty(fromArg) ? null : DateTime.Parse(fromArg).Date;
DateTime? toDate = string.IsNullOrEmpty(toArg) ? null : DateTime.Parse(toArg).Date;
var mode = GetArg("--mode", "options").ToLowerInvariant();
bool trailing = !GetArg("--trailing", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
int maxSl = int.Parse(GetArg("--maxsl", "2"));
int gap = int.Parse(GetArg("--gap", "30"));
string dumpPath = GetArg("--dump", "");
string filterPreset = GetArg("--filters", "none").ToLowerInvariant(); // none | reduce-sl
var refs = GetArg("--refs", "10:45,11:15,12:45")
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(s => TimeSpan.Parse(s.Trim()))
    .ToList();

EntryFilterConfig entryFilters = filterPreset switch
{
    "reduce-sl" or "reducesl" or "sl" => EntryFilterConfig.ReduceStopLossPreset(),
    "reduce-sl-strict" or "strict" => EntryFilterConfig.ReduceStopLossStrictPreset(),
    "reduce-sl-ultra" or "ultra" => EntryFilterConfig.ReduceStopLossUltraPreset(),
    _ => new EntryFilterConfig()
};

string root = FindAppRoot();
string appsettingsPath = Path.Combine(root, "FyersLoginWeb", "appsettings.json");
string tokenPath = Path.Combine(root, "FyersLoginWeb", "bin", "Debug", "net9.0", "token.json");
if (!File.Exists(tokenPath))
    tokenPath = Path.Combine(root, "token.json");
// Prefer existing web-app datacache (Release has multi-month index history)
string[] cacheDirs =
{
    Path.Combine(root, "FyersLoginWeb", "bin", "Release", "net9.0", "datacache"),
    Path.Combine(root, "FyersLoginWeb", "bin", "Debug", "net9.0", "datacache"),
    Path.Combine(AppContext.BaseDirectory, "datacache"),
};

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

string authCode = Environment.GetEnvironmentVariable("FYERS_AUTH_CODE") ?? GetArg("--auth-code", "");
if (!string.IsNullOrEmpty(authCode))
{
    Console.WriteLine("Exchanging auth_code for access token...");
    var exchanged = await ExchangeAuthCodeAsync(http, fyers, authCode);
    access = exchanged.AccessToken;
    refresh = exchanged.RefreshToken;
    Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
    File.WriteAllText(tokenPath, JsonConvert.SerializeObject(exchanged, Formatting.Indented));
    Console.WriteLine($"Token saved -> {tokenPath}");
}
else if (string.IsNullOrEmpty(access) || !await ProbeAsync(http, fyers.ClientId, access))
{
    string pin = Environment.GetEnvironmentVariable("FYERS_PIN") ?? GetArg("--pin", "");
    if (!string.IsNullOrEmpty(refresh) && !string.IsNullOrEmpty(pin))
    {
        Console.WriteLine("Refreshing access token via refresh_token...");
        try
        {
            var refreshed = await RefreshAsync(http, fyers, refresh, pin);
            access = refreshed.AccessToken;
            refresh = refreshed.RefreshToken;
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
            File.WriteAllText(tokenPath, JsonConvert.SerializeObject(refreshed, Formatting.Indented));
            Console.WriteLine($"Token refreshed + saved -> {tokenPath}");
        }
        catch (Exception ex) when (ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("SEBI", StringComparison.OrdinalIgnoreCase))
        {
            Fail("Fyers refresh API SEBI ki wajah se band hai. Naya login chahiye:\n" +
                 "1) Fyers login URL kholo, PIN se login karo\n" +
                 "2) Redirect URL se auth_code copy karo\n" +
                 "3) FYERS_AUTH_CODE=<code> ya --auth-code <code> dekar dubara chalao\n" +
                 "Detail: " + ex.Message);
        }
    }
    else
    {
        Fail("Access token expire. Fyers refresh API often disabled (SEBI). " +
             "FYERS_AUTH_CODE / --auth-code do (login redirect se), ya naya token.json push karo.");
    }
}

if (mode == "index" && (fromDate != null || toDate != null))
{
    var start = fromDate ?? day;
    var end = toDate ?? day;
    Console.WriteLine($"=== Index range {start:yyyy-MM-dd} -> {end:yyyy-MM-dd}  trailing={trailing}  refs={string.Join(",", refs)}  gap={gap}m maxSL={maxSl}  filters={entryFilters} ===\n");
    await RunIndexRangeAsync(http, fyers.ClientId, access, start, end, refs, trailing, maxSl, gap, cacheDirs, dumpPath, entryFilters);
}
else
{
    Console.WriteLine($"=== Backtest {day:yyyy-MM-dd}  mode={mode}  trailing={trailing}  refs={string.Join(",", refs)} ===\n");
    if (mode == "index")
        await RunIndexAsync(http, fyers.ClientId, access, day, refs, trailing, cacheDirs);
    else
        await RunOptionsAsync(http, fyers.ClientId, access, day, refs, trailing);
}

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

static async Task<TokenModel> ExchangeAuthCodeAsync(HttpClient http, FyersSettings fyers, string authCode)
{
    string hash = Sha256Hex($"{fyers.ClientId}:{fyers.SecretKey}");
    var payload = new JObject
    {
        ["grant_type"] = "authorization_code",
        ["appIdHash"] = hash,
        ["code"] = authCode.Trim()
    };
    using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
    using var resp = await http.PostAsync("https://api-t1.fyers.in/api/v3/validate-authcode", content);
    string body = await resp.Content.ReadAsStringAsync();
    var json = JObject.Parse(body);
    if (json["s"]?.ToString() != "ok")
        throw new Exception("Auth-code exchange failed: " + body);
    return new TokenModel
    {
        AccessToken = json["access_token"]?.ToString() ?? "",
        RefreshToken = json["refresh_token"]?.ToString() ?? "",
        CreatedAt = DateTime.Now
    };
}

static string Sha256Hex(string input)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static string CacheFileName(string symbol, string resolution, DateTime day)
{
    string safe = symbol.Replace(":", "_").Replace("/", "_");
    return $"{safe}_{resolution}_{day:yyyy-MM-dd}.json";
}

static List<Candle>? TryLoadCache(string[] cacheDirs, string symbol, string resolution, DateTime day)
{
    if (day.Date >= DateTime.Now.Date) return null; // today always live
    string name = CacheFileName(symbol, resolution, day);
    foreach (var dir in cacheDirs)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path)) continue;
        try
        {
            return JsonConvert.DeserializeObject<List<Candle>>(File.ReadAllText(path)) ?? new List<Candle>();
        }
        catch { /* try next */ }
    }
    return null;
}

static void SaveCache(string[] cacheDirs, string symbol, string resolution, DateTime day, List<Candle> candles)
{
    if (day.Date >= DateTime.Now.Date || candles.Count == 0) return;
    string dir = cacheDirs.FirstOrDefault(Directory.Exists)
                 ?? cacheDirs.Last();
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, CacheFileName(symbol, resolution, day)),
        JsonConvert.SerializeObject(candles));
}

static async Task<List<Candle>> GetCandlesAsync(HttpClient http, string clientId, string access,
    string symbol, string resolution, DateTime day, string[]? cacheDirs = null)
{
    if (cacheDirs != null)
    {
        var cached = TryLoadCache(cacheDirs, symbol, resolution, day);
        if (cached != null) return cached;
    }

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
        if (cacheDirs != null) SaveCache(cacheDirs, symbol, resolution, day, list);
        return list;
    }
    throw new Exception("History rate-limited");
}

static int PriorityOf(string sym)
{
    var u = sym.ToUpperInvariant();
    if (u.Contains("BANKNIFTY") || u.Contains("NIFTYBANK")) return 2;
    if (u.Contains("SENSEX")) return 1;
    if (u.Contains("NIFTY")) return 0;
    return 9;
}

static string ShortName(string sym) =>
    sym.Contains("BANK") ? "BankNifty" :
    sym.Contains("SENSEX") ? "Sensex" :
    sym.Contains("NIFTY") ? "Nifty" : sym;

static async Task RunIndexAsync(HttpClient http, string clientId, string access, DateTime day,
    List<TimeSpan> refs, bool trailing, string[] cacheDirs)
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
        var candles = await GetCandlesAsync(http, clientId, access, sym, "3", day, cacheDirs);
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
        await Task.Delay(120);
    }

    PrintTrades(all.OrderBy(t => t.Sig.EntryTime).Select(t =>
        $"{t.Sig.EntryTime:HH:mm}  {t.Sym,-22} {(t.Sig.IsLong ? "LONG " : "SHORT")}  " +
        $"entry {t.Sig.EntryPrice,8:F2}  SL {t.Sig.StopLoss,8:F2}  T {t.Sig.Target,8:F2}  " +
        $"{t.Sig.Outcome,-12} R={t.Sig.RealizedR,6:F2}  ref {t.Sig.Reference.StartTime:HH:mm}"));

    Summarize(all.Select(a => a.Sig));
}

static async Task RunIndexRangeAsync(HttpClient http, string clientId, string access,
    DateTime from, DateTime to, List<TimeSpan> refs, bool trailing, int maxSl, int gap, string[] cacheDirs,
    string dumpPath = "", EntryFilterConfig? entryFilters = null)
{
    entryFilters ??= new EntryFilterConfig();
    var legs = new[]
    {
        ("NSE:NIFTY50-INDEX", 2m, 0),
        ("BSE:SENSEX-INDEX", 3m, 1),
        ("NSE:NIFTYBANK-INDEX", 2m, 2),
    };

    var raw = new List<PfRow>();
    int daysWithData = 0, apiHits = 0, cacheHits = 0;

    for (var day = from; day <= to; day = day.AddDays(1))
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
        bool any = false;
        foreach (var (sym, rr, prio) in legs)
        {
            bool hadCache = TryLoadCache(cacheDirs, sym, "3", day) != null;
            var candles = await GetCandlesAsync(http, clientId, access, sym, "3", day, cacheDirs);
            if (hadCache) cacheHits++; else if (candles.Count > 0) apiHits++;
            if (candles.Count == 0) continue;
            any = true;

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
                    raw.Add(new PfRow(sym, ShortName(sym), prio, s));
            }
            if (!hadCache) await Task.Delay(80);
        }
        if (any)
        {
            daysWithData++;
            if (daysWithData % 10 == 0)
                Console.WriteLine($"  ... processed through {day:yyyy-MM-dd}  rawSignals={raw.Count}  cacheHits={cacheHits} apiHits={apiHits}");
        }
    }

    Console.WriteLine($"\nDays with data: {daysWithData}  cacheHits={cacheHits} apiHits={apiHits}  rawSignals={raw.Count}");

    int beforeFilter = raw.Count;
    raw = raw.Where(r => entryFilters.Allows(r.Sig, r.Name)).ToList();
    Console.WriteLine($"Entry filters [{entryFilters}]: {beforeFilter} -> {raw.Count} signals kept");

    var cands = raw.Select(r => new LiveCand(r.Sig, r.Priority)).ToList();
    var decisions = PortfolioSelector.Select(cands, maxSlPerDay: maxSl, minGapMinutes: gap);
    var taken = new List<PfRow>();
    for (int i = 0; i < raw.Count; i++)
        if (!decisions[cands[i]].Skipped) taken.Add(raw[i]);

    Console.WriteLine("\n--- MONTHLY TAKEN ---");
    Console.WriteLine($"{"Month",-10}{"#",5}{"NetR",8}{"Win%",6}{"Tgt",5}{"SL",5}{"Trail",6}{"SqOff",6}");
    foreach (var g in taken.GroupBy(t => t.Sig.EntryTime.ToString("yyyy-MM")).OrderBy(x => x.Key))
    {
        var list = g.ToList();
        int wins = list.Count(x => x.Sig.RealizedR > 0);
        int losses = list.Count(x => x.Sig.RealizedR < 0);
        int wr = (wins + losses) == 0 ? 0 : (int)Math.Round(100.0 * wins / (wins + losses));
        Console.WriteLine($"{g.Key,-10}{list.Count,5}{list.Sum(x => x.Sig.RealizedR),8:F2}{wr,5}%{list.Count(x => x.Sig.Outcome == TradeOutcome.TargetHit),5}{list.Count(x => x.Sig.Outcome == TradeOutcome.StopLossHit),5}{list.Count(x => x.Sig.Outcome == TradeOutcome.TrailStopHit),6}{list.Count(x => x.Sig.Outcome == TradeOutcome.TimeExit),6}");
    }

    Console.WriteLine("\n--- BY INDEX (TAKEN) ---");
    foreach (var g in taken.GroupBy(t => t.Name).OrderBy(x => PriorityOf(x.First().Sym)))
    {
        Console.Write($"{g.Key,-12} ");
        Summarize(g.Select(x => x.Sig));
    }

    Console.WriteLine("\nRAW summary:");
    Summarize(raw.Select(r => r.Sig));
    Console.WriteLine("\nTAKEN summary (portfolio rules applied):");
    Summarize(taken.Select(t => t.Sig));

    // last 15 taken trades for spot-check
    Console.WriteLine("\n--- LAST 15 TAKEN TRADES ---");
    PrintTrades(taken.OrderBy(t => t.Sig.EntryTime).TakeLast(15).Select(t =>
        $"{t.Sig.EntryTime:yyyy-MM-dd HH:mm}  {t.Name,-10} {(t.Sig.IsLong ? "LONG " : "SHORT")}  " +
        $"{t.Sig.Outcome,-12} R={t.Sig.RealizedR,6:F2}  ref {t.Sig.Reference.StartTime:HH:mm}"));

    if (!string.IsNullOrEmpty(dumpPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
        using var sw = new StreamWriter(dumpPath);
        sw.WriteLine("taken,sym,name,side,ref,entry,exit,weekday,month,refRange,risk,riskBps,minsAfterRef,minsHold,retrFirst,outcome,R,prio");
        for (int i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            var s = t.Sig;
            bool isTaken = !decisions[cands[i]].Skipped;
            bool retrFirst = s.FirstBreachTime == default;
            double minsAfterRef = (s.EntryTime - s.Reference.EndTime).TotalMinutes;
            double hold = s.OutcomeTime.HasValue ? (s.OutcomeTime.Value - s.EntryTime).TotalMinutes : -1;
            decimal riskBps = s.EntryPrice == 0 ? 0 : (s.Risk / s.EntryPrice) * 10000m;
            sw.WriteLine(string.Join(',',
                isTaken ? 1 : 0,
                t.Sym,
                t.Name,
                s.IsLong ? "LONG" : "SHORT",
                s.Reference.StartTime.ToString("HH:mm"),
                s.EntryTime.ToString("yyyy-MM-dd HH:mm"),
                s.OutcomeTime?.ToString("yyyy-MM-dd HH:mm") ?? "",
                s.EntryTime.DayOfWeek,
                s.EntryTime.ToString("yyyy-MM"),
                s.Reference.Range.ToString("F2"),
                s.Risk.ToString("F2"),
                riskBps.ToString("F2"),
                minsAfterRef.ToString("F0"),
                hold.ToString("F0"),
                retrFirst ? 1 : 0,
                s.Outcome,
                s.RealizedR.ToString("F3"),
                t.Priority));
        }
        Console.WriteLine($"\nDump written: {dumpPath} ({raw.Count} rows)");
    }
}

static async Task<(string expiryLabel, Dictionary<(long strike, string cepe), string> symbols)>
    LoadNearestChainAsync(HttpClient http, string clientId, string access, string index)
{
    string url = "https://api-t1.fyers.in/data/options-chain-v3" +
                 $"?symbol={Uri.EscapeDataString(index)}&strikecount=20";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.TryAddWithoutValidation("Authorization", $"{clientId}:{access}");
    using var resp = await http.SendAsync(req);
    string body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new Exception($"Option chain HTTP {(int)resp.StatusCode}: {body}");

    var json = JObject.Parse(body);
    var data = json["data"] as JObject ?? throw new Exception("Option chain: no data");
    var expiries = data["expiryData"] as JArray ?? new JArray();
    string label = expiries.FirstOrDefault()?["date"]?.ToString() ?? "?";
    string flag = expiries.FirstOrDefault()?["expiry_flag"]?.ToString() ?? "?";

    var map = new Dictionary<(long strike, string cepe), string>();
    foreach (var row in data["optionsChain"] as JArray ?? new JArray())
    {
        string sym = row["symbol"]?.ToString() ?? "";
        string ot = row["option_type"]?.ToString() ?? "";
        if (ot is not ("CE" or "PE")) continue;
        if (!long.TryParse(row["strike_price"]?.ToString(), out long strike)) continue;
        map[(strike, ot)] = sym;
    }
    Console.WriteLine($"  chain nearest expiry={label} ({flag}), strikes={map.Count / 2}");
    return ($"{label}/{flag}", map);
}

static string? ResolveOptionSymbol(Dictionary<(long strike, string cepe), string> map,
    long strike, string cepe, decimal step)
{
    if (map.TryGetValue((strike, cepe), out var exact)) return exact;
    // nearest available strike on chain
    var candidates = map.Keys.Where(k => k.cepe == cepe).Select(k => k.strike).ToList();
    if (candidates.Count == 0) return null;
    long nearest = candidates.OrderBy(s => Math.Abs(s - strike)).First();
    return map[(nearest, cepe)];
}

static async Task RunOptionsAsync(HttpClient http, string clientId, string access, DateTime day,
    List<TimeSpan> refs, bool trailing)
{
    // Live bot style: ATM CE/PE on monitoring-start spot, refs 10:45/11:15/12:45
    // Symbols come from Fyers options-chain (handles weekly YYMDD vs monthly YYMMM).
    var legs = new[]
    {
        new Leg("nifty", "NSE:NIFTY50-INDEX", "NSE:NIFTY", 2m, 50m, 0),
        new Leg("sensex", "BSE:SENSEX-INDEX", "BSE:SENSEX", 3m, 100m, 1),
        new Leg("bank", "NSE:NIFTYBANK-INDEX", "NSE:BANKNIFTY", 2m, 100m, 2),
    };

    var raw = new List<OptRow>();
    foreach (var leg in legs)
    {
        var idx = await GetCandlesAsync(http, clientId, access, leg.Index, "3", day);
        Console.WriteLine($"{leg.Index}: {idx.Count} candles");
        if (idx.Count == 0) continue;

        var (_, chain) = await LoadNearestChainAsync(http, clientId, access, leg.Index);

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
                string? sym = ResolveOptionSymbol(chain, strike, cepe, leg.Step);
                if (sym == null)
                {
                    Console.WriteLine($"    {leg.OptRoot} {strike}{cepe}: not on chain");
                    continue;
                }
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
sealed record PfRow(string Sym, string Name, int Priority, TradeSignal Sig);

sealed class LiveCand : IPortfolioTrade
{
    private readonly TradeSignal _s;
    public LiveCand(TradeSignal s, int prio) { _s = s; Priority = prio; }
    public DateTime EntryTime => _s.EntryTime;
    public DateTime ExitTime => _s.OutcomeTime ?? _s.EntryTime.Date.AddHours(15).AddMinutes(30);
    public int Priority { get; }
    public bool IsStopLoss => _s.Outcome == TradeOutcome.StopLossHit;
}
