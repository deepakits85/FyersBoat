# Analysis scripts

## four_candle_ema_multitf.py
First-4-candle + EMA9/15 (volume OFF). c3 O/H/L/C must all clear EMA
(no wick or body touch).

**SL / Target**
- Break candle = c1 (jis ka High/Low break ho)
- BUY: Entry = c1.High, SL = c1.Low | SELL: Entry = c1.Low, SL = c1.High
- Target = 1:1.6

Caches Fyers history under
`FyersLoginWeb/bin/Release/net9.0/datacache/multitf/`.

```bash
python3 BacktestToday/Analysis/four_candle_ema_multitf.py                # RR=1.6 default
python3 BacktestToday/Analysis/four_candle_ema_multitf.py --rr=2
python3 BacktestToday/Analysis/four_candle_ema_multitf.py --rr=3
python3 BacktestToday/Analysis/four_candle_ema_multitf.py --force        # re-fetch
```

TFs: 3, 5, 10, 15, 20, 30 min — Nifty / Bank / Sensex.
