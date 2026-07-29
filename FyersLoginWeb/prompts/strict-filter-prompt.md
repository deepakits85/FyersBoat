# Prompt: FyersBoat STRICT entry filter — kya tha, kya karna hai

Copy-paste below into a new agent/chat when continuing this work.

---

## CONTEXT (kya tha)

Repo: `deepakits85/FyersBoat` — ASP.NET Core breakout/retracement strategy (Fyers).

### Base strategy (filters se pehle)
- Instruments: Nifty, Sensex, BankNifty (priority: Nifty > Sensex > Bank)
- Reference windows (30m): **10:45, 11:15, 12:45**
- Logic: first breach → 50% retracement → second breach → entry
- SL = retracement level; RR Nifty/Bank **1:2**, Sensex **1:3**; trailing on; square-off **14:45**
- Portfolio: 30m gap between trades, max SL/day
- Options mode: ATM CE/PE buy (same signal logic on option candles)
- Live bot already uses **strict** via `EntryFilterConfig.ReduceStopLossStrictPreset()` in `LiveTradingService.cs`

### Problem jo solve kiya
Full-year index backtest (Aug 2025–Jul 2026, ~242 days) pe raw strategy:
- ~474 trades, SL% ~49%, avgR ~0.26, ~1.8 trades/day  
SL rate high tha → data-driven entry filters banaye.

### STRICT preset (`--filters strict`) — exact rules

Code: `FyersLoginWeb/FyersLoginWeb/Strategy/EntryFilterConfig.cs` → `ReduceStopLossStrictPreset()`

1. **Skip ref 10:45** — sirf 11:15 aur 12:45 allow
2. **Min lag ≥ 30 minutes** after reference end before entry
3. **Skip Tuesday**
4. **Skip Sensex** completely
5. **Min risk (bps)** = `(Risk / EntryPrice) * 10000`:
   - Nifty / NIFTY50 ≥ **6.0**
   - BankNifty / NIFTYBANK ≥ **7.5**
   - (tiny SL = noise; Q1 se neeche skip)

CLI:  
`dotnet run --project FyersLoginWeb/BacktestToday -- --mode index|options --from YYYY-MM-DD --to YYYY-MM-DD --filters strict`

### Results snapshot (jo mil chuka)

**Index full year (strict):**
- ~150 trades, NetR ~+64, SL% ~**33%**, avgR ~0.43, ~0.58 trades/day (~3/week)
- Monthly ~+5.3R → 1% risk/trade pe ₹1L ≈ ₹5.3k/mo (user ko kam laga daily-income goal ke liye)

**Options July 2026 cache (strict):** ~12 taken, ~+12R, Win% ~75  
**Options July (none):** ~32 taken, ~+27R

### Premium reality check (important)
- Pehle jo “ATM ~600” bola tha **galat mix** tha (Bank/Sensex).
- **Nifty ATM kabhi ~600 nahi** current expiry pe.
- July taken Nifty premiums: med **~243** (136–368); SL med **~9 pt**
- Bank med ~680, Sensex med ~900
- Cache/backtest mostly **monthly `26JUL`** symbols use karta hai — **weekly current expiry** nahi; near-weekly ATM ~100 user assumption alag/realistic near expiry.

### User P&L mental model (assumptions)
Nifty: prem ~100, SL ~10pt, tgt ~20–25, qty 1000 on 1L → ±10k / +20–25k; ~3 trades/week; 50% SL → ~₹60k/mo.  
Yeh tabhi hold karta hai jab weekly ATM ~100 + SL ~10 + clean 1:2 mile; July monthly cache pe prem higher, Bank/Sensex SL bada, trail/sq-off R dilute karta hai.

---

## TASK (kya karna hai)

Continue from STRICT filter as baseline. Do **not** invent a new unrelated strategy unless asked.

1. Treat **strict** as the live/default filter set (already wired in live).
2. When discussing premiums / P&L, always split **Nifty vs Bank vs Sensex**; never quote blended ATM premium as “Nifty”.
3. Prefer **weekly current-expiry** ATM symbols for Nifty options analysis when possible (not only monthly `YYMMM`).
4. If improving expectancy/income:
   - keep strict rules unless A/B shows better on holdout;
   - Nifty-first sizing at realistic premium (~100 near weekly expiry, SL~10pt);
   - report ₹ P&L with lot/qty constraints, not only R-multiples;
   - compare `none` / `reduce-sl` / `strict` / `ultra` before changing filters.
5. Do not commit secrets (`token.json`, API keys). Use existing CLI + `EntryFilterConfig` patterns.

### Optional next asks (pick if user wants)
- Nifty-only July options P&L at realistic qty on weekly ATM
- Collect/backtest weekly-expiry option symbols
- Tune strict (e.g. Nifty-only live) without raising SL%
- Paper/live checklist for strict rules

---

## One-liner for agents

> FyersBoat STRICT = skip 10:45 + lag≥30m + skip Tue + skip Sensex + min risk bps (Nifty≥6, Bank≥7.5). Live already uses it. Index ~3 trades/week, SL%~33, ~+5R/mo. Nifty ATM ≠ 600 (that was Bank/Sensex); Nifty July monthly med~243 / weekly near-expiry ~100. Next: weekly ATM Nifty P&L + realistic qty, keep strict as baseline.
