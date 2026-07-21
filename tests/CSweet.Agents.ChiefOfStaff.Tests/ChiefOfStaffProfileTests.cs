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
        Assert.Equal("assistant.converse.v1", ChiefOfStaffProfile.ConverseCapability);
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

        Assert.Contains(ManagementCapabilities.CheckIn, provides);
        Assert.Contains(AgentConfigurationCapabilities.Describe, provides);
        Assert.Contains(AgentConfigurationCapabilities.Update, provides);
        Assert.Contains(PlatformCapabilities.BusinessProfileRead, requires);
        Assert.Contains(PlatformCapabilities.WorkforceSearch, requires);
        Assert.Contains(PlatformCapabilities.BudgetEvaluate, requires);
        Assert.Contains(PlatformCapabilities.ManagementCycleRead, requires);
        Assert.DoesNotContain(PlatformCapabilities.UserInputRequest, requires);
        Assert.Contains(PlatformCapabilities.UserInputRequest, PlatformCapabilities.Global);
        Assert.Contains(PlatformCapabilities.HiringRecommendationUpsert, requires);
        Assert.Contains(PlatformCapabilities.HiringRecommendationList, requires);
        Assert.Contains(PlatformCapabilities.HiringWorkflowStage, requires);
        Assert.Contains("at most one high-value question", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("near 120 words", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not act as a subject-matter expert", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable personal to-do list", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-line role map", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only the highest-priority unfilled role", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never print, describe, or imitate a tool call", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask one concise plain-text question instead", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
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
}
