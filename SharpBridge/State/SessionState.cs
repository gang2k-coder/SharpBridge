namespace SharpBridge.State;

public enum SessionState
{
    Detached,
    Attaching,
    Stopped,
    Running,
    Exited
};

public sealed class SessionStateMachine
{
    public SessionState Current { get; private set; }


    public SessionStateMachine()
    {
        Current = SessionState.Detached;
    }


    public void TransitionTo(SessionState newState)
    {
        if (!CanTransition(Current, newState))
        {
            throw new InvalidOperationException(
                $"{Current} -> {newState} is invalid");
        }

        Current = newState;
    }


    private static bool CanTransition(
        SessionState from,
        SessionState to)
    {
        return (from, to) switch
        {
            (SessionState.Detached,
             SessionState.Attaching) => true,

            (SessionState.Attaching,
             SessionState.Running) => true,

            (SessionState.Stopped,
             SessionState.Running) => true,

            (SessionState.Running,
             SessionState.Stopped) => true,

            (_, SessionState.Exited) => true,

            _ => false
        };
    }
}

