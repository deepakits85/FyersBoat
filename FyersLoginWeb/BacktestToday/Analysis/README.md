# Analysis scripts

## ema_failed_break_multitf.py (current)
EMA failed-break (Bank 15m 5-Jun style). First candle fully ignored.
BUY: green High-break → next red | SELL: red Low-break → next green.
EMA clear on break+entry | no entry after 14:45 | skip 2nd consecutive break.
**Day proximity 10% ON by default** (`--no-day-prox` only when asked).
Day H/L measured **through the break candle only** (pre-entry) so entry cannot fake near day-low/high.
SL prior 1–2 + 50% cap | Target 1:3.

```bash
python3 BacktestToday/Analysis/ema_failed_break_multitf.py \
  --tfs=10,15,20,30 --fetch-from=2026-04-20 --analyze-from=2026-05-01 --to=2026-07-30 --quiet

# only when user asks to remove 10%:
python3 BacktestToday/Analysis/ema_failed_break_multitf.py --no-day-prox --tfs=10,15,20,30 --quiet
```

## four_candle_ema_multitf.py (legacy R&D)
Old first-4-candle pattern. See script docstring / `--invert` / `--rr=`.

Caches under `FyersLoginWeb/bin/Release/net9.0/datacache/multitf/`.
