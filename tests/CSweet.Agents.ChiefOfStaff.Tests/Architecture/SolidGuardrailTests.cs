using CSweet.Agent.SDK;
using Microsoft.Extensions.Logging.Abstractions;

using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Formatting;
using CSweet.Agents.ChiefOfStaff.Handlers;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using CSweet.Agents.ChiefOfStaff.Policies;
using CSweet.Agents.ChiefOfStaff.Profiles;

namespace CSweet.Agents.ChiefOfStaff.Tests.Architecture;

/// <summary>
/// SOLID guardrails: the agent must depend on abstractions (DIP), expose
/// handler extension points (OCP), and keep formatting behind a single
/// substitutable service (SRP). These tests fail if a future change
/// reintroduces concrete coupling or removes the extension points.
/// </summary>
public sealed class SolidGuardrailTests
{
    [Fact]
    public void Agent_DependsOnOrchestratorAbstraction_NotConcrete()
    {
        var orchestratorParam = typeof(ChiefOfStaffAgent)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .FirstOrDefault(p => p.ParameterType == typeof(IChiefOfStaffOrchestrator));
        Assert.NotNull(orchestratorParam);

        Assert.DoesNotContain(
            typeof(ChiefOfStaffAgent).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(ChiefOfStaffOrchestrator));
    }

    [Fact]
    public void Orchestrator_ImplementsAbstraction()
    {
        Assert.Contains(typeof(IChiefOfStaffOrchestrator), typeof(ChiefOfStaffOrchestrator).GetInterfaces());
    }

    [Fact]
    public void Agent_ExposesCapabilityAndEventHandlerExtensionPoints()
    {
        Assert.Contains(
            typeof(ChiefOfStaffAgent).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(IReadOnlyList<ICapabilityHandler>));
        Assert.Contains(
            typeof(ChiefOfStaffAgent).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(IReadOnlyList<IAgentEventHandler>));
    }

    [Fact]
    public void DefaultPolicies_PreserveHistoricalBehavior()
    {
        IGameStudioOwnershipPolicy policy = new DefaultGameStudioOwnershipPolicy();
        Assert.False(policy.IsGameProducer(new HiringRecommendationResponse(
            Guid.NewGuid(), null, "Product Manager", "Own product outcomes.", "Suggested", null,
            [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        IManagerSelector<ProjectManagerCandidate> selector = new LowestIdManagerSelector();
        Assert.Null(selector.Select([]));
        var first = new ProjectManagerCandidate(Guid.NewGuid(), "B", null);
        var second = new ProjectManagerCandidate(Guid.NewGuid(), "A", null);
        var expected = new[] { first, second }.OrderBy(x => x.Id).First();
        Assert.Equal(expected, selector.Select([first, second]));
    }

    [Fact]
    public void OperatingProfileRegistry_ReportsUnknownKeysExplicitly()
    {
        IOperatingProfileRegistry registry = new DefaultOperatingProfileRegistry();
        var known = registry.ResolveExplicit(BusinessOperatingProfiles.GeneralKey);
        Assert.True(known.Found);

        var unknown = registry.ResolveExplicit("does-not-exist");
        Assert.False(unknown.Found);
        Assert.Equal(BusinessOperatingProfiles.GeneralKey, unknown.Profile.Key);
    }

    [Fact]
    public void FormatterService_MatchesAgentStaticBehavior()
    {
        IResponsePolicyFormatter formatter = new ResponsePolicyFormatter();
        Assert.Equal(
            ChiefOfStaffAgent.NormalizeRoleIdentity("Game Producer (Agent)"),
            formatter.NormalizeRoleIdentity("Game Producer (Agent)"));
        Assert.Equal(
            ChiefOfStaffAgent.EnforceResponseMode("Priority 1 Hire: Product Manager"),
            formatter.EnforceResponseMode("Priority 1 Hire: Product Manager"));
    }

    [Fact]
    public void Agent_CanBeConstructedWithSubstitutedOrchestrator()
    {
        var agent = new ChiefOfStaffAgent(NullLogger<ChiefOfStaffAgent>.Instance, new StubOrchestrator());
        Assert.NotNull(agent);
    }

    private sealed class StubOrchestrator : IChiefOfStaffOrchestrator
    {
        public Task<ChiefOperatingContext> AssembleContextAsync(AgentRuntimeContext runtimeContext, CancellationToken cancellationToken) =>
            Task.FromResult(new ChiefOperatingContext(null, null, null, null, null, null, []));

        public string BuildGroundedPrompt(string userPrompt, string capability, ChiefOperatingContext context, AgentSettings settings) =>
            userPrompt;

        public Task CaptureExplicitFactsAsync(
            Microsoft.Extensions.AI.IChatClient chatClient,
            AssistantCapabilityInput input,
            ChiefOperatingContext context,
            AgentRuntimeContext runtimeContext,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
