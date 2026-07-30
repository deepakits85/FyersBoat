# Analysis scripts

## ema_failed_break_multitf.py (current)
EMA failed-break (Bank 15m 5-Jun style). First candle ignore. EMA9+15 clear.
BUY: High break by **green** → **next red** → entry close.
SELL: Low break by **red** → **next green** → entry close.
Day proximity 10% optional (`--no-day-prox`). SL prior 1–2 + 50% cap. Target 1:3.

```bash
python3 BacktestToday/Analysis/ema_failed_break_multitf.py \
  --tfs=10,15,20,30 --fetch-from=2026-04-20 --analyze-from=2026-05-01 --to=2026-07-30 --quiet

python3 BacktestToday/Analysis/ema_failed_break_multitf.py --no-day-prox --tfs=10,15,20,30 --quiet
python3 BacktestToday/Analysis/ema_failed_break_multitf.py --sl-lookback=1 --rr=3 --quiet
```

## four_candle_ema_multitf.py (legacy R&D)
Old first-4-candle pattern. See script docstring / `--invert` / `--rr=`.

Caches under `FyersLoginWeb/bin/Release/net9.0/datacache/multitf/`.
