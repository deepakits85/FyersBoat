#!/usr/bin/env python3
"""
EMA failed-break signal — multi-TF backtest.

Rules (Bank 15m 5-Jun BUY style only):
  - First session candle ignored for signals
  - BUY: clear below EMA9+EMA15 (break + entry both), optional near day-low (10%);
         prior High broken by GREEN candle → next candle RED → entry on that close
  - SELL: clear above both EMAs (break + entry), optional near day-high;
         prior Low broken by RED candle → next candle GREEN → entry on that close
  - No entry after 14:45 IST
  - Ignore 2nd consecutive high/low break (broken candle itself already broke prior)
  - SL: min/max of prior 1–2 candles; if risk > 50% of candle just before broken → cap to 50%
  - Target 1:3
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

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / "FyersLoginWeb" / "bin" / "Release" / "net9.0" / "datacache" / "multitf"
CACHE.mkdir(parents=True, exist_ok=True)
TOKEN = ROOT / "FyersLoginWeb" / "bin" / "Debug" / "net9.0" / "token.json"
APPSETTINGS = ROOT / "FyersLoginWeb" / "appsettings.json"

IST = timedelta(hours=5, minutes=30)
DEFAULT_TFS = ["10", "15", "20", "30"]
SYMS = [
    ("Nifty", "NSE:NIFTY50-INDEX"),
    ("Bank", "NSE:NIFTYBANK-INDEX"),
    ("Sensex", "BSE:SENSEX-INDEX"),
]


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


def cache_path(sym, res, d0, d1):
    safe = sym.replace(":", "_").replace("/", "_")
    return CACHE / f"{safe}_{res}_{d0}_{d1}.json"


def month_chunks(d0, d1):
    start = datetime.strptime(d0, "%Y-%m-%d").date()
    end = datetime.strptime(d1, "%Y-%m-%d").date()
    cur = start
    while cur <= end:
        if cur.month == 12:
            nxt = cur.replace(year=cur.year + 1, month=1, day=1)
        else:
            nxt = cur.replace(month=cur.month + 1, day=1)
        chunk_end = min(end, nxt - timedelta(days=1))
        yield cur.isoformat(), chunk_end.isoformat()
        cur = chunk_end + timedelta(days=1)


def _fetch_once(cid, access, sym, res, d0, d1):
    url = (
        "https://api-t1.fyers.in/data/history"
        f"?symbol={urllib.parse.quote(sym)}&resolution={res}&date_format=1"
        f"&range_from={d0}&range_to={d1}&cont_flag=1"
    )
    req = urllib.request.Request(
        url,
        headers={"Authorization": f"{cid}:{access}", "User-Agent": "FyersBoat-EmaFB/1.0"},
    )
    with urllib.request.urlopen(req, timeout=90) as r:
        body = json.loads(r.read())
    if body.get("s") != "ok":
        raise RuntimeError(f"{sym} {res} {d0}->{d1}: {body}")
    by = {}
    for row in body.get("candles") or []:
        ts = datetime.fromtimestamp(row[0], timezone.utc).replace(tzinfo=None) + IST
        by[ts.isoformat()] = {
            "t": ts.isoformat(),
            "o": float(row[1]),
            "h": float(row[2]),
            "l": float(row[3]),
            "c": float(row[4]),
            "v": float(row[5]) if len(row) > 5 else 0.0,
        }
    return by


def fetch_range(cid, access, sym, res, d0, d1, force=False):
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


def clear_above(c, e):
    return c["o"] > e and c["h"] > e and c["l"] > e and c["c"] > e


def clear_below(c, e):
    return c["o"] < e and c["h"] < e and c["l"] < e and c["c"] < e


def is_red(c):
    return c["c"] < c["o"]


def is_green(c):
    return c["c"] > c["o"]


def day_range(bars, through_idx):
    hi = max(b["h"] for b in bars[: through_idx + 1])
    lo = min(b["l"] for b in bars[: through_idx + 1])
    return hi, lo


def compute_sl(bars, entry_idx, broken_idx, entry, is_buy, sl_lookback):
    lookback = max(1, min(2, sl_lookback))
    extremes = []
    for k in range(1, lookback + 1):
        idx = entry_idx - k
        if idx < 0:
            break
        extremes.append(bars[idx]["l"] if is_buy else bars[idx]["h"])
    if not extremes:
        return entry - 0.01 if is_buy else entry + 0.01
    primary = min(extremes) if is_buy else max(extremes)
    primary_risk = abs(entry - primary)

    cap_risk = None
    cap_idx = broken_idx - 1
    if cap_idx >= 0:
        rng = bars[cap_idx]["h"] - bars[cap_idx]["l"]
        if rng > 0:
            cap_risk = rng * 0.5

    if cap_risk is not None and primary_risk > cap_risk:
        return entry - cap_risk if is_buy else entry + cap_risk
    return primary


def try_signal(bars, break_idx, is_buy, use_day_prox, prox_pct, sl_lookback, rr):
    """Bank 15m 5-Jun style only:
    BUY: High break + green break + next red → entry next close
    SELL: Low break + red break + next green → entry next close
    First candle never used as broken reference (break_idx >= 2).
    """
    if break_idx < 2 or break_idx + 1 >= len(bars):
        return None
    brk = bars[break_idx]
    broken = bars[break_idx - 1]
    nxt = bars[break_idx + 1]

    if is_buy:
        if not (brk["h"] > broken["h"]):
            return None
        if not is_green(brk):
            return None
        if not is_red(nxt):
            return None
        # Continuous high-break ignore: broken already broke prior high → 2nd break
        if break_idx >= 2 and broken["h"] > bars[break_idx - 2]["h"]:
            return None
    else:
        if not (brk["l"] < broken["l"]):
            return None
        if not is_red(brk):
            return None
        if not is_green(nxt):
            return None
        if break_idx >= 2 and broken["l"] < bars[break_idx - 2]["l"]:
            return None

    entry_idx = break_idx + 1
    entry_bar = bars[entry_idx]

    # No entry after 14:45
    et = datetime.fromisoformat(entry_bar["t"])
    if (et.hour, et.minute) > (14, 45):
        return None

    # Break + entry both clear of EMA (no wick/body touch)
    if is_buy:
        if not (clear_below(brk, brk["ema9"]) and clear_below(brk, brk["ema15"])):
            return None
        if not (clear_below(entry_bar, entry_bar["ema9"]) and clear_below(entry_bar, entry_bar["ema15"])):
            return None
    else:
        if not (clear_above(brk, brk["ema9"]) and clear_above(brk, brk["ema15"])):
            return None
        if not (clear_above(entry_bar, entry_bar["ema9"]) and clear_above(entry_bar, entry_bar["ema15"])):
            return None

    day_hi, day_lo = day_range(bars, entry_idx)
    day_rng = day_hi - day_lo
    if use_day_prox and day_rng > 0:
        if is_buy:
            if (entry_bar["c"] - day_lo) / day_rng > prox_pct:
                return None
        else:
            if (day_hi - entry_bar["c"]) / day_rng > prox_pct:
                return None

    entry = entry_bar["c"]
    sl = compute_sl(bars, entry_idx, break_idx - 1, entry, is_buy, sl_lookback)
    risk = abs(entry - sl)
    if risk <= 0:
        return None
    if is_buy and sl >= entry:
        return None
    if not is_buy and sl <= entry:
        return None
    target = entry + risk * rr if is_buy else entry - risk * rr
    return {
        "side": "BUY" if is_buy else "SELL",
        "entry": entry,
        "sl": sl,
        "target": target,
        "risk": risk,
        "rr": rr,
        "entry_idx": entry_idx,
        "break_idx": break_idx,
        "broken_idx": break_idx - 1,
        "entry_t": entry_bar["t"],
        "break_t": brk["t"],
        "broken_t": broken["t"],
        "day_hi": day_hi,
        "day_lo": day_lo,
        "confirm": "next",  # always next candle after green/red fail break
    }


def scan_day(bars, use_day_prox, prox_pct, sl_lookback, rr):
    trades = []
    i = 2  # first candle fully ignored (not even broken reference)
    while i < len(bars):
        buy = try_signal(bars, i, True, use_day_prox, prox_pct, sl_lookback, rr)
        if buy:
            trades.append(buy)
            i = buy["entry_idx"] + 1
            continue
        sell = try_signal(bars, i, False, use_day_prox, prox_pct, sl_lookback, rr)
        if sell:
            trades.append(sell)
            i = sell["entry_idx"] + 1
            continue
        i += 1
    return trades


def session_days(rows, analyze_from, analyze_to):
    by_day = defaultdict(list)
    for r in rows:
        t = datetime.fromisoformat(r["t"])
        if t.date() < analyze_from or t.date() > analyze_to:
            continue
        if (t.hour, t.minute) < (9, 15) or (t.hour, t.minute) > (15, 30):
            continue
        by_day[t.date()].append(r)
    return sorted(by_day.items())


def resolve_outcome(trade, bars):
    """Bars after entry candle. Same-bar SL+TP → SL first."""
    buy = trade["side"] == "BUY"
    sl, tgt = trade["sl"], trade["target"]
    after = bars[trade["entry_idx"] + 1 :]
    for b in after:
        if buy:
            hit_sl = b["l"] <= sl
            hit_tg = b["h"] >= tgt
            if hit_sl:
                return "SL", b["t"], sl
            if hit_tg:
                return "TARGET", b["t"], tgt
        else:
            hit_sl = b["h"] >= sl
            hit_tg = b["l"] <= tgt
            if hit_sl:
                return "SL", b["t"], sl
            if hit_tg:
                return "TARGET", b["t"], tgt
    last = after[-1] if after else None
    return "OPEN", (last["t"] if last else None), (last["c"] if last else None)


def hhmm(iso):
    return datetime.fromisoformat(iso).strftime("%H:%M") if iso else "-"


def main():
    force = "--force" in sys.argv
    quiet = "--quiet" in sys.argv
    use_day_prox = "--no-day-prox" not in sys.argv  # default ON; off only when asked
    prox_pct = float(arg_val(sys.argv, "prox", "0.10"))
    sl_lookback = int(arg_val(sys.argv, "sl-lookback", "2"))
    rr = float(arg_val(sys.argv, "rr", "3"))
    tfs = [x.strip() for x in arg_val(sys.argv, "tfs", ",".join(DEFAULT_TFS)).split(",") if x.strip()]
    fetch_from = arg_val(sys.argv, "fetch-from", "2026-04-20")
    analyze_from_s = arg_val(sys.argv, "analyze-from", arg_val(sys.argv, "from", "2026-05-01"))
    analyze_to_s = arg_val(sys.argv, "to", "2026-07-30")
    analyze_from = datetime.strptime(analyze_from_s, "%Y-%m-%d").date()
    analyze_to = datetime.strptime(analyze_to_s, "%Y-%m-%d").date()

    cid, access = load_auth()
    print(f"Cache dir: {CACHE}")
    print("Rule: EMA failed-break | first candle ignore | EMA9+15 clear")
    print("BUY: High-break GREEN then next RED | SELL: Low-break RED then next GREEN")
    print("EMA clear on break+entry | No entry after 14:45 | Skip 2nd consecutive break")
    print("First candle fully ignored (not used as broken reference)")
    print(f"Day proximity: {'ON '+str(prox_pct*100)+'%' if use_day_prox else 'OFF'}")
    print(f"SL lookback={sl_lookback} (+50% cap) | Target 1:{rr}")
    print(f"TFs: {', '.join(tfs)} | Analyze {analyze_from_s}→{analyze_to_s}\n")

    trades_log = []
    by_tf = defaultdict(lambda: {"BUY": 0, "SELL": 0, "TARGET": 0, "SL": 0, "OPEN": 0})

    for name, sym in SYMS:
        print(f"===== {name} =====")
        for res in tfs:
            rows = fetch_range(cid, access, sym, res, fetch_from, analyze_to_s, force=force)
            if len(rows) < 30:
                print(f"  {res}m: insufficient data")
                continue
            closes = [r["c"] for r in rows]
            e9, e15 = ema(closes, 9), ema(closes, 15)
            for i, r in enumerate(rows):
                r["ema9"], r["ema15"] = e9[i], e15[i]

            days = session_days(rows, analyze_from, analyze_to)
            print(f"  --- {res}m --- days={len(days)}")
            for day, bars in days:
                # bars already session-filtered; ensure sorted
                bars = sorted(bars, key=lambda x: x["t"])
                day_trades = scan_day(bars, use_day_prox, prox_pct, sl_lookback, rr)
                if not day_trades and not quiet:
                    print(f"    {day} → NONE")
                for tr in day_trades:
                    outcome, exit_t, _ = resolve_outcome(tr, bars)
                    tr["outcome"], tr["exit_t"] = outcome, exit_t
                    trades_log.append((name, res, str(day), tr))
                    by_tf[res][tr["side"]] += 1
                    by_tf[res][outcome] += 1
                    print(
                        f"    {day} → {tr['side']:4} conf={tr['confirm']:5} "
                        f"@{hhmm(tr['entry_t'])} Entry={tr['entry']:.2f} "
                        f"SL={tr['sl']:.2f} T={tr['target']:.2f} risk={tr['risk']:.2f} "
                        f"→ {outcome}" + (f" @{hhmm(exit_t)}" if exit_t and outcome != "OPEN" else "")
                    )

    print("\n========== BY TIMEFRAME ==========")
    print(f"{'TF':5} {'Sig':5} {'BUY':5} {'SELL':5} {'TGT':5} {'SL':5} {'OPEN':5} {'NetR':8} {'Win%':6}")
    for res in tfs:
        b = by_tf[res]
        sig = b["BUY"] + b["SELL"]
        nr = b["TARGET"] * rr + b["SL"] * (-1.0)
        wr = (100.0 * b["TARGET"] / sig) if sig else 0.0
        print(f"{res+'m':5} {sig:5} {b['BUY']:5} {b['SELL']:5} {b['TARGET']:5} {b['SL']:5} {b['OPEN']:5} {nr:+8.1f} {wr:5.1f}%")

    print("\n========== BY INDEX ==========")
    for name, _ in SYMS:
        rows_t = [t for t in trades_log if t[0] == name]
        buys = sum(1 for *_, tr in rows_t if tr["side"] == "BUY")
        sells = sum(1 for *_, tr in rows_t if tr["side"] == "SELL")
        tgt = sum(1 for *_, tr in rows_t if tr["outcome"] == "TARGET")
        sl = sum(1 for *_, tr in rows_t if tr["outcome"] == "SL")
        opn = sum(1 for *_, tr in rows_t if tr["outcome"] == "OPEN")
        print(f"{name:8} sig={buys+sells:3} BUY={buys:3} SELL={sells:3} TGT={tgt:3} SL={sl:3} OPEN={opn:3} NetR={tgt*rr-sl:+.1f}")

    buys = sum(1 for *_, tr in trades_log if tr["side"] == "BUY")
    sells = sum(1 for *_, tr in trades_log if tr["side"] == "SELL")
    tgt = sum(1 for *_, tr in trades_log if tr["outcome"] == "TARGET")
    sl = sum(1 for *_, tr in trades_log if tr["outcome"] == "SL")
    opn = sum(1 for *_, tr in trades_log if tr["outcome"] == "OPEN")
    n = buys + sells
    print(f"\n========== TOTALS RR=1:{rr} | dayProx={'ON' if use_day_prox else 'OFF'} ==========")
    print(f"Signals: BUY={buys} SELL={sells} total={n}")
    print(f"Outcomes: TARGET={tgt} SL={sl} OPEN={opn} Win%={(100*tgt/n if n else 0):.1f}%")
    print(f"Net R (OPEN=0): {tgt*rr - sl:+.1f}R")

    print("\n========== DETAILED TRADES ==========")
    print(
        f"{'#':3} {'Index':7} {'TF':4} {'Day':10} {'Side':4} {'Conf':5} "
        f"{'Brk':5} {'Ent':5} {'Entry':>10} {'SL':>10} {'Target':>10} {'Risk':>8} "
        f"{'Out':6} {'Exit':5} {'R':>5}"
    )
    for i, (name, res, day, tr) in enumerate(trades_log, 1):
        r_mult = rr if tr["outcome"] == "TARGET" else (-1.0 if tr["outcome"] == "SL" else 0.0)
        print(
            f"{i:<3} {name:7} {res+'m':4} {day:10} {tr['side']:4} {tr['confirm']:5} "
            f"{hhmm(tr['break_t']):5} {hhmm(tr['entry_t']):5} "
            f"{tr['entry']:10.2f} {tr['sl']:10.2f} {tr['target']:10.2f} {tr['risk']:8.2f} "
            f"{tr['outcome']:6} {hhmm(tr['exit_t']):5} {r_mult:5.1f}"
        )


if __name__ == "__main__":
    main()
