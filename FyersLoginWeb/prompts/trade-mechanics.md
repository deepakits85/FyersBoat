# Trade mechanics — how live takes trades

## Short answer

| Question | Answer |
|----------|--------|
| Every tick? | **Nahi** — Fyers **History API** se **3-min OHLC** (tick sirf WaitingForEntry fill timing / compare) |
| Wick pe turant? | **Nahi** — **usi candle ke CLOSE > reference high** |
| Confirm candle pe entry? | **Nahi** — confirm close ke baad hi pata chalta hai |
| Entry kab? | **Agli** candle(s)/ticks pe jab price **ref high** touch kare → fill @ ref high |
| Kab scan? | **Har ~15s** (signals + exits) |

## Timing example (29 Jul Nifty CE — corrected)

- Ref 11:15 High = **125.45**
- Confirm bar **12:09–12:12**: Close **126.8 > 125.45** → CLOSE-OK (entry nahi)
- Entry bar **12:12–12:15**: Low ≤ 125.45 → BUY @ **125.45**

## Flow

1. History 3m **closed** candles (`EndTime <= now`)
2. ATM from monitoring-start spot
3. Breach → 50% retrace → 2nd breach + **Close > refHigh** (not prior-high)
4. State `WaitingForEntry` → fill jab Low/High **ref high/low** touch
5. Strict filters + portfolio (30m gap, max 2 SL/day)
6. Fresh = **candle close ke 6 min** ke andar
7. Paper: buy limit → SL-M + target → 14:45 square-off

## Confirm level (important)

- BUY/SELL confirm + entry cap = **30m reference High/Low only**
- Prior-high `BreakoutLevel` sirf **pehla** breakout detect me (setup), confirm/entry me nahi
- Bug (fixed 29 Jul night): retracement-first pe EffLevel=prior-high use ho raha tha → late/wrong confirms

## Close confirm vs wick

- **Close confirm:** Close bhi **refHigh** paar — jhootha wick filter
- **Wick mode:** High touch = turant — recommended nahi

Config: `RecommendedLiveConfig` + `LiveTradingService`.
