# STRICT recommended live config (29 Jul 2026 findings)

Branch: **`cursor/strict-recommended-90e4`**

## RAKHO

| Setting | Value |
|---------|--------|
| Indices | Nifty + BankNifty |
| Refs | **11:15, 12:45** |
| Filters | **strict** (lag≥30, skip Tue, min risk bps Nifty≥6 Bank≥7.5) |
| RR | **1:2** |
| 90 min ref max-wait | **ON** |
| Entry | **3m candle CLOSE confirm** |
| Trailing | ON (2R → SL entry+1, target 2.5R) |
| Gap / max SL | 30 min / **2 SL/day** |
| Sensex | **OFF** |
| 10:45 | **OFF** |
| Tick live entry (`UseLiveEntry`) | **OFF** (wick/tick pe −21R tha) |

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
