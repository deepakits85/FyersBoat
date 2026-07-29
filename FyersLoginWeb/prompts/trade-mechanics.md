# Trade mechanics — how live takes trades

## Short answer

| Question | Answer |
|----------|--------|
| Every tick? | **Nahi** — Fyers **History API** se **3-min OHLC** |
| Wick pe turant? | **Nahi** — **usi candle ke CLOSE** pe confirm |
| Close ke baad +3 min wait? | **Nahi** — close hote hi next poll (~15s) pe entry |
| Kab scan? | **Har ~15s** (signals + exits) |

## Timing example

- Breakout candle **12:15–12:18**
- **12:18** pe candle CLOSE → Close confirm yahi decide hota hai
- **12:18–12:19** (max ~15s poll) pe paper BUY — **agli 12:18–12:21 candle ka wait nahi**

## Flow

1. History 3m **closed** candles (`EndTime <= now`)
2. ATM from monitoring-start spot
3. Breach → 50% retrace → 2nd breach + **Close hold**
4. Strict filters + portfolio (30m gap, max 2 SL/day)
5. Fresh = **candle close ke 6 min** ke andar (StartTime+3m se age)
6. Paper: buy limit → SL-M + target → 14:45 square-off

## Close confirm vs wick

- **Close confirm:** jhootha wick filter — Close bhi level paar
- **Wick mode:** High touch = turant — recommended nahi (zyada false break)

Config: `RecommendedLiveConfig` + `LiveTradingService`.
