using SharpBridge.State;

namespace SharpBridge.Infrastructure.Attributes;


[AttributeUsage(
    AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = true)]
public sealed class AllowedStateAttribute : Attribute
{
    public IReadOnlyCollection<SessionState> AllowedStates { get; }

    public AllowedStateAttribute(
        params SessionState[] states)
    {
        if (states == null || states.Length == 0)
        {
            throw new ArgumentException(
                "At least one session state is required.",
                nameof(states));
        }

        AllowedStates = states;
    }
}