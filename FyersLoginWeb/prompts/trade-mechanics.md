# Trade mechanics — DATA-BACKED (R&D)

## Short answer

| Question | Answer |
|----------|--------|
| Entry kab? | Close confirm ke **usi 3m candle** pe, agar Low ≤ cap (EffLevel) |
| Gap up? | Tabhi pullback wait (`WaitingForEntry`) |
| Level? | **EffLevel** (retrace-first pe prior-high included; warna ref High) |
| Filters? | **strict** (lag≥30, skip Tue/Sensex/10:45, min risk bps) |

## Kyu yeh

11m index pe ye model **~+63.8R / ~5.3R-mo**.  
“Next-bar @ refHigh only” R&D pe **~+1.4R** — data ne reject kiya.

## Flow

1. 30m ref → 50% retrace → 2nd breach  
2. Close EffLevel hold  
3. Same bar Low≤cap → BUY @ cap  
4. Strict + portfolio (30m gap, max 2 SL)  
5. Trail / 14:45 square-off  

Config: `RecommendedLiveConfig` + `LiveTradingService` (PaperMode).
