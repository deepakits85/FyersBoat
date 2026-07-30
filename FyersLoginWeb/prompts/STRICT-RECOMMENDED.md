# DATA-BACKED recommended config (R&D)

Branch: **`cursor/strict-recommended-90e4`**

## Principle
Jo **11m index data** pe jeete, wahi use. Theory / “live feel” rules se data winner mat todna.

## Winner (verified restore)
Index Aug 2025–Jul 2026 | strict filters | close-confirm @ EffLevel | **same-candle fill** if Low≤cap:

| | |
|--|--|
| TAKEN | ~150 trades |
| NetR | **~+63.8R** |
| Avg / month | **~+5.3R** |
| Pos months | 10/12 |

## Rules (DATA)

| Setting | Value |
|---------|--------|
| Indices | Nifty + BankNifty (Sensex filter-off) |
| Refs | 11:15, 12:45 (10:45 skipped by strict) |
| Filters | **strict** |
| RR | 1:2 + trail |
| 90m wait | ON |
| Breakout | Close hold @ **EffLevel** (prior-high on retrace-first OK) |
| Entry | Confirm candle pe fill agar Low≤cap; gap pe pullback wait |

## Haar gaya (mat use karo abhi)
“Confirm candle pe entry nahi + next bar @ refHigh only” → same 11m pe ~**+1.4R** (almost flat).

## Code
- `BreakoutRetracementStrategy` — data-era entry (83cdcab path)
- `RecommendedLiveConfig` — strict + this entry model
- `EntryFilterConfig.ReduceStopLossStrictPreset()`
