using CSweet.Agents.ChiefOfStaff;
using CSweet.Agent.SDK;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

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
        var project = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "CSweet.Agents.ChiefOfStaff", "CSweet.Agents.ChiefOfStaff.csproj")));
        Assert.Contains($"<Version>{ChiefOfStaffProfile.Version}</Version>", project, StringComparison.Ordinal);
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
    public async Task ApprovedResourceChange_CreatesHiringSuggestionsAndBriefsChiefManager()
    {
        var organizationId = Guid.NewGuid();
        var chiefInstallationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var productManagerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var managerChatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var roles = new[]
        {
            Role("web3d", "Lead Web3D Developer", 1, 1) with
            {
                ReportsToOrganizationUserId = productManagerId
            },
            Role("quality", "QA / Playtester", 2, 1) with
            {
                ReportsToOrganizationUserId = productManagerId
            }
        };
        var request = new ResourceChangeRequestResponse(
            requestId,
            organizationId,
            productManagerId,
            Guid.NewGuid(),
            chiefId,
            Guid.NewGuid(),
            Guid.Empty,
            "Ship the first browser game",
            "The approved team covers product delivery and independent quality.",
            1,
            roles,
            roles.Select(role => new ResourceChangeRoleDelta("Add", role, null)).ToList(),
            [],
            [],
            null,
            "Approved",
            "Delivered",
            "Approved.",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var organization = new OrganizationSnapshotResponse(
            organizationId,
            "Active",
            [
                new OrganizationPerson(
                    chiefId, "C-Sweet Chief of Staff", "Agent", null, ownerId, chiefInstallationId, true),
                new OrganizationPerson(
                    ownerId, "Owner", "Human", null, null, null, true),
                new OrganizationPerson(
                    productManagerId, "C-Sweet Product Manager", "Agent", null, chiefId,
                    request.RequesterInstallationId, true)
            ],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow);
        var upserts = new List<UpsertHiringRecommendationRequest>();
        SendCommunicationMessageRequest? managerMessage = null;
        SuggestUserActionRequest? suggestedAction = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, OrganizationSnapshotResponse>(
                PlatformCapabilities.OrganizationSnapshotRead,
                (_, _) => Task.FromResult(organization))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(
                PlatformCapabilities.ResourceChangeRead,
                (_, _) => Task.FromResult(new ResourceChangeReadResponse([request])))
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([])))
            .RegisterCapability<UpsertHiringRecommendationRequest, HiringRecommendationResponse>(
                PlatformCapabilities.HiringRecommendationUpsert,
                (input, _) =>
                {
                    upserts.Add(input);
                    return Task.FromResult(new HiringRecommendationResponse(
                        Guid.NewGuid(),
                        input.WorkstreamId,
                        input.Title,
                        input.Objective,
                        "Suggested",
                        input.RecommendedCandidateReference,
                        [],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                    {
                        Priority = input.Priority,
                        RoleKey = input.RoleKey,
                        Headcount = input.Headcount,
                        SourceResourceChangeRequestId = input.SourceResourceChangeRequestId
                    });
                })
            .RegisterCapability<JsonElement, CommunicationHubResponse>(
                ChiefOfStaffProfile.ReadCommunicationCapability,
                (_, _) => Task.FromResult(new CommunicationHubResponse(
                    chiefId,
                    true,
                    [
                        new CommunicationChatResponse(
                            managerChatId,
                            string.Empty,
                            null,
                            true,
                            true,
                            true,
                            true,
                            DateTimeOffset.UtcNow,
                            [
                                new CommunicationParticipantResponse(
                                    chiefId, "C-Sweet Chief of Staff", "Agent", "Chief of Staff"),
                                new CommunicationParticipantResponse(ownerId, "Owner", "Human", "CEO")
                            ],
                            null,
                            null,
                            0)
                    ],
                    [],
                    [])))
            .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(
                ChiefOfStaffProfile.SendCommunicationMessageCapability,
                (input, _) =>
                {
                    managerMessage = input;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { id = messageId }));
                })
            .RegisterCapability<SuggestUserActionRequest, JsonElement>(
                ChiefOfStaffProfile.SuggestUserActionCapability,
                (input, _) =>
                {
                    suggestedAction = input;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            chiefInstallationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"),
                "C-Sweet Chief of Staff",
                null,
                "Chief of Staff",
                null,
                [],
                null,
                ownerId.ToString("D"),
                "Owner"));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

        await agent.HandleEventAsync(
            new AgentEventEnvelope(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ManagementEvents.ResourceChangeDecided,
                JsonSerializer.SerializeToElement(new ResourceChangeDecisionEvent(
                    requestId,
                    organizationId,
                    productManagerId,
                    chiefId,
                    "Approved",
                    DateTimeOffset.UtcNow)),
                DateTimeOffset.UtcNow),
            context,
            CancellationToken.None);

        Assert.Equal(2, upserts.Count);
        Assert.All(upserts, upsert =>
        {
            Assert.Empty(upsert.CandidateReferences);
            Assert.Null(upsert.RecommendedCandidateReference);
            Assert.Equal(requestId, upsert.SourceResourceChangeRequestId);
            Assert.StartsWith($"{productManagerId:N}:", upsert.RoleKey, StringComparison.Ordinal);
        });
        Assert.NotNull(managerMessage);
        Assert.Equal(managerChatId, managerMessage.ChatId);
        Assert.Contains("Lead Web3D Developer", managerMessage.Content, StringComparison.Ordinal);
        Assert.Contains("QA / Playtester", managerMessage.Content, StringComparison.Ordinal);
        Assert.Contains("candidate-free hiring suggestions", managerMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(suggestedAction);
        Assert.Equal(messageId, suggestedAction.MessageId);
        Assert.Equal("Lead Web3D Developer", suggestedAction.Parameters.GetProperty("role").GetString());
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
