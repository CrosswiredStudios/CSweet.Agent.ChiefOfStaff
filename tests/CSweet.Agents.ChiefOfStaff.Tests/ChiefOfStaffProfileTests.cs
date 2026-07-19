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
        var context = new ChiefOperatingContext(null, null, organization, null, null, []);
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
