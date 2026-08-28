using CSweet.Agents.ChiefOfStaff;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ChiefOfStaffProfileTests
{
    [Fact]
    public void Agent_ReconcilesHiringTodosWhenItsSdkRuntimeActivates()
    {
        Assert.Contains(
            typeof(IAgentActivationHandler),
            typeof(ChiefOfStaffAgent).GetInterfaces());
    }

    [Fact]
    public void RuntimeOwnedHiringToolsAreNeverExposedToTheModel()
    {
        var input = new AssistantCapabilityInput(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            "Prepare the onboarding message.",
            null);

        Assert.False(ChiefOfStaffAgent.IsModelToolAvailable(input, "suggest_user_action"));
        Assert.False(ChiefOfStaffAgent.IsModelToolAvailable(
            input with { MessageId = Guid.NewGuid() },
            "suggest_user_action"));
        Assert.False(ChiefOfStaffAgent.IsModelToolAvailable(
            input with { ChatTurnId = Guid.NewGuid() },
            "suggest_user_action"));
        Assert.False(ChiefOfStaffAgent.IsModelToolAvailable(input, "add_personal_todo"));
        Assert.True(ChiefOfStaffAgent.IsModelToolAvailable(input, "organization_read"));
    }

    [Fact]
    public async Task OptionalOnboardingActionFailureDoesNotFailTheGreeting()
    {
        var recommendation = new HiringRecommendationResponse(
            Guid.NewGuid(),
            null,
            "Product Manager",
            "Own product outcomes.",
            "Suggested",
            null,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            Priority = 1
        };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([recommendation])))
            .RegisterCapability<SuggestUserActionRequest, SuggestedUserActionResponse>(
                PlatformCapabilities.UserActionSuggest,
                (_, _) => Task.FromException<SuggestedUserActionResponse>(
                    new PlatformCapabilityException(
                        PlatformCapabilities.UserActionSuggest,
                        PlatformCapabilityErrorCode.ValidationFailed,
                        "The optional action was rejected.")));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

        await agent.AttachTopHiringActionAsync(
            Guid.NewGuid(),
            "agent-onboarded:test",
            runtime.CreateContext(),
            CancellationToken.None);
    }

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
    public async Task RootManifest_PassesSdkValidation()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "csweet-plugin.json"));

        var manifest = await AgentManifestLoader.LoadAsync(manifestPath, CancellationToken.None);

        Assert.Equal(ChiefOfStaffProfile.AgentId, manifest.Id);
        Assert.Contains(WorkforceEvents.Changed, manifest.Events.Subscribes);
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

        Assert.All(provides.Concat(requires), capability =>
            Assert.Contains(capability!, CapabilityCatalog.All));
        Assert.Contains(ManagementCapabilities.CheckIn, provides);
        Assert.Contains(AgentConfigurationCapabilities.Describe, provides);
        Assert.Contains(AgentConfigurationCapabilities.Update, provides);
        var configurationKeys = manifest.RootElement.GetProperty("configuration").EnumerateArray()
            .Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Equal([
            "llmProviderId", "llmModel", "businessOperatingProfile", "customBusinessDescription"
        ], configurationKeys);
        var customDescription = manifest.RootElement.GetProperty("configuration").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "customBusinessDescription");
        Assert.True(customDescription.GetProperty("required").GetBoolean());
        Assert.Equal("businessOperatingProfile", customDescription.GetProperty("visibleWhenFieldKey").GetString());
        Assert.Equal("custom", customDescription.GetProperty("visibleWhenValue").GetString());
        Assert.Contains(PlatformCapabilities.BusinessProfileRead, requires);
        Assert.Contains(PlatformCapabilities.WorkforceSearch, requires);
        Assert.Contains(AgentCatalogCapabilities.Search, requires);
        Assert.Contains(PlatformCapabilities.BudgetEvaluate, requires);
        Assert.Contains(PlatformCapabilities.ManagementCycleRead, requires);
        Assert.Contains(PlatformCapabilities.UserInputRequest, requires);
        Assert.Contains(PlatformCapabilities.HiringRecommendationUpsert, requires);
        Assert.Contains(PlatformCapabilities.HiringRecommendationList, requires);
        Assert.Contains(PersonalTodoCapabilities.Activate, requires);
        Assert.Contains(ChiefOfStaffProfile.SuggestUserActionCapability, requires);
        Assert.DoesNotContain(PlatformCapabilities.HiringWorkflowStage, requires);
        Assert.Contains(ChiefOfStaffProfile.RecommendationFulfilledEvent, subscriptions);
        Assert.Contains(ProductManagementCapabilities.RoleBrief, provides);
        Assert.Contains(ProductManagementCapabilities.PlanReview, provides);
        Assert.Contains(ProductManagementCapabilities.Escalation, provides);
        Assert.Contains(ProductManagementCapabilities.Plan, requires);
        Assert.Contains(ProductManagementCapabilities.ContextUpdate, requires);
        Assert.Contains(ChiefOfStaffProfile.ReadCommunicationCapability, requires);
        Assert.Contains(AgentLifecycleCapabilities.CompleteOnboarding, requires);
        Assert.Contains("single blocking question", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("near 120 words", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not act as a subject-matter expert", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable personal to-do list", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CEO-direct managerial shape", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only the highest-priority unfilled role", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never print, describe, or imitate a tool call", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask one concise plain-text question instead", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Consult an active Product Manager", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Originate hiring recommendations only for accountable managers who report directly to the CEO", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("defer the recommendation to the appropriate product or functional lead", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clearly overrides this boundary", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("These suggestions remain the lead's recommendations", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not assume a Product Manager", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native bounded-choice widget", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one proactive hiring recommendation at a time", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Marketplace owns candidate discovery", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deterministic Chief runtime mirrors", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not call `add_personal_todo`", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not call it from model responses", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is not a blocked condition", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("suggest_user_action", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one combined brief", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once per new or increased role", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never ask a question in the same response that makes a recommendation", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not ask for information merely because a profile field is incomplete", ChiefOfStaffProfile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductManagerLiaison_RequiresAnActiveAgentSharingTheHumanCeo()
    {
        var ceoId = Guid.NewGuid();
        var otherCeoId = Guid.NewGuid();
        var chief = new OrganizationPerson(
            Guid.NewGuid(), "Chief of Staff", "Agent", null, ceoId, Guid.NewGuid(), true);
        var peerProductManager = new OrganizationPerson(
            Guid.NewGuid(), "Product Manager", "Agent", null, ceoId, Guid.NewGuid(), true);
        var unrelatedProductManager = new OrganizationPerson(
            Guid.NewGuid(), "Product Manager West", "Agent", null, otherCeoId, Guid.NewGuid(), true);
        var organization = new OrganizationSnapshotResponse(
            Guid.NewGuid(),
            "Active",
            [
                chief,
                peerProductManager,
                unrelatedProductManager,
                new OrganizationPerson(ceoId, "CEO", "Human", null, null, null, true),
                new OrganizationPerson(otherCeoId, "Other CEO", "Human", null, null, null, true)
            ],
            [], [], [], [], DateTimeOffset.UtcNow);

        Assert.True(ChiefOfStaffAgent.IsProductManagerLiaison(
            chief, peerProductManager, organization));
        Assert.False(ChiefOfStaffAgent.IsProductManagerLiaison(
            chief, unrelatedProductManager, organization));
        Assert.False(ChiefOfStaffAgent.IsProductManagerLiaison(
            chief, peerProductManager with { EmployeeType = "Human" }, organization));
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
    public void ContextualOnboardingFallback_RecommendsFirstHireWithoutAppendingDiscoveryQuestion()
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
        Assert.Contains("leadership coverage", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("who is the first specific customer", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", message, StringComparison.Ordinal);
        Assert.DoesNotContain("what you're building", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroundedPrompt_DoesNotInjectRoutineBusinessDiscoveryQuestion()
    {
        var profile = new BusinessProfileResponse(
            Guid.NewGuid(), "Example", "SaaS", "Software", null, null, "Validation",
            [], [], null, [], null, [], [], null, "UTC", 1, 0.2m,
            new Dictionary<string, ProfileFieldProvenance>());
        var context = new ChiefOperatingContext(profile, null, null, null, null, null, []);
        var orchestrator = new ChiefOfStaffOrchestrator(
            NullLogger<ChiefOfStaffOrchestrator>.Instance);

        var prompt = orchestrator.BuildGroundedPrompt(
            "Who should I hire first?",
            ChiefOfStaffProfile.ConverseCapability,
            context,
            new AgentSettings(new Dictionary<string, JsonElement>()));

        Assert.DoesNotContain(
            "Who is the first specific customer you intend to serve?",
            prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Never ask and recommend or suggest an action in the same response",
            prompt,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnboardingFormatter_RemovesQuestionWhenResponseAlsoRecommends()
    {
        const string response = """
Role Map: Product Management, Engineering, Marketing
Priority 1 Hire: Product Manager
I have added this to your hiring backlog.
Who is the first specific customer you intend to serve?
""";

        var formatted = ChiefOfStaffAgent.FormatOnboardingMessage(response);

        Assert.Contains("Product Manager", formatted);
        Assert.Contains("hiring backlog", formatted);
        Assert.DoesNotContain("Question for you", formatted);
        Assert.DoesNotContain("Who is the first specific customer", formatted);
    }

    [Fact]
    public void OnboardingFormatter_PreservesQuestionWhenNoRecommendationIsMade()
    {
        const string response = """
I've reviewed the available profile, but it does not identify what kind of business this is.
What type of business are you building?
""";

        var formatted = ChiefOfStaffAgent.FormatOnboardingMessage(response);

        Assert.Contains("Question for you", formatted);
        Assert.Contains("What type of business are you building?", formatted);
    }

    [Fact]
    public void ResponseModeGuard_RemovesInlineQuestionAfterRecommendation()
    {
        const string response =
            "The highest priority is to hire a Product Manager. Who is the first specific customer you intend to serve?";

        var validated = ChiefOfStaffAgent.EnforceResponseMode(response);

        Assert.Equal("The highest priority is to hire a Product Manager.", validated);
    }

    [Fact]
    public void ResponseModeGuard_DoesNotRemoveQuestionOnlyClarification()
    {
        const string response =
            "I cannot choose the first role responsibly from the available profile. What type of business are you building?";

        var validated = ChiefOfStaffAgent.EnforceResponseMode(response);

        Assert.Equal(response, validated);
    }

    [Fact]
    public void ResponseModeGuard_DoesNotMistakeBlockedRecommendationForSuggestion()
    {
        const string response =
            "Before I can recommend a first hire, I need to know what kind of business this is. What type of business are you building?";

        var validated = ChiefOfStaffAgent.EnforceResponseMode(response);

        Assert.Equal(response, validated);
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
        Assert.Contains("Business operating profile: General", prompt);
        Assert.DoesNotContain("Product Manager as the priority-one hire", prompt);
        Assert.DoesNotContain("Product Manager as the priority-one hire", fallback);
        Assert.DoesNotContain("Browse Marketplace candidates", fallback);
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
        Assert.Contains(request.RequesterOrganizationUserId.ToString("D"), brief, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(request.ManagerOrganizationUserId.ToString("D"), brief, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("administered by the Chief", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CeoApprovedProductManagerResourceChange_CreatesLeadAuthoredSuggestionsAndBriefsCeo()
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
            ownerId,
            Guid.NewGuid(),
            Guid.Empty,
            "Ship the first browser game",
            "The approved team covers product delivery and independent quality.",
            1,
            roles,
            [
                new ResourceChangeRoleDelta("Add", roles[0], null),
                new ResourceChangeRoleDelta("Increase", roles[1] with { Headcount = 3 }, roles[1])
            ],
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
                    chiefId, "Sherly", "Agent", null, ownerId, chiefInstallationId, true),
                new OrganizationPerson(
                    ownerId, "Owner", "Human", null, null, null, true),
                new OrganizationPerson(
                    productManagerId, "C-Sweet Product Manager", "Agent", null, ownerId,
                    request.RequesterInstallationId, true)
            ],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow);
        var upserts = new List<UpsertHiringRecommendationRequest>();
        var recommendations = new List<HiringRecommendationResponse>();
        var hiringTodos = new List<AddPersonalTodoItemRequest>();
        SendCommunicationMessageRequest? managerMessage = null;
        var suggestedActions = new List<SuggestUserActionRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, OrganizationSnapshotResponse>(
                PlatformCapabilities.OrganizationSnapshotRead,
                (_, _) => Task.FromResult(organization))
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(
                PlatformCapabilities.ResourceChangeRead,
                (_, _) => Task.FromResult(new ResourceChangeReadResponse([request])))
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse(recommendations)))
            .RegisterCapability<UpsertHiringRecommendationRequest, HiringRecommendationResponse>(
                PlatformCapabilities.HiringRecommendationUpsert,
                (input, _) =>
                {
                    upserts.Add(input);
                    var recommendation = new HiringRecommendationResponse(
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
                    };
                    recommendations.Add(recommendation);
                    return Task.FromResult(recommendation);
                })
            .RegisterCapability<object, PersonalTodoDirectory>(
                PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(Guid.NewGuid(), chiefId, "Sherly", ownerId, "Owner", 1, [])],
                    chiefId)))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Add,
                (input, _) =>
                {
                    hiringTodos.Add(input);
                    return Task.FromResult(PersonalTodo(input, chiefId));
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
                    suggestedActions.Add(input);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            chiefInstallationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"),
                "Sherly",
                null,
                null,
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
            Assert.Contains(upsert.RoleKey, roles.Select(role => role.RoleKey));
            Assert.StartsWith($"resource-change:{requestId:N}:role:", upsert.IdempotencyKey, StringComparison.Ordinal);
        });
        Assert.Equal([1, 2], upserts.Select(upsert => upsert.Headcount).ToArray());
        Assert.Equal(2, hiringTodos.Count);
        Assert.False(hiringTodos[0].StartInBacklog);
        Assert.True(hiringTodos[1].StartInBacklog);
        Assert.All(hiringTodos, todo =>
            Assert.True(ChiefOfStaffAgent.TryGetHiringRecommendationId(
                todo.CorrelationId, out _)));
        Assert.NotNull(managerMessage);
        Assert.Equal(managerChatId, managerMessage.ChatId);
        Assert.Contains("Lead Web3D Developer", managerMessage.Content, StringComparison.Ordinal);
        Assert.Contains("QA / Playtester", managerMessage.Content, StringComparison.Ordinal);
        Assert.Contains("candidate-free hiring suggestions", managerMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, suggestedActions.Count);
        Assert.All(suggestedActions, action => Assert.Equal(messageId, action.MessageId));
        Assert.All(suggestedActions, action =>
            Assert.True(action.Parameters.GetProperty("recommendationId").TryGetGuid(out _)));
        Assert.Equal(
            ["Lead Web3D Developer", "QA / Playtester"],
            suggestedActions
                .Select(action => action.Parameters.GetProperty("role").GetString()!)
                .ToArray());
        Assert.Equal(2, suggestedActions.Select(action => action.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task ApprovedResourceChange_NotVisibleToChiefFailsInsteadOfSilentlyCompleting()
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(
                PlatformCapabilities.ResourceChangeRead,
                (_, _) => Task.FromResult(new ResourceChangeReadResponse([])));
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            installationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"),
                "Chief of Staff",
                null,
                "Chief of Staff",
                null,
                [],
                null,
                null,
                null));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.HandleEventAsync(
            new AgentEventEnvelope(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ManagementEvents.ResourceChangeDecided,
                JsonSerializer.SerializeToElement(new ResourceChangeDecisionEvent(
                    requestId,
                    organizationId,
                    Guid.NewGuid(),
                    chiefId,
                    "Approved",
                    DateTimeOffset.UtcNow)),
                DateTimeOffset.UtcNow),
            context,
            CancellationToken.None));

        Assert.Contains(requestId.ToString("D"), exception.Message, StringComparison.Ordinal);
        Assert.Contains("not visible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecommendationFulfilled_AcknowledgesExactEventAndAdvancesToNextSuggestion()
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var ownerChatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var next = new HiringRecommendationResponse(
            Guid.NewGuid(),
            null,
            "QA / Playtester",
            "Protect release quality.",
            "Suggested",
            null,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            Priority = 2,
            RoleKey = "quality",
            Headcount = 1
        };
        SendCommunicationMessageRequest? sent = null;
        SuggestUserActionRequest? suggested = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(
                PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory([], chiefId)))
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([next])))
            .RegisterCapability<JsonElement, CommunicationHubResponse>(
                ChiefOfStaffProfile.ReadCommunicationCapability,
                (_, _) => Task.FromResult(new CommunicationHubResponse(
                    chiefId,
                    true,
                    [new CommunicationChatResponse(
                        ownerChatId,
                        string.Empty,
                        null,
                        true,
                        true,
                        true,
                        true,
                        DateTimeOffset.UtcNow,
                        [
                            new CommunicationParticipantResponse(chiefId, "Chief", "Agent", "Chief of Staff"),
                            new CommunicationParticipantResponse(ownerId, "Owner", "Human", "CEO")
                        ],
                        null,
                        null,
                        0)],
                    [],
                    [])))
            .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(
                ChiefOfStaffProfile.SendCommunicationMessageCapability,
                (request, _) =>
                {
                    sent = request;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { id = messageId }));
                })
            .RegisterCapability<SuggestUserActionRequest, JsonElement>(
                ChiefOfStaffProfile.SuggestUserActionCapability,
                (request, _) =>
                {
                    suggested = request;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
                });
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            installationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"), "Chief", null, "Chief of Staff", null, [], null,
                ownerId.ToString("D"), "Owner"));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

        await agent.HandleEventAsync(
            FulfilledEvent(organizationId, Guid.NewGuid(), "Lead Web3D Developer"),
            context,
            CancellationToken.None);
        Assert.Null(sent);

        var fulfilled = FulfilledEvent(organizationId, installationId, "Lead Web3D Developer");
        await agent.HandleEventAsync(fulfilled, context, CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Contains("Lead Web3D Developer", sent.Content, StringComparison.Ordinal);
        Assert.Contains(next.Title, sent.Content, StringComparison.Ordinal);
        Assert.NotNull(suggested);
        Assert.Equal(next.Id, suggested.Parameters.GetProperty("recommendationId").GetGuid());
        Assert.Equal(next.Title, suggested.Parameters.GetProperty("role").GetString());
    }

    [Fact]
    public void HiringTodoRequest_PreservesPriorityCorrelationAndDormantState()
    {
        var recommendation = new HiringRecommendationResponse(
            Guid.NewGuid(), null, "Product Manager", "Own product outcomes.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Priority = 1,
            RoleKey = "product-manager"
        };

        var request = ChiefOfStaffAgent.BuildHiringTodoRequest(
            recommendation, startInBacklog: true);

        Assert.Equal("Hire Product Manager", request.Title);
        Assert.Equal(WorkPriorities.High, request.Priority);
        Assert.True(request.StartInBacklog);
        Assert.True(ChiefOfStaffAgent.TryGetHiringRecommendationId(
            request.CorrelationId, out var parsed));
        Assert.Equal(recommendation.Id, parsed);
    }

    [Fact]
    public async Task ActiveHiringTodo_RemainsInDoingWithoutAnotherMessageOrAction()
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(
            Guid.NewGuid(), null, "Product Manager", "Own product outcomes.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Priority = 1 };
        var item = PersonalTodo(
            ChiefOfStaffAgent.BuildHiringTodoRequest(recommendation, false), chiefId) with
        {
            Status = PersonalTodoStatuses.Running
        };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([recommendation])));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            installationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"), "Chief", null, "Chief of Staff", null, [], null, null, null));

        var result = await agent.HandlePersonalTodoAsync(item, context, CancellationToken.None);

        Assert.Equal(
            PersonalTodoResult.InProgress(
                "Awaiting the manager's review and hiring action for Product Manager."),
            result);
    }

    [Fact]
    public async Task FulfilledRecommendation_ResumesDoingTicketSoSdkCanMoveItToDone()
    {
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(
            recommendationId, null, "Product Manager", "Own product outcomes.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Priority = 1 };
        var doing = PersonalTodo(
            ChiefOfStaffAgent.BuildHiringTodoRequest(recommendation, false), chiefId) with
        {
            Status = PersonalTodoStatuses.Running
        };
        RequeuePersonalTodoItemRequest? resumed = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, PersonalTodoDirectory>(
                PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(
                        Guid.NewGuid(), chiefId, "Chief", ownerId, "Owner", 1, [doing])],
                    chiefId)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Requeue,
                (request, _) =>
                {
                    resumed = request;
                    return Task.FromResult(doing with { Status = PersonalTodoStatuses.Ready });
                })
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([])))
            .RegisterCapability<JsonElement, CommunicationHubResponse>(
                ChiefOfStaffProfile.ReadCommunicationCapability,
                (_, _) => Task.FromResult(new CommunicationHubResponse(
                    chiefId,
                    true,
                    [new CommunicationChatResponse(
                        Guid.NewGuid(), string.Empty, null, true, true, true, true,
                        DateTimeOffset.UtcNow,
                        [
                            new CommunicationParticipantResponse(
                                chiefId, "Chief", "Agent", "Chief of Staff"),
                            new CommunicationParticipantResponse(ownerId, "Owner", "Human", "CEO")
                        ],
                        null, null, 0)],
                    [],
                    [])))
            .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(
                ChiefOfStaffProfile.SendCommunicationMessageCapability,
                (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid() })));
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
        var context = runtime.CreateContext(
            organizationId.ToString("D"),
            installationId.ToString("D"),
            new AgentIdentity(
                chiefId.ToString("D"), "Chief", null, "Chief of Staff", null, [], null,
                ownerId.ToString("D"), "Owner"));

        await agent.HandleEventAsync(
            FulfilledEvent(
                organizationId, installationId, "Product Manager", recommendationId),
            context,
            CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(doing.Id, resumed.ItemId);
    }

    [Fact]
    public async Task ResolvedHiringTodo_ActivatesExactlyOneNextBacklogRole()
    {
        var chiefId = Guid.NewGuid();
        var resolved = new HiringRecommendationResponse(
            Guid.NewGuid(), null, "Product Manager", "Own product outcomes.", "Fulfilled",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Priority = 1 };
        var next = new HiringRecommendationResponse(
            Guid.NewGuid(), null, "Software Developer", "Build the product.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Priority = 2 };
        var currentItem = PersonalTodo(
            ChiefOfStaffAgent.BuildHiringTodoRequest(resolved, false), chiefId) with
        {
            Status = PersonalTodoStatuses.Running
        };
        var nextItem = PersonalTodo(
            ChiefOfStaffAgent.BuildHiringTodoRequest(next, true), chiefId);
        ActivatePersonalTodoItemRequest? activated = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, HiringBacklogResponse>(
                PlatformCapabilities.HiringRecommendationList,
                (_, _) => Task.FromResult(new HiringBacklogResponse([next])))
            .RegisterCapability<object, PersonalTodoDirectory>(
                PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory(
                    [new PersonalTodoBoard(Guid.NewGuid(), chiefId, "Chief", null, null, 1,
                        [currentItem, nextItem])], chiefId)))
            .RegisterCapability<ActivatePersonalTodoItemRequest, PersonalTodoItem>(
                PersonalTodoCapabilities.Activate,
                (request, _) =>
                {
                    activated = request;
                    return Task.FromResult(nextItem with { Status = PersonalTodoStatuses.Ready });
                });
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

        _ = await agent.HandlePersonalTodoAsync(
            currentItem,
            runtime.CreateContext(identity: new AgentIdentity(
                chiefId.ToString("D"), "Chief", null, "Chief of Staff", null, [], null, null, null)),
            CancellationToken.None);

        Assert.NotNull(activated);
        Assert.Equal(nextItem.Id, activated.ItemId);
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

    private static PersonalTodoItem PersonalTodo(
        AddPersonalTodoItemRequest request,
        Guid ownerId) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), ownerId, ownerId, "Sherly",
            request.Title, request.Description ?? string.Empty,
            request.StartInBacklog ? PersonalTodoStatuses.Backlog : PersonalTodoStatuses.Ready,
            request.Priority, 1024, 1, request.DueDate, request.SourceConversationId,
            request.SourceMessageId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            CorrelationId = request.CorrelationId
        };

    private static AgentEventEnvelope FulfilledEvent(
        Guid organizationId,
        Guid requestingInstallationId,
        string roleTitle,
        Guid? recommendationId = null)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        return new AgentEventEnvelope(
            Guid.NewGuid(),
            Guid.NewGuid(),
            HiringEvents.RecommendationFulfilled,
            JsonSerializer.SerializeToElement(new HiringRecommendationFulfilledEvent(
                organizationId,
                recommendationId ?? Guid.NewGuid(),
                Guid.NewGuid(),
                requestingInstallationId,
                "web3d",
                roleTitle,
                Guid.NewGuid(),
                null,
                1,
                1,
                [Guid.NewGuid()],
                occurredAt)),
            occurredAt);
    }

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
