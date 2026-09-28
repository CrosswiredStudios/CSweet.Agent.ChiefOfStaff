using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using CSweet.Agents.ChiefOfStaff.Contracts;

namespace CSweet.Agents.ChiefOfStaff.Orchestration;

/// <summary>
/// Orchestrates context assembly, prompt grounding, and report building for the Chief of Staff.
/// Extracted as an interface so <see cref="ChiefOfStaffAgent"/> depends on an abstraction (DIP),
/// and alternative orchestrators can be substituted without modifying the agent (OCP/LSP).
/// </summary>
public interface IChiefOfStaffOrchestrator
{
    Task<ChiefOperatingContext> AssembleContextAsync(
        AgentRuntimeContext runtimeContext,
        CancellationToken cancellationToken);

    string BuildGroundedPrompt(string userPrompt, string capability, ChiefOperatingContext context, AgentSettings settings);

    Task CaptureExplicitFactsAsync(
        IChatClient chatClient,
        AssistantCapabilityInput input,
        ChiefOperatingContext context,
        AgentRuntimeContext runtimeContext,
        CancellationToken cancellationToken);
}
