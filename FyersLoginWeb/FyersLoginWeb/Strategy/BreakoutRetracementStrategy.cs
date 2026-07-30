using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// 30-min reference candle par based "double breach with retracement" strategy.
    /// Direction-aware — Long (BUY) aur Short (SELL) dono.
    ///
    /// LONG (BUY):
    ///   1) First breakout : 3m High > 30m High
    ///   2) Retracement    : 3m Low  &lt;= (High - Range*pct)
    ///   3) Second breakout: 3m High > 30m High
    ///   4) Entry zone      : [High .. High + buffer]  (chasing nahi)
    ///   SL = retrLevel - slBuf ; Target = Entry + Risk*RR (upar)
    ///
    /// SHORT (SELL) — mirror:
    ///   1) First breakdown : 3m Low  &lt; 30m Low
    ///   2) Retracement     : 3m High >= (Low + Range*pct)
    ///   3) Second breakdown: 3m Low  &lt; 30m Low
    ///   4) Entry zone       : [Low - buffer .. Low]
    ///   SL = retrLevel + slBuf ; Target = Entry - Risk*RR (neeche)
    ///
    /// Ek reference par sirf ek signal. Sirf CLOSED 3-min candles pass karo.
    /// </summary>
    public class BreakoutRetracementStrategy
    {
        private readonly StrategyConfig _config;
        private readonly TradeSide _side;

        private ReferenceCandle? _reference;
        private decimal _retracementLevel;

        private decimal _firstBreachPrice;
        private DateTime _firstBreachTime;
        private decimal _retracementExtreme;
        private DateTime _retracementTime;
        private decimal _secondBreachPrice;
        private DateTime _secondBreachTime;

        public StrategyState State { get; private set; } = StrategyState.WaitingForFirstBreakout;
        public ReferenceCandle? Reference => _reference;
        public decimal RetracementLevel => _retracementLevel;
        public TradeSide Side => _side;

        private bool IsLong => _side == TradeSide.Long;

        // cycle ka pehla breakout ho chuka? (prior-high check sirf pehle breakout pe lagta hai)
        private bool _hadFirstBreakout;

        /// <summary>
        /// MANUAL-style: hamesha 30m reference High/Low.
        /// Prior-high mix nahi — warna chart pe dikhne wala breakout bot skip kar deta hai.
        /// </summary>
        private decimal EffLevel => IsLong ? _reference!.High : _reference!.Low;

        /// <summary>Entry @ reference High/Low (+ buffer).</summary>
        public decimal EntryCapPrice => IsLong
            ? _reference!.High + _config.EntryBufferPoints
            : _reference!.Low - _config.EntryBufferPoints;

        /// <summary>
        /// Retracement confirm: soft = pullback to EffLevel; else full RetracementPercent (50%).
        /// </summary>
        private bool IsRetracementTouch(Candle c) =>
            _config.SoftRetracementConfirm
                ? (IsLong ? c.Low <= EffLevel : c.High >= EffLevel)
                : (IsLong ? c.Low <= _retracementLevel : c.High >= _retracementLevel);

        public BreakoutRetracementStrategy(StrategyConfig? config = null, TradeSide side = TradeSide.Long)
        {
            _config = config ?? new StrategyConfig();
            _side = side;
        }

        public void SetReference(ReferenceCandle reference)
        {
            _reference = reference ?? throw new ArgumentNullException(nameof(reference));
            // retracement level: long neeche (High - Range*pct), short upar (Low + Range*pct)
            _retracementLevel = IsLong
                ? reference.High - reference.Range * _config.RetracementPercent
                : reference.Low + reference.Range * _config.RetracementPercent;
            Reset();
        }

        public void Reset()
        {
            State = StrategyState.WaitingForFirstBreakout;
            _firstBreachPrice = 0m; _firstBreachTime = default;
            _retracementExtreme = 0m; _retracementTime = default;
            _secondBreachPrice = 0m; _secondBreachTime = default;
            _hadFirstBreakout = false;
        }

        public StrategyResult ProcessThreeMinuteCandle(Candle candle)
        {
            if (candle == null) throw new ArgumentNullException(nameof(candle));
            if (_reference == null)
                return Info("Reference 30m candle set nahi hai.");
            if (State == StrategyState.BuySignal)
                return Info("Signal already generated is reference par. Reset ka intezaar.");

            return State switch
            {
                StrategyState.WaitingForFirstBreakout => HandleFirstBreach(candle),
                StrategyState.WaitingForRetracement => HandleRetracement(candle),
                StrategyState.WaitingForSecondBreakout => HandleSecondBreach(candle),
                StrategyState.WaitingForEntry => HandleEntry(candle),
                _ => Info($"No-op in state {State}.")
            };
        }

        // Step 1: pehla breach (BUY/SELL nahi)
        private StrategyResult HandleFirstBreach(Candle c)
        {
            // pehla breakout -> EffLevel me prior-high (resistance) bhi shaamil
            bool breached = IsLong ? c.High > EffLevel : c.Low < EffLevel;
            if (breached)
            {
                _firstBreachPrice = IsLong ? c.High : c.Low;
                _firstBreachTime = c.StartTime;
                _hadFirstBreakout = true; // ab aage 2nd breakout sirf reference high dekhega
                State = StrategyState.WaitingForRetracement;

                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.FirstBreakoutConfirmed,
                    Message = IsLong
                        ? $"FIRST_BREAKOUT: 3m High {c.High} > refHigh {EffLevel}. Ab retracement (neeche)."
                        : $"FIRST_BREAKDOWN: 3m Low {c.Low} < refLow {EffLevel}. Ab retracement (upar)."
                };
            }

            // NAYA: breakout se PEHLE hi retracement ho gaya (price 50% tak aa gayi) to
            // seedha WaitingForSecondBreakout — ab agla breakout hi BUY/SELL dega.
            // UseRetracementFirst=false -> ye loose path OFF: pehle asli breakout zaroori.
            bool retrEarly = _config.UseRetracementFirst &&
                (IsLong ? c.Low <= _retracementLevel : c.High >= _retracementLevel);
            if (retrEarly)
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
                State = StrategyState.WaitingForSecondBreakout;

                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.RetracementConfirmed,
                    Message = IsLong
                        ? $"RETRACEMENT-first: 3m Low {c.Low} <= level {_retracementLevel} (breakout se pehle). Ab breakout hi BUY dega."
                        : $"RETRACEMENT-first: 3m High {c.High} >= level {_retracementLevel} (breakdown se pehle). Ab breakdown hi SELL dega."
                };
            }

            return Info(IsLong
                ? $"Waiting first breakout: 3m High {c.High} <= 30m High {_reference!.High}."
                : $"Waiting first breakdown: 3m Low {c.Low} >= 30m Low {_reference!.Low}.");
        }

        // Step 2: retracement (BUY/SELL nahi)
        private StrategyResult HandleRetracement(Candle c)
        {
            // freshness: first breakout ke baad 15 min me retracement na aaye to setup drop
            if ((c.StartTime - _firstBreachTime).TotalMinutes > _config.SetupFreshnessMinutes)
            {
                Reset();
                return HandleFirstBreach(c); // isi candle ko naye setup ki tarah dekho
            }

            bool retr = IsRetracementTouch(c);
            if (retr)
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
                State = StrategyState.WaitingForSecondBreakout;

                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.RetracementConfirmed,
                    Message = IsLong
                        ? (_config.SoftRetracementConfirm
                            ? $"RETRACEMENT: 3m Low {c.Low} <= refHigh {EffLevel} (soft, 50% not required). Ab 2nd breakout."
                            : $"RETRACEMENT: 3m Low {c.Low} <= level {_retracementLevel}. Ab 2nd breakout.")
                        : (_config.SoftRetracementConfirm
                            ? $"RETRACEMENT: 3m High {c.High} >= refLow {EffLevel} (soft, 50% not required). Ab 2nd breakdown."
                            : $"RETRACEMENT: 3m High {c.High} >= level {_retracementLevel}. Ab 2nd breakdown.")
                };
            }
            return Info(IsLong
                ? (_config.SoftRetracementConfirm
                    ? $"Waiting retracement: 3m Low {c.Low} > refHigh {EffLevel}."
                    : $"Waiting retracement: 3m Low {c.Low} > level {_retracementLevel}.")
                : (_config.SoftRetracementConfirm
                    ? $"Waiting retracement: 3m High {c.High} < refLow {EffLevel}."
                    : $"Waiting retracement: 3m High {c.High} < level {_retracementLevel}."));
        }

        // Step 3: doosra breach -> setup ready, entry dekho
        private StrategyResult HandleSecondBreach(Candle c)
        {
            // agar is candle par phir se soft/full retracement touch hua to time refresh
            if (IsRetracementTouch(c))
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
            }

            // Confirm + entry level = reference High/Low (manual chart jaisa).
            decimal lvl = EffLevel;
            bool breached = IsLong ? c.High > lvl : c.Low < lvl;
            if (breached)
            {
                // retest freshness: retracement ke 15 min ke andar hi breakout valid
                if ((c.StartTime - _retracementTime).TotalMinutes > _config.SetupFreshnessMinutes)
                {
                    State = StrategyState.WaitingForRetracement; // stale -> fresh retracement chahiye
                    return Info(IsLong
                        ? $"Breakout par retest stale ({(c.StartTime - _retracementTime).TotalMinutes:F0}m purana). Fresh retracement ka intezaar."
                        : $"Breakdown par retest stale. Fresh retracement ka intezaar.");
                }

                _secondBreachPrice = IsLong ? c.High : c.Low;
                _secondBreachTime = c.StartTime;

                decimal cap = EntryCapPrice;

                bool needConfirm = _config.RequireCloseConfirm
                    || (_config.ConfirmRetrFirstOnly && !_hadFirstBreakout);
                if (!needConfirm)
                    return GenerateSignal(c, cap);

                bool holdFailed = IsLong ? c.Close < lvl : c.Close > lvl;
                if (holdFailed)
                    return Info(IsLong
                        ? $"Breakout wick par Close {c.Close} < refHigh {lvl}. Valid close ka intezaar."
                        : $"Breakdown wick par Close {c.Close} > refLow {lvl}. Valid close ka intezaar.");

                bool canFillNow = IsLong ? c.Low <= cap : c.High >= cap;
                if (canFillNow)
                    return GenerateSignal(c, cap);

                State = StrategyState.WaitingForEntry;
                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.SecondBreakoutConfirmed,
                    Message = IsLong
                        ? $"SECOND_BREAKOUT CLOSE-OK vs refHigh {lvl}; candle cap se upar gap. Pullback wait."
                        : $"SECOND_BREAKDOWN CLOSE-OK vs refLow {lvl}; candle cap se neeche gap. Pullback wait."
                };
            }
            return Info(IsLong
                ? $"Waiting 2nd breakout: 3m High {c.High} <= refHigh {lvl}."
                : $"Waiting 2nd breakdown: 3m Low {c.Low} >= refLow {lvl}.");
        }

        // Step 4: price pullback me entry-zone tak aaya? to entry
        private StrategyResult HandleEntry(Candle c)
        {
            // long: price neeche cap tak (Low <= cap) ; short: price upar cap tak (High >= cap)
            bool reached = IsLong ? c.Low <= EntryCapPrice : c.High >= EntryCapPrice;
            if (reached)
                return GenerateSignal(c, EntryCapPrice);

            return Info(IsLong
                ? $"Waiting entry: 3m Low {c.Low} > cap {EntryCapPrice}."
                : $"Waiting entry: 3m High {c.High} < cap {EntryCapPrice}.");
        }

        // Entry fill -> signal with SL/target
        private StrategyResult GenerateSignal(Candle c, decimal entry)
        {
            decimal sl = IsLong
                ? _retracementLevel - _config.StopLossBufferPoints
                : _retracementLevel + _config.StopLossBufferPoints;
            decimal risk = IsLong ? entry - sl : sl - entry;
            decimal target = IsLong
                ? entry + risk * _config.RiskRewardRatio
                : entry - risk * _config.RiskRewardRatio;

            State = StrategyState.BuySignal; // terminal (dono side ke liye "signal generated")

            var signal = new TradeSignal
            {
                Side = _side,
                Reference = _reference!,
                FirstBreachPrice = _firstBreachPrice,
                FirstBreachTime = _firstBreachTime,
                RetracementLevel = _retracementLevel,
                RetracementExtreme = _retracementExtreme,
                RetracementTime = _retracementTime,
                SecondBreachPrice = _secondBreachPrice,
                SecondBreachTime = _secondBreachTime,
                EntryPrice = entry,
                EntryTime = c.StartTime,
                StopLoss = sl,
                Target = target,
                Risk = risk,
                RiskRewardRatio = _config.RiskRewardRatio
            };

            return new StrategyResult
            {
                State = State,
                JustConfirmed = StrategyState.BuySignal,
                Signal = signal,
                Message = $"{(IsLong ? "BUY" : "SELL")} @ {entry} | SL {sl} | Target {target} " +
                          $"(1:{_config.RiskRewardRatio}) | Risk {risk}."
            };
        }

        private StrategyResult Info(string msg) =>
            new StrategyResult { State = State, Message = msg };
    }
}
