using System;
using System.Collections.Generic;
using System.Linq;

namespace FyersLoginWeb.Strategy
{
    /// <summary>Ek 3-min candle par strategy ka snapshot (UI/log ke liye).</summary>
    public class BacktestStep
    {
        public Candle Candle { get; init; } = null!;
        public ReferenceCandle Reference { get; init; } = null!;
        public StrategyResult Result { get; init; } = null!;
    }

    /// <summary>Ek din (ya candle set) ka poora backtest result — Long + Short dono.</summary>
    public class BacktestResult
    {
        public int ReferenceCount { get; init; }
        public int ThreeMinCount { get; init; }

        public List<TradeSignal> BuySignals { get; init; } = new();   // Long
        public List<TradeSignal> SellSignals { get; init; } = new();  // Short
        public List<BacktestStep> LongSteps { get; init; } = new();
        public List<BacktestStep> ShortSteps { get; init; } = new();

        public int BuyTargetHits => BuySignals.Count(s => s.Outcome == TradeOutcome.TargetHit);
        public int BuyStopLossHits => BuySignals.Count(s => s.Outcome == TradeOutcome.StopLossHit);
        public int BuyTrailStops => BuySignals.Count(s => s.Outcome == TradeOutcome.TrailStopHit);
        public int BuyOpen => BuySignals.Count(s => s.Outcome == TradeOutcome.Open);

        public int SellTargetHits => SellSignals.Count(s => s.Outcome == TradeOutcome.TargetHit);
        public int SellStopLossHits => SellSignals.Count(s => s.Outcome == TradeOutcome.StopLossHit);
        public int SellTrailStops => SellSignals.Count(s => s.Outcome == TradeOutcome.TrailStopHit);
        public int SellOpen => SellSignals.Count(s => s.Outcome == TradeOutcome.Open);

        // square-off (2:45) par band hue trades
        public int BuyTimeExits => BuySignals.Count(s => s.Outcome == TradeOutcome.TimeExit);
        public int SellTimeExits => SellSignals.Count(s => s.Outcome == TradeOutcome.TimeExit);

        // precise realized net-R (har outcome ka actual R jode) — aggregate net R ke liye
        public decimal BuyNetR => BuySignals.Sum(s => s.RealizedR);
        public decimal SellNetR => SellSignals.Sum(s => s.RealizedR);
        public decimal BuyTimeExitR => BuySignals.Where(s => s.Outcome == TradeOutcome.TimeExit).Sum(s => s.RealizedR);
        public decimal SellTimeExitR => SellSignals.Where(s => s.Outcome == TradeOutcome.TimeExit).Sum(s => s.RealizedR);

        public int TimeExitWins => BuySignals.Concat(SellSignals)
            .Count(s => s.Outcome == TradeOutcome.TimeExit && s.RealizedR > 0);
        public int TimeExitLosses => BuySignals.Concat(SellSignals)
            .Count(s => s.Outcome == TradeOutcome.TimeExit && s.RealizedR < 0);
    }

    /// <summary>
    /// 3-min candles ke set par poori strategy chalata hai (backtest) — Long aur Short
    /// dono parallel. Pure logic — koi API/DB/UI nahi.
    /// </summary>
    public static class StrategyBacktester
    {
        // onlyRefStart diya ho to SIRF us ek 30-min reference ko use karo (rolling/abandon nahi).
        public static BacktestResult Run(IEnumerable<Candle> threeMinCandles, StrategyConfig config,
            TimeSpan? onlyRefStart = null)
        {
            var threeMin = threeMinCandles.OrderBy(c => c.StartTime).ToList();

            // 30-min reference candles (3-min se aggregate)
            var windowAgg = new Dictionary<DateTime, Candle>();
            foreach (var c in threeMin)
            {
                var w = MarketSession.GetReferenceWindow(c.StartTime);
                if (w == null) continue;
                var start = w.Value.start;

                if (!windowAgg.TryGetValue(start, out var agg))
                    windowAgg[start] = new Candle(start, w.Value.end, c.Open, c.High, c.Low, c.Close);
                else
                {
                    agg.High = Math.Max(agg.High, c.High);
                    agg.Low = Math.Min(agg.Low, c.Low);
                    agg.Close = c.Close;
                }
            }

            var windows = windowAgg.Values.OrderBy(w => w.EndTime).ToList();
            int wi = 0;

            // har window ke liye prior high/low (pichhle N windows ka max high / min low)
            var priorHigh = new decimal?[windows.Count];
            var priorLow = new decimal?[windows.Count];
            if (config.UsePriorLevelBreak)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    int from = Math.Max(0, i - config.PriorLevelLookback);
                    decimal? ph = null, pl = null;
                    for (int j = from; j < i; j++)
                    {
                        ph = ph.HasValue ? Math.Max(ph.Value, windows[j].High) : windows[j].High;
                        pl = pl.HasValue ? Math.Min(pl.Value, windows[j].Low) : windows[j].Low;
                    }
                    priorHigh[i] = ph; priorLow[i] = pl;
                }
            }

            var longMgr = new StrategyManager(config, TradeSide.Long);
            var shortMgr = new StrategyManager(config, TradeSide.Short);

            var buySignals = new List<TradeSignal>();
            var sellSignals = new List<TradeSignal>();
            longMgr.OnSignal += s => buySignals.Add(s);
            shortMgr.OnSignal += s => sellSignals.Add(s);

            var longSteps = new List<BacktestStep>();
            var shortSteps = new List<BacktestStep>();

            foreach (var c in threeMin)
            {
                while (wi < windows.Count && windows[wi].EndTime <= c.StartTime)
                {
                    int idx = wi;
                    var win = windows[wi];
                    wi++;
                    // single-reference mode: sirf matching window feed karo
                    if (onlyRefStart != null && win.StartTime.TimeOfDay != onlyRefStart.Value)
                        continue;
                    longMgr.OnThirtyMinuteCandleClosed(win, priorHigh[idx], priorLow[idx]);
                    shortMgr.OnThirtyMinuteCandleClosed(win, priorHigh[idx], priorLow[idx]);
                }

                if (longMgr.CurrentReference != null)
                {
                    var before = longMgr.CurrentReference;
                    var r = longMgr.OnThreeMinuteCandleClosed(c);
                    var refUsed = r.Signal?.Reference ?? longMgr.CurrentReference ?? before;
                    longSteps.Add(new BacktestStep { Candle = c, Reference = refUsed, Result = r });
                }

                if (shortMgr.CurrentReference != null)
                {
                    var before = shortMgr.CurrentReference;
                    var r = shortMgr.OnThreeMinuteCandleClosed(c);
                    var refUsed = r.Signal?.Reference ?? shortMgr.CurrentReference ?? before;
                    shortSteps.Add(new BacktestStep { Candle = c, Reference = refUsed, Result = r });
                }
            }

            // no-entry-after cutoff: 2:45 (ya jo bhi SquareOffTime) ke baad koi entry nahi
            if (config.UseSquareOff)
            {
                buySignals.RemoveAll(s => s.EntryTime.TimeOfDay >= config.SquareOffTime);
                sellSignals.RemoveAll(s => s.EntryTime.TimeOfDay >= config.SquareOffTime);
            }

            // reference max-wait: reference EndTime (monitoring start) ke 1 ghante baad koi entry nahi
            // (us reference ko chhod do). e.g. 10:45 ref -> cutoff 12:15.
            if (config.UseReferenceMaxWait)
            {
                buySignals.RemoveAll(s => s.EntryTime > s.Reference.EndTime.AddMinutes(config.MaxReferenceWaitMinutes));
                sellSignals.RemoveAll(s => s.EntryTime > s.Reference.EndTime.AddMinutes(config.MaxReferenceWaitMinutes));
            }

            EvaluateOutcomes(buySignals, threeMin, config);
            EvaluateOutcomes(sellSignals, threeMin, config);

            return new BacktestResult
            {
                ReferenceCount = windowAgg.Count,
                ThreeMinCount = threeMin.Count,
                BuySignals = buySignals,
                SellSignals = sellSignals,
                LongSteps = longSteps,
                ShortSteps = shortSteps
            };
        }

        /// <summary>
        /// LIVE tick entry: candles-so-far feed karke LONG manager ki CURRENT state return.
        /// WaitingForSecondBreakout / WaitingForEntry pe EntryCapLevel watch arm hota hai.
        /// </summary>
        public static StrategyManager RunLongManager(IEnumerable<Candle> threeMinCandles,
            StrategyConfig config, TimeSpan? onlyRefStart = null)
        {
            var threeMin = threeMinCandles.OrderBy(c => c.StartTime).ToList();

            var windowAgg = new Dictionary<DateTime, Candle>();
            foreach (var c in threeMin)
            {
                var w = MarketSession.GetReferenceWindow(c.StartTime);
                if (w == null) continue;
                var start = w.Value.start;
                if (!windowAgg.TryGetValue(start, out var agg))
                    windowAgg[start] = new Candle(start, w.Value.end, c.Open, c.High, c.Low, c.Close);
                else { agg.High = Math.Max(agg.High, c.High); agg.Low = Math.Min(agg.Low, c.Low); agg.Close = c.Close; }
            }
            var windows = windowAgg.Values.OrderBy(w => w.EndTime).ToList();

            var priorHigh = new decimal?[windows.Count];
            var priorLow = new decimal?[windows.Count];
            if (config.UsePriorLevelBreak)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    int from = Math.Max(0, i - config.PriorLevelLookback);
                    decimal? ph = null, pl = null;
                    for (int j = from; j < i; j++)
                    {
                        ph = ph.HasValue ? Math.Max(ph.Value, windows[j].High) : windows[j].High;
                        pl = pl.HasValue ? Math.Min(pl.Value, windows[j].Low) : windows[j].Low;
                    }
                    priorHigh[i] = ph; priorLow[i] = pl;
                }
            }

            var longMgr = new StrategyManager(config, TradeSide.Long);
            int wi = 0;
            foreach (var c in threeMin)
            {
                while (wi < windows.Count && windows[wi].EndTime <= c.StartTime)
                {
                    int idx = wi; var win = windows[wi]; wi++;
                    if (onlyRefStart != null && win.StartTime.TimeOfDay != onlyRefStart.Value) continue;
                    longMgr.OnThirtyMinuteCandleClosed(win, priorHigh[idx], priorLow[idx]);
                }
                if (longMgr.CurrentReference != null)
                    longMgr.OnThreeMinuteCandleClosed(c);
            }
            return longMgr;
        }

        // Entry ke baad outcome. Trailing on ho to:
        //   - price 2R (TrailActivateRR) tak jaye -> SL entry±offset, target 2.5R (TrailTargetRR)
        private static void EvaluateOutcomes(List<TradeSignal> signals, List<Candle> threeMin, StrategyConfig cfg)
        {
            foreach (var sig in signals)
            {
                decimal R = sig.Risk;
                bool trailing = false;

                // trailing ke levels (activate hone ke baad)
                decimal activate = sig.IsLong
                    ? sig.EntryPrice + cfg.TrailActivateRR * R
                    : sig.EntryPrice - cfg.TrailActivateRR * R;
                decimal trailSL = sig.IsLong
                    ? sig.EntryPrice + cfg.TrailStopOffsetPoints
                    : sig.EntryPrice - cfg.TrailStopOffsetPoints;
                decimal trailTgt = sig.IsLong
                    ? sig.EntryPrice + cfg.TrailTargetRR * R
                    : sig.EntryPrice - cfg.TrailTargetRR * R;

                foreach (var c in threeMin.Where(x => x.StartTime > sig.EntryTime))
                {
                    // square-off: 2:45 (SquareOffTime) wali candle par abhi tak open hai to
                    // us candle ke OPEN price par force-close kar do (TimeExit).
                    if (cfg.UseSquareOff && c.StartTime.TimeOfDay >= cfg.SquareOffTime)
                    {
                        Set(sig, TradeOutcome.TimeExit, c.Open, c);
                        break;
                    }

                    if (!cfg.UseTrailing)
                    {
                        // simple: original SL / target
                        if (sig.IsLong)
                        {
                            if (c.Low <= sig.StopLoss) { Set(sig, TradeOutcome.StopLossHit, sig.StopLoss, c); break; }
                            if (c.High >= sig.Target) { Set(sig, TradeOutcome.TargetHit, sig.Target, c); break; }
                        }
                        else
                        {
                            if (c.High >= sig.StopLoss) { Set(sig, TradeOutcome.StopLossHit, sig.StopLoss, c); break; }
                            if (c.Low <= sig.Target) { Set(sig, TradeOutcome.TargetHit, sig.Target, c); break; }
                        }
                        continue;
                    }

                    // ---- Trailing ON ----
                    if (!trailing)
                    {
                        // Phase A: abhi original SL, aur 2R ka intezaar
                        if (sig.IsLong)
                        {
                            if (c.Low <= sig.StopLoss) { Set(sig, TradeOutcome.StopLossHit, sig.StopLoss, c); break; }
                            if (c.High >= activate)
                            {
                                trailing = true;
                                if (c.High >= trailTgt) { Set(sig, TradeOutcome.TargetHit, trailTgt, c); break; }
                            }
                        }
                        else
                        {
                            if (c.High >= sig.StopLoss) { Set(sig, TradeOutcome.StopLossHit, sig.StopLoss, c); break; }
                            if (c.Low <= activate)
                            {
                                trailing = true;
                                if (c.Low <= trailTgt) { Set(sig, TradeOutcome.TargetHit, trailTgt, c); break; }
                            }
                        }
                    }
                    else
                    {
                        // Phase B: SL entry±1, target 2.5R
                        if (sig.IsLong)
                        {
                            if (c.Low <= trailSL) { Set(sig, TradeOutcome.TrailStopHit, trailSL, c); break; }
                            if (c.High >= trailTgt) { Set(sig, TradeOutcome.TargetHit, trailTgt, c); break; }
                        }
                        else
                        {
                            if (c.High >= trailSL) { Set(sig, TradeOutcome.TrailStopHit, trailSL, c); break; }
                            if (c.Low <= trailTgt) { Set(sig, TradeOutcome.TargetHit, trailTgt, c); break; }
                        }
                    }
                }
            }
        }

        private static void Set(TradeSignal s, TradeOutcome o, decimal price, Candle c)
        {
            s.Outcome = o;
            s.OutcomePrice = price;
            s.OutcomeTime = c.StartTime;
        }
    }
}
