#!/usr/bin/env python3
"""
First-4-candle + EMA9/15 signal checker (VOLUME OFF).
c3 Open/High/Low/Close must all be clear of EMA9 and EMA15 (no wick/body touch).
SL = break candle (c1) opposite extreme; Target via --rr= (default 1.6).

Examples:
  python3 four_candle_ema_multitf.py --rr=2 --tfs=10,15,20,30 --from=2026-05-01 --to=2026-07-30
  python3 four_candle_ema_multitf.py --force
"""
from __future__ import annotations

import json
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
DEFAULT_TFS = ["3", "5", "10", "15", "20", "30"]
SYMS = [
    ("Nifty", "NSE:NIFTY50-INDEX"),
    ("Bank", "NSE:NIFTYBANK-INDEX"),
    ("Sensex", "BSE:SENSEX-INDEX"),
]
DEFAULT_RR = 1.6


def arg_val(argv, key, default=None):
    prefix = f"--{key}="
    for a in argv:
        if a.startswith(prefix):
            return a.split("=", 1)[1]
    return default


def load_auth():
    tok = json.loads(TOKEN.read_text())
    cfg = json.loads(APPSETTINGS.read_text())
    return cfg["Fyers"]["ClientId"], tok["AccessToken"]


def cache_path(sym: str, res: str, d0: str, d1: str) -> Path:
    safe = sym.replace(":", "_").replace("/", "_")
    return CACHE / f"{safe}_{res}_{d0}_{d1}.json"


def _fetch_once(cid: str, access: str, sym: str, res: str, d0: str, d1: str):
    url = (
        "https://api-t1.fyers.in/data/history"
        f"?symbol={urllib.parse.quote(sym)}&resolution={res}&date_format=1"
        f"&range_from={d0}&range_to={d1}&cont_flag=1"
    )
    req = urllib.request.Request(
        url,
        headers={"Authorization": f"{cid}:{access}", "User-Agent": "FyersBoat-MultiTF/1.0"},
    )
    with urllib.request.urlopen(req, timeout=90) as r:
        body = json.loads(r.read())
    if body.get("s") != "ok":
        raise RuntimeError(f"{sym} {res} {d0}->{d1}: {body}")
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
    return by


def month_chunks(d0: str, d1: str):
    """Split inclusive date range into ~1-month chunks (Fyers history limits)."""
    start = datetime.strptime(d0, "%Y-%m-%d").date()
    end = datetime.strptime(d1, "%Y-%m-%d").date()
    cur = start
    while cur <= end:
        # next month start - 1 day, or end
        if cur.month == 12:
            nxt = cur.replace(year=cur.year + 1, month=1, day=1)
        else:
            nxt = cur.replace(month=cur.month + 1, day=1)
        chunk_end = min(end, nxt - timedelta(days=1))
        yield cur.isoformat(), chunk_end.isoformat()
        cur = chunk_end + timedelta(days=1)


def fetch_range(cid: str, access: str, sym: str, res: str, d0: str, d1: str, force=False):
    path = cache_path(sym, res, d0, d1)
    if path.exists() and not force:
        return json.loads(path.read_text())

    by = {}
    for c0, c1 in month_chunks(d0, d1):
        part = _fetch_once(cid, access, sym, res, c0, c1)
        by.update(part)
        print(f"  fetched {sym} {res}m {c0}→{c1} (+{len(part)})", flush=True)
        time.sleep(0.4)
    rows = [by[k] for k in sorted(by)]
    path.write_text(json.dumps(rows))
    print(f"  cached {path.name} ({len(rows)} bars)", flush=True)
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


def build_trade(side: str, c1, c3, rr: float):
    buy = side == "BUY"
    entry = c1["h"] if buy else c1["l"]
    sl = c1["l"] if buy else c1["h"]
    risk = abs(entry - sl) or 0.01
    target = entry + risk * rr if buy else entry - risk * rr
    return {
        "side": side,
        "entry": entry,
        "sl": sl,
        "target": target,
        "risk": risk,
        "rr": rr,
        "break_h": c1["h"],
        "break_l": c1["l"],
        "c1_t": c1["t"],
        "c2_t": None,
        "c3_t": c3["t"],
        "c1_o": c1["o"],
        "c1_h": c1["h"],
        "c1_l": c1["l"],
        "c1_c": c1["c"],
        "signal_t": c3["t"],
    }


def apply_signal(c0, c1, c2, c3, rr: float, invert: bool = False):
    c2_red = c2["c"] < c2["o"]
    c2_green = c2["c"] > c2["o"]
    c3_red = c3["c"] < c3["o"]
    c3_green = c3["c"] > c3["o"]

    above = clear_above(c3, c3["ema9"]) and clear_above(c3, c3["ema15"])
    below = clear_below(c3, c3["ema9"]) and clear_below(c3, c3["ema15"])

    pattern = None
    breakout = c2["h"] > c1["h"]
    if breakout and above and not (c2_green and c3_green):
        if c2_red or (c2_green and c3_red):
            pattern = "BUY"

    if pattern is None:
        breakdown = c2["l"] < c1["l"]
        if breakdown and below and not (c2_red and c3_red):
            if c2_green or (c2_red and c3_green):
                pattern = "SELL"

    if pattern is None:
        return None

    side = ("SELL" if pattern == "BUY" else "BUY") if invert else pattern
    t = build_trade(side, c1, c3, rr)
    t["c2_t"] = c2["t"]
    t["pattern"] = pattern
    t["inverted"] = invert
    return t


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
    buy = trade["side"] == "BUY"
    sl, tgt = trade["sl"], trade["target"]
    for b in bars_after:
        if buy:
            hit_sl = b["l"] <= sl
            hit_tg = b["h"] >= tgt
            if hit_sl and hit_tg:
                return "SL", b["t"], sl
            if hit_sl:
                return "SL", b["t"], sl
            if hit_tg:
                return "TARGET", b["t"], tgt
        else:
            hit_sl = b["h"] >= sl
            hit_tg = b["l"] <= tgt
            if hit_sl and hit_tg:
                return "SL", b["t"], sl
            if hit_sl:
                return "SL", b["t"], sl
            if hit_tg:
                return "TARGET", b["t"], tgt
    last = bars_after[-1] if bars_after else None
    return "OPEN", (last["t"] if last else None), (last["c"] if last else None)


def hhmm(iso):
    if not iso:
        return "-"
    return datetime.fromisoformat(iso).strftime("%H:%M")


def trading_days(rows, analyze_from: datetime.date, analyze_to: datetime.date):
    days = sorted({
        datetime.fromisoformat(r["t"]).date()
        for r in rows
        if analyze_from <= datetime.fromisoformat(r["t"]).date() <= analyze_to
        and (datetime.fromisoformat(r["t"]).hour, datetime.fromisoformat(r["t"]).minute) >= (9, 15)
    })
    return days


def main():
    force = "--force" in sys.argv
    quiet = "--quiet" in sys.argv  # skip NONE day spam
    invert = "--invert" in sys.argv
    rr = float(arg_val(sys.argv, "rr", DEFAULT_RR))
    tfs = [x.strip() for x in arg_val(sys.argv, "tfs", ",".join(DEFAULT_TFS)).split(",") if x.strip()]
    # fetch window (EMA warmup): default 20 Apr → 30 Jul; analyze: 1 May → 30 Jul
    fetch_from = arg_val(sys.argv, "fetch-from", arg_val(sys.argv, "from", "2026-04-20"))
    analyze_from_s = arg_val(sys.argv, "analyze-from", arg_val(sys.argv, "from", "2026-05-01"))
    analyze_to_s = arg_val(sys.argv, "to", "2026-07-30")
    # if user only passed --from/--to, fetch from --from too
    if arg_val(sys.argv, "from") and not arg_val(sys.argv, "fetch-from"):
        fetch_from = analyze_from_s
    analyze_from = datetime.strptime(analyze_from_s, "%Y-%m-%d").date()
    analyze_to = datetime.strptime(analyze_to_s, "%Y-%m-%d").date()

    cid, access = load_auth()

    print(f"Cache dir: {CACHE}")
    print("Rule: first-4 (ignore c0) + EMA9/15 | VOL OFF | c3 OHLC clear EMA")
    print(f"SL = break candle (c1) opposite extreme | Target 1:{rr}")
    if invert:
        print("INVERT ON: pattern BUY → trade SELL, pattern SELL → trade BUY")
    print(f"TFs: {', '.join(tfs)} min")
    print(f"Fetch {fetch_from}→{analyze_to_s} | Analyze {analyze_from_s}→{analyze_to_s}\n")

    summary = []  # (name, tf, day, side, outcome)
    trades_log = []
    by_tf = defaultdict(lambda: {"BUY": 0, "SELL": 0, "TARGET": 0, "SL": 0, "OPEN": 0, "NONE": 0, "days": 0})

    for name, sym in SYMS:
        print(f"===== {name} =====")
        for res in tfs:
            rows = fetch_range(cid, access, sym, res, fetch_from, analyze_to_s, force=force)
            if len(rows) < 20:
                print(f"  {res}m: insufficient data ({len(rows)})")
                continue
            closes = [r["c"] for r in rows]
            e9 = ema(closes, 9)
            e15 = ema(closes, 15)
            for i, r in enumerate(rows):
                r["ema9"] = e9[i]
                r["ema15"] = e15[i]

            days = trading_days(rows, analyze_from, analyze_to)
            print(f"  --- {res}m --- days={len(days)} bars={len(rows)}")
            for day in days:
                by_tf[res]["days"] += 1
                bars = first_session_bars(rows, day, 4)
                if len(bars) < 4:
                    summary.append((name, res, str(day), "NODATA", "-"))
                    if not quiet:
                        print(f"    {day} NO DATA (bars={len(bars)})")
                    continue
                c0, c1, c2, c3 = bars[0], bars[1], bars[2], bars[3]
                trade = apply_signal(c0, c1, c2, c3, rr, invert=invert)
                if not trade:
                    summary.append((name, res, str(day), "NONE", "-"))
                    by_tf[res]["NONE"] += 1
                    if not quiet:
                        print(f"    {day} → NONE @c3={hhmm(c3['t'])}")
                    continue

                after = session_bars_after(rows, day, c3["t"])
                outcome, exit_t, exit_px = resolve_outcome(trade, after)
                trade["outcome"] = outcome
                trade["exit_t"] = exit_t
                trade["exit_px"] = exit_px
                trade["c2_o"], trade["c2_h"], trade["c2_l"], trade["c2_c"] = c2["o"], c2["h"], c2["l"], c2["c"]
                trade["c3_o"], trade["c3_h"], trade["c3_l"], trade["c3_c"] = c3["o"], c3["h"], c3["l"], c3["c"]
                trade["c3_ema9"], trade["c3_ema15"] = c3["ema9"], c3["ema15"]
                summary.append((name, res, str(day), trade["side"], outcome))
                trades_log.append((name, res, str(day), trade))
                by_tf[res][trade["side"]] += 1
                by_tf[res][outcome] += 1
                pat = trade.get("pattern", "")
                inv_tag = f" (pat={pat})" if invert else ""
                print(
                    f"    {day} → {trade['side']:4}{inv_tag} @c3={hhmm(c3['t'])} "
                    f"Entry={trade['entry']:.2f} SL={trade['sl']:.2f} "
                    f"T={trade['target']:.2f} (1:{rr}) risk={trade['risk']:.2f} "
                    f"→ {outcome}" + (f" @{hhmm(exit_t)}" if exit_t and outcome != "OPEN" else "")
                )

    buys = sum(1 for *_, s, _o in summary if s == "BUY")
    sells = sum(1 for *_, s, _o in summary if s == "SELL")
    tgt = sum(1 for *_, o in summary if o == "TARGET")
    sl = sum(1 for *_, o in summary if o == "SL")
    opn = sum(1 for *_, o in summary if o == "OPEN")
    net_r = tgt * rr + sl * (-1.0)
    n_sig = buys + sells
    win = (100.0 * tgt / n_sig) if n_sig else 0.0

    print("\n========== BY TIMEFRAME ==========")
    print(f"{'TF':5} {'Sig':5} {'BUY':5} {'SELL':5} {'TGT':5} {'SL':5} {'OPEN':5} {'NetR':8} {'Win%':6}")
    for res in tfs:
        b = by_tf[res]
        sig = b["BUY"] + b["SELL"]
        nr = b["TARGET"] * rr + b["SL"] * (-1.0)
        wr = (100.0 * b["TARGET"] / sig) if sig else 0.0
        print(
            f"{res+'m':5} {sig:5} {b['BUY']:5} {b['SELL']:5} {b['TARGET']:5} {b['SL']:5} {b['OPEN']:5} "
            f"{nr:+8.1f} {wr:5.1f}%"
        )

    print("\n========== BY INDEX ==========")
    print(f"{'Index':8} {'Sig':5} {'BUY':5} {'SELL':5} {'TGT':5} {'SL':5} {'OPEN':5} {'NetR':8}")
    for name, _ in SYMS:
        rows_s = [x for x in summary if x[0] == name]
        b = sum(1 for *_, s, _o in rows_s if s == "BUY")
        se = sum(1 for *_, s, _o in rows_s if s == "SELL")
        tg = sum(1 for *_, o in rows_s if o == "TARGET")
        sln = sum(1 for *_, o in rows_s if o == "SL")
        op = sum(1 for *_, o in rows_s if o == "OPEN")
        print(f"{name:8} {b+se:5} {b:5} {se:5} {tg:5} {sln:5} {op:5} {tg*rr+sln*(-1):+8.1f}")

    print(f"\n========== TOTALS RR=1:{rr} | {analyze_from_s}→{analyze_to_s}"
          f"{' | INVERT' if invert else ''} ==========")
    print(f"Signals: BUY={buys}  SELL={sells}  total={n_sig}")
    print(f"Outcomes: TARGET={tgt}  SL={sl}  OPEN={opn}  Win%={win:.1f}%")
    print(f"Net R (OPEN=0): {net_r:+.1f}R")

    print("\n========== DETAILED TRADES (verify) ==========")
    print(
        f"{'#':3} {'Index':7} {'TF':4} {'Day':10} {'Pat':4} {'Side':4} "
        f"{'c1':5} {'c2':5} {'c3':5} "
        f"{'c1.H':>10} {'c1.L':>10} {'Entry':>10} {'SL':>10} {'Target':>10} {'Risk':>8} "
        f"{'Out':6} {'Exit':5} {'R':>5}"
    )
    for i, (name, res, day, trade) in enumerate(trades_log, 1):
        r_mult = trade["rr"] if trade["outcome"] == "TARGET" else (-1.0 if trade["outcome"] == "SL" else 0.0)
        print(
            f"{i:<3} {name:7} {res+'m':4} {day:10} {trade.get('pattern','-'):4} {trade['side']:4} "
            f"{hhmm(trade['c1_t']):5} {hhmm(trade['c2_t']):5} {hhmm(trade['c3_t']):5} "
            f"{trade['c1_h']:10.2f} {trade['c1_l']:10.2f} "
            f"{trade['entry']:10.2f} {trade['sl']:10.2f} {trade['target']:10.2f} {trade['risk']:8.2f} "
            f"{trade['outcome']:6} {hhmm(trade['exit_t']):5} {r_mult:5.1f}"
        )

    print("\n========== CANDLE OHLC (c1 break / c2 / c3 signal) ==========")
    for i, (name, res, day, trade) in enumerate(trades_log, 1):
        print(
            f"#{i} {name} {res}m {day} {trade['side']} | "
            f"c1@{hhmm(trade['c1_t'])} O={trade['c1_o']:.2f} H={trade['c1_h']:.2f} L={trade['c1_l']:.2f} C={trade['c1_c']:.2f} | "
            f"c2@{hhmm(trade['c2_t'])} O={trade['c2_o']:.2f} H={trade['c2_h']:.2f} L={trade['c2_l']:.2f} C={trade['c2_c']:.2f} | "
            f"c3@{hhmm(trade['c3_t'])} O={trade['c3_o']:.2f} H={trade['c3_h']:.2f} L={trade['c3_l']:.2f} C={trade['c3_c']:.2f} "
            f"EMA9={trade['c3_ema9']:.2f} EMA15={trade['c3_ema15']:.2f}"
        )


if __name__ == "__main__":
    main()
