using CSweet.Agents.ChiefOfStaff;
using CSweet.Agent.SDK;
using System.Text.Json;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ChiefOfStaffProfileTests
{
    [Fact]
    public void Profile_UsesThirdPartyIdentityAndCompatibleConversationContract()
    {
        Assert.Equal("com.csweet.chief-of-staff", ChiefOfStaffProfile.AgentId);
        Assert.Equal(AssistantCapabilities.Converse, ChiefOfStaffProfile.ConverseCapability);
        Assert.Equal("com.csweet.user.message.received.v1", ChiefOfStaffProfile.UserMessageReceivedEvent);
        Assert.Equal("com.csweet.assistant.response.chunk.v1", ChiefOfStaffProfile.AssistantResponseChunkEvent);
    }

    [Fact]
    public void RootManifest_UsesImporterCompatibleActivationMode()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "csweet-plugin.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        Assert.Equal(
            "AlwaysOn",
            manifest.RootElement
                .GetProperty("runtime")
                .GetProperty("defaultActivationMode")
                .GetString());
    }

    [Fact]
    public void RootManifest_VersionMatchesImplementationVersion()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "csweet-plugin.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        Assert.Equal(
            ChiefOfStaffProfile.Version,
            manifest.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void RootManifest_DeclaresManagementAndAuthoritativePlatformContracts()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "csweet-plugin.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var provides = manifest.RootElement.GetProperty("provides").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToList();
        var requires = manifest.RootElement.GetProperty("requires").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToList();
        var subscriptions = manifest.RootElement.GetProperty("events").GetProperty("subscribes")
            .EnumerateArray().Select(x => x.GetString()).ToList();

        var extensionCapabilities = new[]
        {
            ChiefOfStaffProfile.ResolveHiringRecommendationCapability,
            ChiefOfStaffProfile.SuggestUserActionCapability
        };
        Assert.All(provides.Concat(requires).Except(extensionCapabilities), capability =>
            Assert.Contains(capability!, CapabilityCatalog.All));
        Assert.Contains(ManagementCapabilities.CheckIn, provides);
        Assert.Contains(AgentConfigurationCapabilities.Describe, provides);
        Assert.Contains(AgentConfigurationCapabilities.Update, provides);
        Assert.Contains(PlatformCapabilities.BusinessProfileRead, requires);
        Assert.Contains(PlatformCapabilities.WorkforceSearch, requires);
        Assert.Contains(AgentCatalogCapabilities.Search, requires);
        Assert.Contains(PlatformCapabilities.BudgetEvaluate, requires);
        Assert.Contains(PlatformCapabilities.ManagementCycleRead, requires);
        Assert.Contains(PlatformCapabilities.UserInputRequest, requires);
        Assert.Contains(PlatformCapabilities.HiringRecommendationUpsert, requires);
        Assert.Contains(PlatformCapabilities.HiringRecommendationList, requires);
        Assert.Contains(ChiefOfStaffProfile.ResolveHiringRecommendationCapability, requires);
        Assert.Contains(ChiefOfStaffProfile.SuggestUserActionCapability, requires);
        Assert.DoesNotContain(PlatformCapabilities.HiringWorkflowStage, requires);
        Assert.Contains(ChiefOfStaffProfile.EmployeeHiredEvent, subscriptions);
        Assert.Contains(ProductManagementCapabilities.RoleBrief, provides);
        Assert.Contains(ProductManagementCapabilities.PlanReview, provides);
        Assert.Contains(ProductManagementCapabilities.Escalation, provides);
        Assert.Contains(ProductManagementCapabilities.Plan, requires);
        Assert.Contains(ProductManagementCapabilities.ContextUpdate, requires);
        Assert.Contains(ChiefOfStaffProfile.ReadCommunicationCapability, requires);
        Assert.Contains(AgentLifecycleCapabilities.CompleteOnboarding, requires);
        Assert.Contains("at most one high-value question", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("near 120 words", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not act as a subject-matter expert", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable personal to-do list", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-line role map", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only the highest-priority unfilled role", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never print, describe, or imitate a tool call", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask one concise plain-text question instead", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Consult an active Product Manager", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Product Manager as the default priority-one hire", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Marketplace owns candidate discovery", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("suggest_user_action", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one combined brief", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("highest-priority new or increased role", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoveryPolicy_AsksOnlyTheHighestValueMissingQuestion()
    {
        var profile = new BusinessProfileResponse(
            Guid.NewGuid(), "Example", "SaaS", "Software", null, null, "Validation",
            [], [], null, [], null, [], [], null, "UTC", 1, 0.2m,
            new Dictionary<string, ProfileFieldProvenance>());

        var question = ChiefOfStaffOrchestrator.HighestValueDiscoveryQuestion(profile, null);

        Assert.Equal("Who is the first specific customer you intend to serve?", question);
        Assert.Equal("Growing", ChiefOfStaffOrchestrator.NormalizeStage("growth"));
    }

    [Fact]
    public void ContextualOnboardingFallback_UsesKnownBusinessFactsAndOneMissingFact()
    {
        var profile = new BusinessProfileResponse(
            Guid.NewGuid(),
            "Trailwise",
            "Marketplace",
            "Outdoor recreation",
            null,
            "Make expert-led outdoor experiences accessible.",
            "Validation",
            [],
            ["Guided trip bookings"],
            "Booking commission",
            ["United States"],
            null,
            [],
            [],
            null,
            "UTC",
            1,
            0.6m,
            new Dictionary<string, ProfileFieldProvenance>());
        var context = new ChiefOperatingContext(profile, null, null, null, null, null, []);

        var message = ChiefOfStaffOrchestrator.BuildContextualOnboardingFallback(context);

        Assert.Contains("Trailwise", message);
        Assert.Contains("Outdoor recreation", message);
        Assert.Contains("Make expert-led outdoor experiences accessible", message);
        Assert.Contains("who is the first specific customer", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("what you're building", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductDrivenBusiness_DefaultsToProductManagerAndEmbeddedCandidateSearch()
    {
        var organizationId = Guid.NewGuid();
        var profile = new BusinessProfileResponse(
            organizationId,
            "Trailwise",
            "Marketplace",
            "Outdoor recreation",
            "A software marketplace for guided outdoor trips.",
            "Make expert-led outdoor experiences accessible.",
            "Validation",
            ["New outdoor enthusiasts"],
            ["Guided trip bookings"],
            "Booking commission",
            ["United States"],
            null,
            [],
            [],
            "Moderate",
            "America/Los_Angeles",
            1,
            1m,
            new Dictionary<string, ProfileFieldProvenance>());
        var finance = new FinancialOperatingProfileResponse(
            organizationId,
            "USD",
            100_000m,
            null,
            null,
            null,
            10_000m,
            null,
            1,
            "DigitalFirst",
            1);
        var context = new ChiefOperatingContext(profile, finance, null, null, null, null, []);
        var orchestrator = new ChiefOfStaffOrchestrator(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChiefOfStaffOrchestrator>.Instance);

        var prompt = orchestrator.BuildGroundedPrompt(
            "Who should I hire first?",
            ChiefOfStaffProfile.ConverseCapability,
            context,
            new AgentSettings(new Dictionary<string, JsonElement>()));
        var fallback = ChiefOfStaffOrchestrator.BuildContextualOnboardingFallback(context);

        Assert.True(ChiefOfStaffOrchestrator.IsProductDrivenBusiness(profile));
        Assert.Contains("Product Manager as the priority-one hire", prompt);
        Assert.Contains("Product Manager as the priority-one hire", fallback);
        Assert.Contains("Browse Marketplace candidates", fallback);
    }

    [Theory]
    [InlineData("Product Manager", "productmanager")]
    [InlineData("Product Manager (Agent)", "productmanager")]
    [InlineData(" product-manager ", "productmanager")]
    public void HiredRoleIdentity_IsNormalizedDeterministically(string value, string expected)
    {
        Assert.Equal(expected, ChiefOfStaffAgent.NormalizeRoleIdentity(value));
    }

    [Fact]
    public void ResourceChangeManagerBrief_ListsEveryDeltaInPriorityOrder()
    {
        var request = ResourceChange(
            new ResourceChangeRoleDelta(
                "Increase",
                Role("quality", "QA / Playtester", 3, 2),
                Role("quality", "QA / Playtester", 3, 1)),
            new ResourceChangeRoleDelta(
                "Add",
                Role("web3d", "Lead Web3D Developer", 1, 1),
                null),
            new ResourceChangeRoleDelta(
                "Remove",
                Role("legacy", "Legacy Generalist", 4, 1),
                Role("legacy", "Legacy Generalist", 4, 1)),
            new ResourceChangeRoleDelta(
                "Modify",
                Role("design", "Game Designer", 2, 1),
                Role("design", "Game Designer", 2, 1)));

        var brief = ChiefOfStaffAgent.BuildResourceChangeManagerBrief(request);

        var lead = brief.IndexOf("Lead Web3D Developer", StringComparison.Ordinal);
        var designer = brief.IndexOf("Game Designer", StringComparison.Ordinal);
        var quality = brief.IndexOf("QA / Playtester", StringComparison.Ordinal);
        var removed = brief.IndexOf("Legacy Generalist", StringComparison.Ordinal);
        Assert.True(lead < designer && designer < quality && quality < removed);
        Assert.Contains("**Add: Lead Web3D Developer**", brief);
        Assert.Contains("**Increase: QA / Playtester**", brief);
        Assert.Contains("**Modify: Game Designer**", brief);
        Assert.Contains("**Remove: Legacy Generalist**", brief);
        Assert.Contains("candidate-free hiring suggestions", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagementReport_ProducesPrioritizedConciseMarkdown()
    {
        var organization = new OrganizationSnapshotResponse(
            Guid.NewGuid(), "Active", [], [], [],
            [new WorkstreamSummary(Guid.NewGuid(), "Launch", "Ship the release", "Blocked", "Launch", null,
                DateTimeOffset.UtcNow.AddDays(-1), null, null)],
            [], DateTimeOffset.UtcNow)
        {
            OperatingSignals =
            [
                new OperatingSignal("Blocker", "Critical", "Resolve the production deployment blocker."),
                new OperatingSignal("Approval", "High", "Approve the launch rollback policy."),
                new OperatingSignal("Risk", "Medium", "Monitor support capacity after launch.")
            ]
        };
        var context = new ChiefOperatingContext(null, null, organization, null, null, null, []);
        var requestId = Guid.NewGuid();
        var request = new ManagementCheckInRequest(Guid.NewGuid(), "ExecutiveBriefing", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow, [], [], DateTimeOffset.UtcNow.AddHours(2)) { RequestId = requestId };

        var report = ChiefOfStaffOrchestrator.BuildManagementReport(request, context);

        Assert.Equal(requestId, report.RequestId);
        Assert.Equal("Urgent", report.Severity);
        Assert.Contains("## Work on now", report.Markdown);
        Assert.Contains("Resolve the production deployment blocker", report.Markdown);
        Assert.Contains("Approve the launch rollback policy", report.Markdown);
        Assert.True(report.ImmediateActions.Count <= 5);
        Assert.True(report.ConversationTopics.Count <= 3);
    }

    private static ResourceChangeRole Role(string key, string title, int priority, int headcount) =>
        new(
            key,
            "Product",
            title,
            $"Own the {title} outcome.",
            headcount,
            priority,
            "Now",
            [$"{key}.capability"],
            false,
            Guid.NewGuid(),
            null);

    private static ResourceChangeRequestResponse ResourceChange(params ResourceChangeRoleDelta[] deltas)
    {
        var organizationId = Guid.NewGuid();
        return new ResourceChangeRequestResponse(
            Guid.NewGuid(),
            organizationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Ship the first browser game",
            "The approved team covers product delivery.",
            1,
            deltas.Where(x => x.ChangeKind != "Remove").Select(x => x.Role).ToList(),
            deltas,
            [],
            [],
            null,
            "Approved",
            "Delivered",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
