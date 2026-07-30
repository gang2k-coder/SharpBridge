namespace SharpBridge.State;

public enum SessionState
{
    Detached,
    Attaching,
    Stopped,
    Running,
    Exited
}

public sealed class SessionStateMachine
{
    private int _current = (int)SessionState.Detached;

    public SessionState Current => (SessionState)Volatile.Read(ref _current);

    public void TransitionTo(SessionState newState)
    {
        while (true)
        {
            var old = (SessionState)Volatile.Read(ref _current);
            if (!CanTransition(old, newState))
                throw new InvalidOperationException($"{old} -> {newState} is invalid");

            if (Interlocked.CompareExchange(ref _current, (int)newState, (int)old) == (int)old)
                return;
        }
    }

    private static bool CanTransition(SessionState from, SessionState to)
    {
        return (from, to) switch
        {
            (SessionState.Detached,    SessionState.Attaching) => true,
            (SessionState.Attaching,   SessionState.Running)   => true,
            (SessionState.Stopped,     SessionState.Running)   => true,
            (SessionState.Running,     SessionState.Stopped)   => true,
            (_,                        SessionState.Exited)    => true,
            _ => false
        };
    }
}
