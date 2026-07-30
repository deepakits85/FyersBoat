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
# 3 months, TFs 10/15/20/30, RR 1:2
python3 BacktestToday/Analysis/four_candle_ema_multitf.py \
  --rr=2 --tfs=10,15,20,30 --from=2026-05-01 --to=2026-07-30 --quiet

# EMA warmup from Apr 20 (default when using --analyze-from / fetch-from)
python3 BacktestToday/Analysis/four_candle_ema_multitf.py \
  --rr=3 --tfs=10,15,20,30 --fetch-from=2026-04-20 --analyze-from=2026-05-01 --to=2026-07-30 --quiet

# Invert (pattern BUY → trade SELL, SELL → BUY)
python3 BacktestToday/Analysis/four_candle_ema_multitf.py \
  --invert --rr=2 --tfs=10,15,20,30 --fetch-from=2026-04-20 --analyze-from=2026-05-01 --to=2026-07-30 --quiet
```

TFs: 3, 5, 10, 15, 20, 30 min — Nifty / Bank / Sensex.
