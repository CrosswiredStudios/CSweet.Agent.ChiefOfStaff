using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Handlers;

/// <summary>
/// Handles one platform event type in <c>ChiefOfStaffAgent.HandleEventAsync</c>.
/// New event types are added by registering a handler, not by editing the agent (OCP).
/// </summary>
public interface IAgentEventHandler
{
    /// <summary>The event type this handler serves. Compared with ordinal string equality.</summary>
    string EventType { get; }

    /// <returns>True when the event was handled and dispatch should stop.</returns>
    Task<bool> HandleAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken);
}
