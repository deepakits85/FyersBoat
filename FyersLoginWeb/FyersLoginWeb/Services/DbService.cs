using System;
using System.Collections.Generic;
using FyersLoginWeb.Strategy;
using Microsoft.Data.SqlClient;

namespace FyersLoginWeb.Services
{
    // SQL Server (TradingDB) — candles + signals store. DB/tables auto-create.
    public class DbService
    {
        private readonly string _conn;
        private readonly string _masterConn;

        public DbService(IConfiguration config)
        {
            _conn = config.GetConnectionString("TradingDb")
                    ?? "Server=.;Database=TradingDB;Trusted_Connection=True;TrustServerCertificate=True;";
            // master conn (DB banane ke liye)
            _masterConn = _conn.Replace("Database=TradingDB", "Database=master",
                StringComparison.OrdinalIgnoreCase);
        }

        public void EnsureCreated()
        {
            using (var m = new SqlConnection(_masterConn))
            {
                m.Open();
                new SqlCommand("IF DB_ID('TradingDB') IS NULL CREATE DATABASE TradingDB;", m).ExecuteNonQuery();
            }

            using var c = new SqlConnection(_conn);
            c.Open();
            new SqlCommand(@"
IF OBJECT_ID('dbo.Candles') IS NULL
CREATE TABLE dbo.Candles(
  Id BIGINT IDENTITY PRIMARY KEY,
  Symbol NVARCHAR(80) NOT NULL,
  Resolution NVARCHAR(5) NOT NULL,
  StartTime DATETIME2 NOT NULL,
  [Open] DECIMAL(18,2), High DECIMAL(18,2), Low DECIMAL(18,2), [Close] DECIMAL(18,2),
  Source NVARCHAR(12) NOT NULL DEFAULT('hist'),
  CONSTRAINT UQ_Candle UNIQUE(Symbol, Resolution, StartTime)
);
IF OBJECT_ID('dbo.Signals') IS NULL
CREATE TABLE dbo.Signals(
  Id BIGINT IDENTITY PRIMARY KEY,
  Symbol NVARCHAR(80) NOT NULL,
  Side NVARCHAR(10) NOT NULL,
  RefWindow NVARCHAR(20),
  EntryTime DATETIME2 NOT NULL,
  EntryPrice DECIMAL(18,2), StopLoss DECIMAL(18,2), Target DECIMAL(18,2), RR DECIMAL(6,2),
  Outcome NVARCHAR(20), OutcomeTime DATETIME2 NULL, OutcomePrice DECIMAL(18,2) NULL,
  Mode NVARCHAR(12) NOT NULL DEFAULT('backtest'),
  CreatedAt DATETIME2 NOT NULL DEFAULT(SYSDATETIME())
);", c).ExecuteNonQuery();
        }

        // Dashboard: recent signals
        public List<Models.DashSignal> GetRecentSignals(int limit)
        {
            var list = new List<Models.DashSignal>();
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand($@"SELECT TOP {limit} Symbol,Side,RefWindow,EntryTime,EntryPrice,StopLoss,Target,RR,
                Outcome,OutcomeTime,OutcomePrice,Mode FROM dbo.Signals ORDER BY EntryTime DESC, Id DESC", c);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new Models.DashSignal
                {
                    Symbol = rd["Symbol"].ToString()!,
                    Side = rd["Side"].ToString()!,
                    RefWindow = rd["RefWindow"] as string ?? "",
                    EntryTime = (DateTime)rd["EntryTime"],
                    EntryPrice = (decimal)rd["EntryPrice"],
                    StopLoss = (decimal)rd["StopLoss"],
                    Target = (decimal)rd["Target"],
                    RR = rd["RR"] is decimal rr ? rr : 0m,
                    Outcome = rd["Outcome"] as string ?? "",
                    OutcomeTime = rd["OutcomeTime"] as DateTime?,
                    OutcomePrice = rd["OutcomePrice"] as decimal?,
                    Mode = rd["Mode"] as string ?? ""
                });
            return list;
        }

        // ek signal delete (Symbol+Side+EntryTime se) — overlap cleanup ke liye
        public int DeleteSignal(string symbol, string side, DateTime entryTime)
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand(
                "DELETE FROM dbo.Signals WHERE Symbol=@s AND Side=@sd AND EntryTime=@et", c);
            cmd.Parameters.AddWithValue("@s", symbol);
            cmd.Parameters.AddWithValue("@sd", side);
            cmd.Parameters.AddWithValue("@et", entryTime);
            return cmd.ExecuteNonQuery();
        }

        // per-symbol latest candle time (live data freshness)
        public List<(string sym, DateTime last)> LatestCandleTimes()
        {
            var list = new List<(string, DateTime)>();
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand(@"SELECT Symbol, MAX(StartTime) mx FROM dbo.Candles
                WHERE Resolution='3' AND Symbol LIKE '%INDEX%' GROUP BY Symbol ORDER BY Symbol", c);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add((rd["Symbol"].ToString()!, (DateTime)rd["mx"]));
            return list;
        }

        // quick status (rows + latest signals)
        public string Status()
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            long candles = Convert.ToInt64(new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.Candles", c).ExecuteScalar());
            long sigs = Convert.ToInt64(new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.Signals", c).ExecuteScalar());
            long distinctSyms = Convert.ToInt64(new SqlCommand("SELECT COUNT_BIG(DISTINCT Symbol) FROM dbo.Candles", c).ExecuteScalar());
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Candles: {candles} rows, {distinctSyms} symbols");
            sb.AppendLine($"Signals: {sigs} rows");
            sb.AppendLine("--- latest 10 signals ---");
            var rd = new SqlCommand(@"SELECT TOP 10 Symbol,Side,EntryTime,EntryPrice,StopLoss,Target,Outcome,Mode
                FROM dbo.Signals ORDER BY Id DESC", c).ExecuteReader();
            while (rd.Read())
                sb.AppendLine($"{rd["Symbol"]} {rd["Side"]} {rd["EntryTime"]:HH:mm} @ {rd["EntryPrice"]} SL {rd["StopLoss"]} T {rd["Target"]} -> {rd["Outcome"]} [{rd["Mode"]}]");
            return sb.ToString();
        }

        // is symbol/resolution/din ke candles DB me hain?
        public bool DayExists(string symbol, string resolution, DateTime day)
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand(@"SELECT TOP 1 1 FROM dbo.Candles
                WHERE Symbol=@s AND Resolution=@r AND CAST(StartTime AS DATE)=@d;", c);
            cmd.Parameters.AddWithValue("@s", symbol);
            cmd.Parameters.AddWithValue("@r", resolution);
            cmd.Parameters.AddWithValue("@d", day.Date);
            return cmd.ExecuteScalar() != null;
        }

        // DB se candles wapas padho (backtest ke liye — token/disk-cache ki zaroorat nahi)
        public List<Candle> GetCandles(string symbol, string resolution, DateTime day)
        {
            var list = new List<Candle>();
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand(@"SELECT StartTime,[Open],High,Low,[Close] FROM dbo.Candles
                WHERE Symbol=@s AND Resolution=@r AND CAST(StartTime AS DATE)=@d ORDER BY StartTime;", c);
            cmd.Parameters.AddWithValue("@s", symbol);
            cmd.Parameters.AddWithValue("@r", resolution);
            cmd.Parameters.AddWithValue("@d", day.Date);
            int rm = int.TryParse(resolution, out var m) ? m : 3;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var st = (DateTime)rd["StartTime"];
                list.Add(new Candle
                {
                    StartTime = st,
                    EndTime = st.AddMinutes(rm),
                    Open = Convert.ToDecimal(rd["Open"]),
                    High = Convert.ToDecimal(rd["High"]),
                    Low = Convert.ToDecimal(rd["Low"]),
                    Close = Convert.ToDecimal(rd["Close"])
                });
            }
            return list;
        }

        // candles upsert (duplicate StartTime ignore)
        public void SaveCandles(string symbol, string resolution, IEnumerable<Candle> candles, string source)
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            foreach (var k in candles)
            {
                var cmd = new SqlCommand(@"
IF NOT EXISTS(SELECT 1 FROM dbo.Candles WHERE Symbol=@s AND Resolution=@r AND StartTime=@t)
INSERT INTO dbo.Candles(Symbol,Resolution,StartTime,[Open],High,Low,[Close],Source)
VALUES(@s,@r,@t,@o,@h,@l,@cl,@src);", c);
                cmd.Parameters.AddWithValue("@s", symbol);
                cmd.Parameters.AddWithValue("@r", resolution);
                cmd.Parameters.AddWithValue("@t", k.StartTime);
                cmd.Parameters.AddWithValue("@o", k.Open);
                cmd.Parameters.AddWithValue("@h", k.High);
                cmd.Parameters.AddWithValue("@l", k.Low);
                cmd.Parameters.AddWithValue("@cl", k.Close);
                cmd.Parameters.AddWithValue("@src", source);
                cmd.ExecuteNonQuery();
            }
        }

        // upsert: same (Symbol,Side,EntryTime) ho to outcome update, warna insert
        public void SaveSignal(string symbol, string side, string refWindow, TradeSignal s, string mode)
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            var cmd = new SqlCommand(@"
IF EXISTS(SELECT 1 FROM dbo.Signals WHERE Symbol=@sym AND Side=@side AND EntryTime=@et)
  UPDATE dbo.Signals SET Outcome=@oc, OutcomeTime=@ot, OutcomePrice=@op
  WHERE Symbol=@sym AND Side=@side AND EntryTime=@et;
ELSE
  INSERT INTO dbo.Signals(Symbol,Side,RefWindow,EntryTime,EntryPrice,StopLoss,Target,RR,Outcome,OutcomeTime,OutcomePrice,Mode)
  VALUES(@sym,@side,@rw,@et,@ep,@sl,@tg,@rr,@oc,@ot,@op,@mode);", c);
            cmd.Parameters.AddWithValue("@sym", symbol);
            cmd.Parameters.AddWithValue("@side", side);
            cmd.Parameters.AddWithValue("@rw", (object?)refWindow ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@et", s.EntryTime);
            cmd.Parameters.AddWithValue("@ep", s.EntryPrice);
            cmd.Parameters.AddWithValue("@sl", s.StopLoss);
            cmd.Parameters.AddWithValue("@tg", s.Target);
            cmd.Parameters.AddWithValue("@rr", s.RiskRewardRatio);
            cmd.Parameters.AddWithValue("@oc", s.Outcome.ToString());
            cmd.Parameters.AddWithValue("@ot", (object?)s.OutcomeTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@op", (object?)s.OutcomePrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mode", mode);
            cmd.ExecuteNonQuery();
        }
    }
}
