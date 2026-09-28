using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Handlers;

/// <summary>
/// Handles one capability in <c>ChiefOfStaffAgent.ExecuteCapabilityCoreAsync</c>.
/// New capabilities are added by registering a handler, not by editing the agent (OCP).
/// </summary>
public interface ICapabilityHandler
{
    /// <summary>The capability name this handler serves.</summary>
    string Capability { get; }

    Task<AgentWorkResult> HandleAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken);
}
