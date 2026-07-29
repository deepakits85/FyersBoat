# STRICT recommended live config (29 Jul 2026 findings)

Branch: **`cursor/strict-recommended-90e4`**

## COMPARE mode (kal live)

`UseCandleEntry=true` + `UseLiveEntry=true` — dono chalenge.

- Pehli jo fire kare → **paper ENTRY**
- Doosri → **SHADOW** log (timing compare)
- Log file: `bin/.../logs/live-compare-YYYY-MM-DD.log`  
  columns: `HH:mm:ss.fff`, mode (`CLOSE_ENTRY` / `TICK_ENTRY` / `*_SHADOW` / `TICK_ARM`), symbol, detail

Events: `TICK_ARM`, `TICK_ENTRY`, `TICK_SHADOW`, `CLOSE_ENTRY`, `CLOSE_SHADOW`, `CLOSE_STALE`

## RAKHO (strategy rules same)

| Setting | Value |
|---------|--------|
| Indices | Nifty + BankNifty |
| Refs | **11:15, 12:45** |
| Filters | **strict** |
| RR | **1:2** |
| 90 min wait | **ON** |
| Sensex / 10:45 | **OFF** |

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
