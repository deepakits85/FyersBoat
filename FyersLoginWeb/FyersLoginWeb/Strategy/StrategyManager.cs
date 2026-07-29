using System;

namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// Poora flow tie karta hai:
    ///   - 30m candle complete/change  -> reference set (par cycle ke beech LOCK)
    ///   - 3m candle complete          -> strategy process
    ///   - BUY signal aane par          -> OnBuySignal callback fire
    ///
    /// AHEM RULE (fix): jab ek cycle chal rahi ho (first breakout ke baad, yaani
    /// WaitingForRetracement / WaitingForSecondBreakout), tab naya 30m candle aane
    /// par bhi reference LOCK rehta hai — cycle usi reference par complete hoti hai.
    /// Reference sirf tab badalta hai jab:
    ///   * abhi koi cycle active nahi (WaitingForFirstBreakout), ya
    ///   * BUY ho chuka hai (ab agla reference lo).
    /// Isse "first breakout -> retracement -> second breakout" 30-min boundary ke
    /// paar bhi sahi complete hota hai.
    ///
    /// Ye class hi baad me FYERS se connect hogi (OnBuySignal me order execution).
    /// </summary>
    public class StrategyManager
    {
        private readonly BreakoutRetracementStrategy _strategy;
        private ReferenceCandle? _latestReference; // ab tak dekha sabse naya 30m

        /// <summary>Signal (BUY/SELL) generate hone par fire hota hai (order execution yahan hook karo).</summary>
        public event Action<TradeSignal>? OnSignal;

        /// <summary>Kisi bhi confirmation par fire (logging ke liye optional).</summary>
        public event Action<StrategyResult>? OnUpdate;

        public StrategyManager(StrategyConfig? config = null, TradeSide side = TradeSide.Long)
        {
            _strategy = new BreakoutRetracementStrategy(config, side);
        }

        public StrategyState State => _strategy.State;
        public ReferenceCandle? CurrentReference => _strategy.Reference;
        public TradeSide Side => _strategy.Side;

        // Live real-time entry: next entry level + SL base (retracement)
        public decimal EntryCapLevel => _strategy.EntryCapPrice;
        public decimal RetracementLevel => _strategy.RetracementLevel;

        // Cycle active = first breach ho chuka par abhi signal nahi (reference lock ke liye)
        private bool IsActiveCycle =>
            _strategy.State == StrategyState.WaitingForRetracement ||
            _strategy.State == StrategyState.WaitingForSecondBreakout ||
            _strategy.State == StrategyState.WaitingForEntry;

        // Setup abhi confirm nahi hua (retracement/2nd breach ka wait) — yahi par
        // "itna wait nahi karna" wala abandon lagta hai. Entry-pullback ke wait me nahi
        // (tab setup confirm ho chuka, sirf achhi price ka intezaar).
        private bool IsWaitingForSetup =>
            _strategy.State == StrategyState.WaitingForRetracement ||
            _strategy.State == StrategyState.WaitingForSecondBreakout;

        /// <summary>
        /// Jab ek 30-min candle complete ho ye call karo. Locking rule ke hisaab se
        /// reference apply hota hai ya store rehta hai (baad me use hone ke liye).
        /// </summary>
        public void OnThirtyMinuteCandleClosed(Candle thirtyMin, decimal? priorHigh = null, decimal? priorLow = null)
        {
            var reference = ReferenceCandle.FromCandle(thirtyMin, priorHigh, priorLow);

            // sabse naya reference yaad rakho
            if (_latestReference == null || reference.StartTime > _latestReference.StartTime)
                _latestReference = reference;

            // pehla reference
            if (_strategy.Reference == null)
            {
                _strategy.SetReference(reference);
                return;
            }

            // cycle active nahi -> naye (strictly newer) reference par re-arm (reset)
            if (!IsActiveCycle && reference.StartTime > _strategy.Reference.StartTime)
                _strategy.SetReference(reference);
        }

        /// <summary>
        /// Jab ek CLOSED 3-min candle aaye ye call karo. Result return + events fire.
        /// </summary>
        public StrategyResult OnThreeMinuteCandleClosed(Candle c)
        {
            var result = _strategy.ProcessThreeMinuteCandle(c);

            if (result.JustConfirmed != null)
                OnUpdate?.Invoke(result);

            // signal (BUY/SELL) ban gaya -> fire + agla reference adopt
            if (result.Signal != null)
            {
                OnSignal?.Invoke(result.Signal);
                if (HasNewerReference())
                    _strategy.SetReference(_latestReference!);
                return result;
            }

            // "itna wait nahi karna": agar cycle active hai par is candle par kuch
            // progress nahi hua (JustConfirmed null), AUR ek NEWER reference par isi
            // candle se fresh breach ho raha hai, to purani cycle CHHOD do aur newer
            // reference par shift ho jao. (Jis candle par purani cycle complete hoti
            // hai wahan signal upar hi return ho jata hai -> purani ko preference.)
            if (result.JustConfirmed == null && IsWaitingForSetup && HasNewerReference())
            {
                bool newerBreach = _strategy.Side == TradeSide.Long
                    ? c.High > _latestReference!.High
                    : c.Low < _latestReference!.Low;

                if (newerBreach)
                {
                    _strategy.SetReference(_latestReference!);           // purani chhodo, newer lo
                    var r2 = _strategy.ProcessThreeMinuteCandle(c);      // yahi candle = newer ka first breach
                    if (r2.JustConfirmed != null)
                        OnUpdate?.Invoke(r2);
                    return r2;
                }
            }

            return result;
        }

        private bool HasNewerReference() =>
            _latestReference != null && _strategy.Reference != null &&
            _latestReference.StartTime > _strategy.Reference.StartTime;
    }
}
