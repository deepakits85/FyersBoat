# Recommended live config (current R&D)

Branch: **`cursor/no-retr-first-90e4`**

User-selected live stack (manual-chart aligned):

| Setting | Value |
|---------|-------|
| Index | **Nifty only** |
| Refs | **11:15 only** |
| Breakout level | **Reference High/Low** (`UsePriorLevelBreak=false`) |
| Sequence | **First BO → 50% → 2nd BO** (`UseRetracementFirst=false`) |
| Filters | **OFF** (no skip) |
| Entry | Close confirm @ EffLevel; same-candle fill if Low ≤ Cap |
| Risk | RR=2, trail +1R, 90m wait, 14:45 square-off |

## Code
- `LiveTradingService.Legs` — Nifty only
- `RecommendedLiveConfig.Refs` — 11:15
- `RecommendedLiveConfig.EntryFilters()` — empty
- `MakeConfig` — `UsePriorLevelBreak=false`, `UseRetracementFirst=false`

## Note on older +63.8R number

That figure was from a **different** stack (Nifty+Bank, multi refs, prior-High breakout, strict filters).  
Current live stack is intentionally simpler / chart-aligned — re-run:

```
dotnet run -c Release --project BacktestToday -- \
  --mode index --from 2025-08-01 --to 2026-06-30 \
  --symbols Nifty --refs 11:15 --filters none --cache-only
```
