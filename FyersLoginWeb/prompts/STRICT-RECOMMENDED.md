# STRICT recommended live config (29 Jul 2026 findings)

Branch: **`cursor/strict-recommended-90e4`**

## COMPARE mode (kal live)

`UseCandleEntry=true` + `UseLiveEntry=true`

**Rules (dono modes):**
1. Breakout: 3m **Close > reference high** (confirm)
2. Confirm candle pe entry **nahi** (confirm close ke baad hi pata chalta hai)
3. **Uske baad** next candle/ticks pe price jab **ref high (entry)** pe aaye → entry

- **CLOSE** = next 3m candle pe Low<=refHigh dikhe (us candle close ke baad signal)  
- **TICK** = WaitingForEntry me LTP jab ref-high touch kare → turant @ ref high  
- Pehli → paper ENTRY; doosri → SHADOW  
- Log: `logs/live-compare-YYYY-MM-DD.log` (`HH:mm:ss.fff`)

## RAKHO (strategy rules)

| Setting | Value |
|---------|--------|
| Indices | Nifty + BankNifty |
| Refs | **11:15, 12:45** |
| Filters | **strict** |
| RR | **1:2** |
| 90 min wait | **ON** |
| Sensex / 10:45 | **OFF** |
| Breakout | **CLOSE confirm** |
| Entry | **Reference HIGH** |

## MAT RAKHO

- Sensex, 10:45, RR 1:1, 90-wait OFF, sirf-11:15, tick/wick entry (default)

## Code

- `Strategy/RecommendedLiveConfig.cs` — single source of truth
- `Services/LiveTradingService.cs` — live paper bot (strict wired)
- `Strategy/EntryFilterConfig.cs` → `ReduceStopLossStrictPreset()`
- `Services/FyersLiveFeed.cs` — tick feed available, `UseLiveEntry=false`

## CLI backtest

```bash
dotnet run --project FyersLoginWeb/BacktestToday -c Release -- \
  --mode index --from 2025-08-01 --to 2026-07-28 --filters strict \
  --refs 11:15,12:45 --ref-wait true
```
