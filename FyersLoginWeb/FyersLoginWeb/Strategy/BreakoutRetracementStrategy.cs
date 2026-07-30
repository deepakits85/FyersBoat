using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// SIMPLE rule (user):
    ///   1) First breakout: 3m CLOSE above ref High (long) / below ref Low (short)
    ///   2) Retracement: &gt; RetracementPercent of range (default 20%)
    ///   3) Second breakout: BUY/SELL on breakout touch (wick) — close wait nahi
    ///   SL = StopLossRetracementPercent of ref range (default 50%)
    ///   Target = Entry ± Risk * RR
    /// </summary>
    public class BreakoutRetracementStrategy
    {
        private readonly StrategyConfig _config;
        private readonly TradeSide _side;

        private ReferenceCandle? _reference;
        private decimal _retracementLevel; // confirm level (e.g. 20%)
        private decimal _stopLossLevel;    // SL level (e.g. 50%)

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
        private bool _hadFirstBreakout;

        private decimal EffLevel => IsLong ? _reference!.High : _reference!.Low;

        public decimal EntryCapPrice => IsLong
            ? _reference!.High + _config.EntryBufferPoints
            : _reference!.Low - _config.EntryBufferPoints;

        private bool IsRetracementTouch(Candle c) =>
            IsLong ? c.Low <= _retracementLevel : c.High >= _retracementLevel;

        public BreakoutRetracementStrategy(StrategyConfig? config = null, TradeSide side = TradeSide.Long)
        {
            _config = config ?? new StrategyConfig();
            _side = side;
        }

        public void SetReference(ReferenceCandle reference)
        {
            _reference = reference ?? throw new ArgumentNullException(nameof(reference));
            _retracementLevel = IsLong
                ? reference.High - reference.Range * _config.RetracementPercent
                : reference.Low + reference.Range * _config.RetracementPercent;
            // SL: StopLossRetracementPercent=1.0 => ref Low (long) / ref High (short)
            _stopLossLevel = IsLong
                ? reference.High - reference.Range * _config.StopLossRetracementPercent
                : reference.Low + reference.Range * _config.StopLossRetracementPercent;
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

        private StrategyResult HandleFirstBreach(Candle c)
        {
            // Optional legacy path (OFF in simple config)
            if (_config.UseRetracementFirst && IsRetracementTouch(c))
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
                State = StrategyState.WaitingForSecondBreakout;
                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.RetracementConfirmed,
                    Message = $"RETRACEMENT-first @ {_retracementLevel}. Ab breakout."
                };
            }

            bool breached = _config.FirstBreakoutRequireClose
                ? (IsLong ? c.Close > EffLevel : c.Close < EffLevel)
                : (IsLong ? c.High > EffLevel : c.Low < EffLevel);

            if (breached)
            {
                _firstBreachPrice = IsLong ? c.High : c.Low;
                _firstBreachTime = c.StartTime;
                _hadFirstBreakout = true;
                State = StrategyState.WaitingForRetracement;
                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.FirstBreakoutConfirmed,
                    Message = IsLong
                        ? $"FIRST_BREAKOUT: Close {c.Close} > refHigh {EffLevel}. Ab >{_config.RetracementPercent:P0} retracement."
                        : $"FIRST_BREAKDOWN: Close {c.Close} < refLow {EffLevel}. Ab >{_config.RetracementPercent:P0} retracement."
                };
            }

            return Info(IsLong
                ? $"Waiting first breakout close > refHigh {EffLevel} (Close={c.Close})."
                : $"Waiting first breakdown close < refLow {EffLevel} (Close={c.Close}).");
        }

        private StrategyResult HandleRetracement(Candle c)
        {
            if (_config.UseSetupFreshness &&
                (c.StartTime - _firstBreachTime).TotalMinutes > _config.SetupFreshnessMinutes)
            {
                Reset();
                return HandleFirstBreach(c);
            }

            if (IsRetracementTouch(c))
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
                State = StrategyState.WaitingForSecondBreakout;
                return new StrategyResult
                {
                    State = State,
                    JustConfirmed = StrategyState.RetracementConfirmed,
                    Message = IsLong
                        ? $"RETRACEMENT: Low {c.Low} <= {_retracementLevel} (>{_config.RetracementPercent:P0}). Ab 2nd breakout."
                        : $"RETRACEMENT: High {c.High} >= {_retracementLevel} (>{_config.RetracementPercent:P0}). Ab 2nd breakdown."
                };
            }

            return Info(IsLong
                ? $"Waiting retracement: Low {c.Low} > {_retracementLevel}."
                : $"Waiting retracement: High {c.High} < {_retracementLevel}.");
        }

        private StrategyResult HandleSecondBreach(Candle c)
        {
            if (IsRetracementTouch(c))
            {
                _retracementExtreme = IsLong ? c.Low : c.High;
                _retracementTime = c.StartTime;
            }

            decimal lvl = EffLevel;
            bool breached = IsLong ? c.High > lvl : c.Low < lvl;
            if (!breached)
                return Info(IsLong
                    ? $"Waiting 2nd breakout: High {c.High} <= refHigh {lvl}."
                    : $"Waiting 2nd breakdown: Low {c.Low} >= refLow {lvl}.");

            if (_config.UseSetupFreshness &&
                (c.StartTime - _retracementTime).TotalMinutes > _config.SetupFreshnessMinutes)
            {
                State = StrategyState.WaitingForRetracement;
                return Info("2nd breakout stale — fresh retracement chahiye.");
            }

            _secondBreachPrice = IsLong ? c.High : c.Low;
            _secondBreachTime = c.StartTime;
            decimal cap = EntryCapPrice;

            // Simple: buy on breakout touch — close wait nahi
            bool needConfirm = _config.RequireCloseConfirm
                || (_config.ConfirmRetrFirstOnly && !_hadFirstBreakout);
            if (!needConfirm)
                return GenerateSignal(c, cap);

            bool holdFailed = IsLong ? c.Close < lvl : c.Close > lvl;
            if (holdFailed)
                return Info(IsLong
                    ? $"Breakout wick Close {c.Close} < refHigh {lvl}."
                    : $"Breakdown wick Close {c.Close} > refLow {lvl}.");

            if (IsLong ? c.Low <= cap : c.High >= cap)
                return GenerateSignal(c, cap);

            State = StrategyState.WaitingForEntry;
            return new StrategyResult
            {
                State = State,
                JustConfirmed = StrategyState.SecondBreakoutConfirmed,
                Message = "2nd breakout close-OK; pullback wait for cap."
            };
        }

        private StrategyResult HandleEntry(Candle c)
        {
            bool reached = IsLong ? c.Low <= EntryCapPrice : c.High >= EntryCapPrice;
            if (reached)
                return GenerateSignal(c, EntryCapPrice);
            return Info(IsLong
                ? $"Waiting entry: Low {c.Low} > cap {EntryCapPrice}."
                : $"Waiting entry: High {c.High} < cap {EntryCapPrice}.");
        }

        private StrategyResult GenerateSignal(Candle c, decimal entry)
        {
            decimal sl = IsLong
                ? _stopLossLevel - _config.StopLossBufferPoints
                : _stopLossLevel + _config.StopLossBufferPoints;
            decimal risk = IsLong ? entry - sl : sl - entry;
            if (risk <= 0)
                return Info($"Invalid risk (entry={entry}, SL={sl}). Skip.");

            decimal target = IsLong
                ? entry + risk * _config.RiskRewardRatio
                : entry - risk * _config.RiskRewardRatio;

            State = StrategyState.BuySignal;

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
                Message = $"{(IsLong ? "BUY" : "SELL")} @ {entry} | SL {sl} (ref extreme) | T {target} (1:{_config.RiskRewardRatio})"
            };
        }

        private StrategyResult Info(string msg) =>
            new StrategyResult { State = State, Message = msg };
    }
}
