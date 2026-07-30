#!/usr/bin/env python3
"""
First-4-candle + EMA9/15 signal checker (VOLUME OFF).
c3 Open/High/Low/Close must all be clear of EMA9 and EMA15 (no wick/body touch).
SL = break candle (c1) opposite extreme; Target = 1:6.
Caches Fyers history locally; runs 3/5/10/15/20/30 min TFs.
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.parse
import urllib.request
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]  # .../FyersLoginWeb
CACHE = ROOT / "FyersLoginWeb" / "bin" / "Release" / "net9.0" / "datacache" / "multitf"
CACHE.mkdir(parents=True, exist_ok=True)
TOKEN = ROOT / "FyersLoginWeb" / "bin" / "Debug" / "net9.0" / "token.json"
APPSETTINGS = ROOT / "FyersLoginWeb" / "appsettings.json"

IST = timedelta(hours=5, minutes=30)
TFS = ["3", "5", "10", "15", "20", "30"]
SYMS = [
    ("Nifty", "NSE:NIFTY50-INDEX"),
    ("Bank", "NSE:NIFTYBANK-INDEX"),
    ("Sensex", "BSE:SENSEX-INDEX"),
]
RR = 6.0


def load_auth():
    tok = json.loads(TOKEN.read_text())
    cfg = json.loads(APPSETTINGS.read_text())
    return cfg["Fyers"]["ClientId"], tok["AccessToken"]


def cache_path(sym: str, res: str, d0: str, d1: str) -> Path:
    safe = sym.replace(":", "_").replace("/", "_")
    return CACHE / f"{safe}_{res}_{d0}_{d1}.json"


def fetch_range(cid: str, access: str, sym: str, res: str, d0: str, d1: str, force=False):
    path = cache_path(sym, res, d0, d1)
    if path.exists() and not force:
        return json.loads(path.read_text())

    url = (
        "https://api-t1.fyers.in/data/history"
        f"?symbol={urllib.parse.quote(sym)}&resolution={res}&date_format=1"
        f"&range_from={d0}&range_to={d1}&cont_flag=1"
    )
    req = urllib.request.Request(
        url,
        headers={"Authorization": f"{cid}:{access}", "User-Agent": "FyersBoat-MultiTF/1.0"},
    )
    with urllib.request.urlopen(req, timeout=60) as r:
        body = json.loads(r.read())
    if body.get("s") != "ok":
        raise RuntimeError(f"{sym} {res}: {body}")

    by = {}
    for row in body.get("candles") or []:
        ts = datetime.fromtimestamp(row[0], timezone.utc).replace(tzinfo=None) + IST
        vol = float(row[5]) if len(row) > 5 else 0.0
        by[ts.isoformat()] = {
            "t": ts.isoformat(),
            "o": float(row[1]),
            "h": float(row[2]),
            "l": float(row[3]),
            "c": float(row[4]),
            "v": vol,
        }
    rows = [by[k] for k in sorted(by)]
    path.write_text(json.dumps(rows))
    print(f"  cached {path.name} ({len(rows)} bars)", flush=True)
    time.sleep(0.35)
    return rows


def ema(vals, period):
    k = 2 / (period + 1.0)
    out = [float(vals[0])]
    for i in range(1, len(vals)):
        out.append(vals[i] * k + out[-1] * (1 - k))
    return out


def clear_above(c, ema_v):
    return c["o"] > ema_v and c["h"] > ema_v and c["l"] > ema_v and c["c"] > ema_v


def clear_below(c, ema_v):
    return c["o"] < ema_v and c["h"] < ema_v and c["l"] < ema_v and c["c"] < ema_v


def build_trade(side: str, c1, c3):
    """BUY: Entry=c1.High SL=c1.Low | SELL: Entry=c1.Low SL=c1.High | Target 1:6."""
    buy = side == "BUY"
    entry = c1["h"] if buy else c1["l"]
    sl = c1["l"] if buy else c1["h"]
    risk = abs(entry - sl) or 0.01
    target = entry + risk * RR if buy else entry - risk * RR
    return {
        "side": side,
        "entry": entry,
        "sl": sl,
        "target": target,
        "risk": risk,
        "rr": RR,
        "break_h": c1["h"],
        "break_l": c1["l"],
        "signal_t": c3["t"],
    }


def apply_signal(c0, c1, c2, c3):
    """Volume removed. Returns trade dict or None."""
    c2_red = c2["c"] < c2["o"]
    c2_green = c2["c"] > c2["o"]
    c3_red = c3["c"] < c3["o"]
    c3_green = c3["c"] > c3["o"]

    above = clear_above(c3, c3["ema9"]) and clear_above(c3, c3["ema15"])
    below = clear_below(c3, c3["ema9"]) and clear_below(c3, c3["ema15"])

    breakout = c2["h"] > c1["h"]
    if breakout and above and not (c2_green and c3_green):
        if c2_red or (c2_green and c3_red):
            return build_trade("BUY", c1, c3)

    breakdown = c2["l"] < c1["l"]
    if breakdown and below and not (c2_red and c3_red):
        if c2_green or (c2_red and c3_green):
            return build_trade("SELL", c1, c3)
    return None


def first_session_bars(rows, day: datetime.date, need=4):
    bars = []
    for r in rows:
        t = datetime.fromisoformat(r["t"])
        if t.date() != day:
            continue
        if (t.hour, t.minute) < (9, 15):
            continue
        bars.append(r)
        if len(bars) >= need:
            break
    return bars


def session_bars_after(rows, day: datetime.date, after_iso: str):
    """Same-day bars strictly after signal candle start."""
    out = []
    for r in rows:
        t = datetime.fromisoformat(r["t"])
        if t.date() != day:
            continue
        if (t.hour, t.minute) > (15, 30):
            continue
        if r["t"] > after_iso:
            out.append(r)
    return out


def resolve_outcome(trade, bars_after):
    """Walk forward after signal. Same-bar SL+TP -> adverse (SL) first."""
    buy = trade["side"] == "BUY"
    sl, tgt = trade["sl"], trade["target"]
    for b in bars_after:
        if buy:
            hit_sl = b["l"] <= sl
            hit_tg = b["h"] >= tgt
            if hit_sl and hit_tg:
                return "SL"
            if hit_sl:
                return "SL"
            if hit_tg:
                return "TARGET"
        else:
            hit_sl = b["h"] >= sl
            hit_tg = b["l"] <= tgt
            if hit_sl and hit_tg:
                return "SL"
            if hit_sl:
                return "SL"
            if hit_tg:
                return "TARGET"
    return "OPEN"


def main():
    force = "--force" in sys.argv
    d0, d1 = "2026-07-20", "2026-07-30"
    days = [datetime(2026, 7, d).date() for d in (27, 28, 29, 30)]
    cid, access = load_auth()

    print(f"Cache dir: {CACHE}")
    print("Rule: first-4 (ignore c0) + EMA9/15 | VOL OFF | c3 OHLC clear EMA")
    print("SL = break candle (c1) opposite extreme | Target 1:6")
    print(f"TFs: {', '.join(TFS)} min | Range lookback {d0}→{d1}\n")

    summary = []  # (name, tf, day, side, outcome)
    trades_log = []

    for name, sym in SYMS:
        print(f"===== {name} =====")
        for res in TFS:
            rows = fetch_range(cid, access, sym, res, d0, d1, force=force)
            if len(rows) < 20:
                print(f"  {res}m: insufficient data")
                continue
            closes = [r["c"] for r in rows]
            e9 = ema(closes, 9)
            e15 = ema(closes, 15)
            for i, r in enumerate(rows):
                r["ema9"] = e9[i]
                r["ema15"] = e15[i]

            print(f"  --- {res}m ---")
            for day in days:
                bars = first_session_bars(rows, day, 4)
                if len(bars) < 4:
                    print(f"    {day} NO DATA (bars={len(bars)})")
                    summary.append((name, res, str(day), "NODATA", "-"))
                    continue
                c0, c1, c2, c3 = bars[0], bars[1], bars[2], bars[3]
                trade = apply_signal(c0, c1, c2, c3)
                if not trade:
                    summary.append((name, res, str(day), "NONE", "-"))
                    t3 = datetime.fromisoformat(c3["t"]).strftime("%H:%M")
                    print(f"    {day} → NONE @c3={t3}")
                    continue

                after = session_bars_after(rows, day, c3["t"])
                outcome = resolve_outcome(trade, after)
                summary.append((name, res, str(day), trade["side"], outcome))
                trades_log.append((name, res, day, trade, outcome))
                t3 = datetime.fromisoformat(c3["t"]).strftime("%H:%M")
                print(
                    f"    {day} → {trade['side']:4} @c3={t3} "
                    f"Entry={trade['entry']:.2f} SL={trade['sl']:.2f} "
                    f"T={trade['target']:.2f} (1:{int(RR)}) risk={trade['risk']:.2f} "
                    f"→ {outcome}"
                )

    print("\n========== SIGNAL MATRIX (week) ==========")
    print(f"{'Index':8} {'TF':5} {'27':10} {'28':10} {'29':10} {'30':10}")
    grid = defaultdict(dict)
    for name, res, day, side, outcome in summary:
        cell = side if side in ("NONE", "NODATA") else f"{side[0]}:{outcome[:3]}"
        grid[(name, res)][day[-2:]] = cell
    for name, _ in SYMS:
        for res in TFS:
            row = grid.get((name, res), {})
            print(
                f"{name:8} {res+'m':5} "
                f"{row.get('27','-'):10} {row.get('28','-'):10} "
                f"{row.get('29','-'):10} {row.get('30','-'):10}"
            )

    buys = sum(1 for *_, s, _o in summary if s == "BUY")
    sells = sum(1 for *_, s, _o in summary if s == "SELL")
    tgt = sum(1 for *_, o in summary if o == "TARGET")
    sl = sum(1 for *_, o in summary if o == "SL")
    opn = sum(1 for *_, o in summary if o == "OPEN")
    # R: target = +6, SL = -1, OPEN = 0 (mark-to-close ignored)
    net_r = tgt * RR + sl * (-1.0)
    print(f"\nTotals: BUY={buys}  SELL={sells}")
    print(f"Outcomes (same-day after signal): TARGET={tgt}  SL={sl}  OPEN={opn}")
    print(f"Net R (OPEN=0): {net_r:+.1f}R  |  RR=1:{int(RR)}")


if __name__ == "__main__":
    main()
