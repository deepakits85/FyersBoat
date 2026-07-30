#!/usr/bin/env python3
"""
First-4-candle + EMA9/15 signal checker (VOLUME OFF).
Caches Fyers history locally; runs 3/5/10/15/20/30 min TFs.
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.parse
import urllib.request
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

    rows = []
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
    time.sleep(0.35)  # be nice to API
    return rows


def ema(vals, period):
    k = 2 / (period + 1.0)
    out = [float(vals[0])]
    for i in range(1, len(vals)):
        out.append(vals[i] * k + out[-1] * (1 - k))
    return out


def apply_signal(c0, c1, c2, c3):
    """Volume removed. Same color/EMA/breakout logic as user code."""
    c2_red = c2["c"] < c2["o"]
    c2_green = c2["c"] > c2["o"]
    c3_red = c3["c"] < c3["o"]
    c3_green = c3["c"] > c3["o"]

    # Poora c3 candle EMA se clear (wick/body touch nahi)
    above = c3["l"] > c3["ema9"] and c3["l"] > c3["ema15"]
    below = c3["h"] < c3["ema9"] and c3["h"] < c3["ema15"]

    breakout = c2["h"] > c1["h"]
    if breakout and above:
        if not (c2_green and c3_green):
            if c2_red:
                return "BUY"
            if c2_green and c3_red:
                return "BUY"

    breakdown = c2["l"] < c1["l"]
    if breakdown and below:
        if not (c2_red and c3_red):
            if c2_green:
                return "SELL"
            if c2_red and c3_green:
                return "SELL"
    return ""


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


def main():
    force = "--force" in sys.argv
    d0, d1 = "2026-07-20", "2026-07-30"
    days = [datetime(2026, 7, d).date() for d in (27, 28, 29, 30)]
    cid, access = load_auth()

    print(f"Cache dir: {CACHE}")
    print("Rule: first-4 candle (ignore c0) + EMA9/15 | VOLUME OFF")
    print(f"TFs: {', '.join(TFS)} min | Range lookback {d0}→{d1}\n")

    summary = []  # (name, tf, day, sig)

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
                    summary.append((name, res, str(day), "NODATA"))
                    continue
                c0, c1, c2, c3 = bars[0], bars[1], bars[2], bars[3]
                sig = apply_signal(c0, c1, c2, c3)
                summary.append((name, res, str(day), sig or "NONE"))
                t3 = datetime.fromisoformat(c3["t"]).strftime("%H:%M")
                print(
                    f"    {day} → {(sig or 'NONE'):4} @c3={t3} "
                    f"c2={'G' if c2['c']>c2['o'] else 'R'} c3={'G' if c3['c']>c3['o'] else 'R'} "
                    f"BO={c2['h']>c1['h']} BD={c2['l']<c1['l']} "
                    f"above={c3['l']>c3['ema9'] and c3['l']>c3['ema15']} "
                    f"below={c3['h']<c3['ema9'] and c3['h']<c3['ema15']}"
                )

    print("\n========== SIGNAL MATRIX (week) ==========")
    print(f"{'Index':8} {'TF':5} {'27':6} {'28':6} {'29':6} {'30':6}")
    from collections import defaultdict

    grid = defaultdict(dict)
    for name, res, day, sig in summary:
        grid[(name, res)][day[-2:]] = sig
    for name, _ in SYMS:
        for res in TFS:
            row = grid.get((name, res), {})
            print(
                f"{name:8} {res+'m':5} "
                f"{row.get('27','-'):6} {row.get('28','-'):6} "
                f"{row.get('29','-'):6} {row.get('30','-'):6}"
            )

    buys = sum(1 for *_, s in summary if s == "BUY")
    sells = sum(1 for *_, s in summary if s == "SELL")
    print(f"\nTotals: BUY={buys}  SELL={sells}  (across 3 indices × 6 TFs × 4 days)")


if __name__ == "__main__":
    main()
