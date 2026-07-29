# Trade mechanics — how live takes trades

## Short answer

| Question | Answer |
|----------|--------|
| Every tick? | **Nahi** — Fyers **History API** se **3-min OHLC** |
| Wick pe turant? | **Nahi** — **candle CLOSE confirm** (`RequireCloseConfirm=true`) |
| Kab scan? | Signal scan **~har 60s**; exits **~har 15s** |
| Kab entry order? | Setup confirm hone ke **baad**, signal age **≤6 min** (fresh only) |

## Flow

1. **Data:** `GetCandlesAsync(..., "3", today)` — completed 3m candles only (`EndTime <= now`; forming candle drop).
2. **ATM strike:** monitoring-start (ref+30m) index open se round.
3. **Strategy** on **option** 3m chart: first breach → 50% retrace → 2nd breach.
4. **2nd breach entry:** High/Low ne level touch kiya **aur Close level hold** kare → entry us closed candle pe.
5. **Filters (strict):** lag≥30, skip Tue, min risk bps; Legs mein Sensex/10:45 pehle se nahi.
6. **Portfolio:** 30m gap, max 2 SL/day, Nifty priority > Bank.
7. **Paper order:** buy limit @ entry → fill ke baad **SL-M + target limit** → 14:45 market square-off.

## Close confirm vs wick

- **Close confirm (live):** jhootha wick ignore — Close bhi level ke paar hona chahiye.
- **Wick mode** (`RequireCloseConfirm=false`): High/Low touch = turant entry — recommended nahi.

## Trailing

- Backtest outcomes mein trail@2R / target 2.5R simulate hota hai.
- Live paper abhi **fixed SL + fixed target** orders place karta hai (trail modify abhi broker path pe nahi).

## Config source

`RecommendedLiveConfig` + `LiveTradingService`.
