# Analysis scripts

## ema_failed_break_multitf.py (current)
EMA failed-break opener. First candle ignore. EMA9+15 clear.
Day-low/high proximity 10% optional (`--no-day-prox` to disable).
SL: prior 1–2 candle extreme, capped at 50% of candle before broken. Target 1:3.

```bash
python3 BacktestToday/Analysis/ema_failed_break_multitf.py \
  --tfs=10,15,20,30 --fetch-from=2026-04-20 --analyze-from=2026-05-01 --to=2026-07-30 --quiet

python3 BacktestToday/Analysis/ema_failed_break_multitf.py --no-day-prox --tfs=10,15,20,30 --quiet
python3 BacktestToday/Analysis/ema_failed_break_multitf.py --sl-lookback=1 --rr=3 --quiet
```

## four_candle_ema_multitf.py (legacy R&D)
Old first-4-candle pattern. See script docstring / `--invert` / `--rr=`.

Caches under `FyersLoginWeb/bin/Release/net9.0/datacache/multitf/`.
