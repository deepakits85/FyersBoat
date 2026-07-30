namespace FyersLoginWeb.Strategy
{
    /// <summary>
    /// State machine ke states (spec ke exact naam).
    ///
    /// WAITING_FOR_FIRST_BREAKOUT
    ///   ↓ (3m High > 30m High)  = FIRST_BREAKOUT_CONFIRMED
    /// WAITING_FOR_RETRACEMENT
    ///   ↓ (3m Low &lt;= 50% level) = RETRACEMENT_CONFIRMED
    /// WAITING_FOR_SECOND_BREAKOUT
    ///   ↓ (3m High > 30m High again)
    /// BUY_SIGNAL
    ///
    /// Machine persistent states me rukta hai: WaitingForFirstBreakout,
    /// WaitingForRetracement, WaitingForSecondBreakout, BuySignal.
    /// FirstBreakoutConfirmed / RetracementConfirmed sirf transition ke waqt
    /// "abhi kya confirm hua" batane ke liye report hote hain
    /// (StrategyResult.JustConfirmed).
    /// </summary>
    public enum StrategyState
    {
        WaitingForFirstBreakout,
        FirstBreakoutConfirmed,
        WaitingForRetracement,
        RetracementConfirmed,
        WaitingForSecondBreakout,
        SecondBreakoutConfirmed,
        WaitingForEntry,
        BuySignal
    }
}
