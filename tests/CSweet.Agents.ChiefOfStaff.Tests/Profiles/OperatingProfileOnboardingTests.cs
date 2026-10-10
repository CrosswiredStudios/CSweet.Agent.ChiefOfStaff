using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using CSweet.Agents.ChiefOfStaff.Profiles;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests.Profiles;

public sealed class OperatingProfileOnboardingTests
{
    [Fact]
    public async Task Hiring_AnsweredModeInsidePlatformPromptAdvancesToPublishers()
    {
        var policy = new HiringPolicyResponse(Guid.NewGuid(), 0, new(), false);
        var harness = new Harness(Assessment(true, "general", 0.95), hiringPolicy: policy);
        await harness.HireAsync();
        var text = "<platform_interaction_context>Use hiring preferences.</platform_interaction_context>\n" +
            "<memory_context>Let's change hiring preferences.</memory_context>\n" +
            "<current_user_message>\nDecision: How should I help with hiring?\nAnswer: Recommend candidates\n</current_user_message>";
        await harness.Agent.HandleEventAsync(Event(ChiefOfStaffProfile.UserMessageReceivedEvent, Guid.NewGuid(),
            new UserMessageReceived(Guid.NewGuid(), harness.ConversationId.ToString(), Guid.NewGuid().ToString(), text, null, Guid.NewGuid())),
            harness.Context, CancellationToken.None);
        Assert.Equal(1, harness.CapturedHiringAnswers);
        Assert.Single(harness.Decisions.Values, x => x.Prompt == "How should I help with hiring?");
        Assert.Single(harness.Decisions.Values, x => x.Prompt == "Which publishers should I consider for hiring?");
    }

    [Fact]
    public void Hiring_CurrentStructuredMessageTakesPriorityOverHistoryAndMemory()
    {
        var incoming = new UserMessageReceived(Guid.NewGuid(), "chat", "owner", "Change hiring preferences", null)
            { CurrentMessageContent = "What are we working on?" };
        Assert.Equal("What are we working on?", ChiefOfStaffAgent.ReadCurrentUserMessage(incoming));
    }

    [Theory]
    [InlineData(true, "general", 0.95, false)]
    [InlineData(false, "game-studio", 0.95, true)]
    [InlineData(false, "game-studio", 0.50, false)]
    [InlineData(false, "general", 0.95, false)]
    public async Task Hiring_AssessesProfileBeforeCreatingTheFirstPhase(bool appropriate, string suggested, double confidence, bool mismatch)
    {
        var harness = new Harness(Assessment(appropriate, suggested, confidence));
        await harness.HireAsync();
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal(1, harness.Completions);
        var decision = Assert.Single(harness.Decisions.Values);
        if (mismatch)
        {
            Assert.Equal("game-studio", decision.ConfigurationChange!.ProposedValue);
            Assert.Equal("general", decision.ConfigurationChange.CurrentValue);
            Assert.Equal(["apply", "leave-unchanged"], decision.Options.Select(x => x.Id));
            Assert.Empty(harness.Todos);
            Assert.Contains("current operating mode is General", Assert.Single(harness.Messages.Values));
            Assert.Contains("Game Studio", Assert.Single(harness.Messages.Values));
        }
        else
        {
            Assert.Null(decision.ConfigurationChange);
            Assert.Equal(["product", "research", "financial", "legal"], decision.Options.Select(x => x.Id));
            Assert.Equal(4, harness.Todos.Count);
        }
        Assert.Contains("subscription accounting", harness.Model.Prompt);
        Assert.Contains("customDescription", harness.Model.Prompt);
    }

    [Fact]
    public async Task RetryAfterRestart_ReusesSavedAssessmentAndDecision()
    {
        var harness = new Harness(Assessment(false, "game-studio", 0.95));
        await harness.HireAsync();
        harness.Agent = harness.NewAgent();
        harness.Model.Response = "invalid if inference runs again";
        await harness.HireAsync();
        Assert.Equal(1, harness.Model.Calls);
        Assert.Single(harness.States);
        Assert.Single(harness.Messages);
        Assert.Single(harness.Decisions);
        Assert.Empty(harness.Todos);
    }

    [Fact]
    public async Task OnboardingRetryAfterSettingsRefresh_DoesNotCreateAnotherFocusPhase()
    {
        var harness = new Harness(Assessment(false, "game-studio", 0.95));
        await harness.HireAsync();
        var settings = new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
            ["llmModel"] = JsonSerializer.SerializeToElement("test-model"),
            ["businessOperatingProfile"] = JsonSerializer.SerializeToElement("game-studio")
        };
        var updated = await new AgentTestRuntime().ExecuteCapabilityAsync(harness.Agent,
            AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(settings));
        Assert.True(updated.Succeeded);
        await harness.HireAsync();
        Assert.Single(harness.Decisions);
        Assert.Single(harness.Messages);
        Assert.Empty(harness.Todos);
        Assert.Equal(1, harness.Model.Calls);
    }

    [Theory]
    [InlineData("apply", "game-studio", "creative-product")]
    [InlineData("leave-unchanged", "general", "product")]
    [InlineData("leave-unchanged", "custom", "product")]
    public async Task Answer_ResumesFocusUsingTheSavedEffectiveProfileWithoutAnotherInference(string choice, string profile, string firstFocus)
    {
        var harness = new Harness(Assessment(false, "game-studio", 0.95));
        var eventId = Guid.NewGuid();
        var answer = Event(ChiefOfStaffAgent.ConfigurationChoiceAnsweredEvent, eventId, new
        {
            decisionId = eventId, organizationId = harness.OrganizationId, conversationId = harness.ConversationId,
            key = "businessOperatingProfile", value = profile, selectedOptionId = choice
        });
        await harness.Agent.HandleEventAsync(answer, harness.Context, CancellationToken.None);
        await harness.Agent.HandleEventAsync(answer, harness.Context, CancellationToken.None);
        Assert.Equal(0, harness.Model.Calls);
        Assert.Equal(firstFocus, Assert.Single(harness.Decisions.Values).Options[0].Id);
        Assert.Single(harness.Messages);
        Assert.Equal(4, harness.Todos.Count);
        Assert.All(harness.Todos.Keys, x => Assert.Contains($"leadership-coverage:{profile}:", x));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"currentProfileAppropriate\":false,\"recommendedProfileKey\":\"custom\",\"confidence\":0.99,\"reason\":\"custom\"}")]
    [InlineData("{\"currentProfileAppropriate\":false,\"recommendedProfileKey\":\"invented\",\"confidence\":0.99,\"reason\":\"unknown\"}")]
    public async Task InvalidInference_DoesNotAcknowledgeHiringOrStartFocus(string response)
    {
        var harness = new Harness(response);
        var failure = await Assert.ThrowsAsync<PlatformCapabilityException>(harness.HireAsync);
        Assert.True(failure.Retryable);
        Assert.Empty(harness.Decisions);
        Assert.Empty(harness.Todos);
        Assert.Empty(harness.States);
        Assert.Equal(0, harness.Completions);
    }

    [Fact]
    public async Task MissingCompanyProfile_DoesNotGuessOrStartFocus()
    {
        var harness = new Harness(Assessment(false, "game-studio", 0.95), missingBusiness: true);
        var failure = await Assert.ThrowsAsync<PlatformCapabilityException>(harness.HireAsync);
        Assert.True(failure.Retryable);
        Assert.Equal(0, harness.Model.Calls);
        Assert.Empty(harness.Todos);
        Assert.Equal(0, harness.Completions);
    }

    [Theory]
    [InlineData(true, "general")]
    [InlineData(false, "game-studio")]
    public async Task RequiredAskFailure_KeepsOnboardingUnacknowledgedAndRetriesTheSameQuestion(bool appropriate, string suggested)
    {
        var harness = new Harness(Assessment(appropriate, suggested, 0.95)) { FailQuestions = true };
        await Assert.ThrowsAsync<PlatformCapabilityException>(harness.HireAsync);
        Assert.Equal(0, harness.Completions);
        Assert.Empty(harness.Decisions);
        harness.FailQuestions = false;
        await harness.HireAsync();
        Assert.Equal(1, harness.Completions);
        Assert.Equal(1, harness.Model.Calls);
        Assert.Single(harness.Messages);
        Assert.Single(harness.Decisions);
    }

    [Fact]
    public void OrdinaryQuestions_OmitConfigurationExtensionForOlderAskToolSchemas()
    {
        var request = new RequestUserInputRequest(Guid.NewGuid(), null, Guid.NewGuid(), "Choose a focus",
            [new("a", "Product"), new("b", "Research")], "a", "ordinary-question");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var ordinary = JsonSerializer.SerializeToElement(request, options);
        Assert.False(ordinary.TryGetProperty("configurationChange", out _));
        var profileChoice = JsonSerializer.SerializeToElement(request with
            { ConfigurationChange = new("businessOperatingProfile", "general", "game-studio") }, options);
        Assert.Equal("game-studio", profileChoice.GetProperty("configurationChange").GetProperty("proposedValue").GetString());
    }

    [Fact]
    public void RelayedQuestion_UsesDistinctBoundedChoicesAndKeepsTheRecommendation()
    {
        var escalation = new ProductEscalationRequest(Guid.NewGuid(), Guid.NewGuid(), "Launch", "Which launch?", "Timing",
            ["Alpha", "Beta", " beta ", "Gamma", "Delta", "Epsilon"], "Epsilon", Guid.NewGuid(), "relay");
        var choices = ChiefOfStaffAgent.BuildEscalationOptions(escalation);
        Assert.Equal(4, choices.Count);
        Assert.Equal("Epsilon", choices[0].Label);
        Assert.Equal(4, choices.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var confirmation = ChiefOfStaffAgent.BuildEscalationOptions(escalation with { Options = ["Beta"] });
        Assert.Equal(["Beta", "Discuss alternatives"], confirmation.Select(x => x.Label));
    }

    [Theory]
    [InlineData("mode", "recommend")]
    [InlineData("level", "approved")]
    [InlineData("costs", "free")]
    [InlineData("permissions", "none")]
    [InlineData("publishers", "prefer")]
    public async Task Hiring_AsksOnePolicyQuestionBeforeFocusAndRetriesWithoutDuplicates(string stage, string recommended)
    {
        var policy = new HiringPolicyResponse(Guid.NewGuid(), 0, new(), false, SetupStage: stage);
        var harness = new Harness(Assessment(true, "general", 0.95), hiringPolicy: policy);
        await harness.HireAsync(); await harness.HireAsync();
        var decision = Assert.Single(harness.Decisions.Values);
        Assert.Equal(recommended, decision.RecommendedOptionId);
        Assert.StartsWith($"hiring-policy:{policy.InstallationId:N}:{stage}:", decision.IdempotencyKey);
        Assert.Single(harness.Messages);
        Assert.Empty(harness.Todos);
        Assert.DoesNotContain("focus", decision.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hiring_WithSavedPreferencesContinuesToCompanyFocus()
    {
        var policy = new HiringPolicyResponse(Guid.NewGuid(), 1, new(), true, SetupStage: "complete");
        var harness = new Harness(Assessment(true, "general", 0.95), hiringPolicy: policy);
        await harness.HireAsync();
        Assert.Contains("focus", Assert.Single(harness.Decisions.Values).Prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string Assessment(bool appropriate, string key, double confidence) => JsonSerializer.Serialize(new
        { currentProfileAppropriate = appropriate, recommendedProfileKey = key, confidence, reason = "The company develops its own games." });
    private static AgentEventEnvelope Event(string name, Guid id, object payload) => new(Guid.NewGuid(), id, name,
        JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow, "profile-test");

    private sealed class Harness
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ConversationId { get; } = Guid.NewGuid();
        private Guid EmployeeId { get; } = Guid.NewGuid();
        private Guid EventId { get; } = Guid.NewGuid();
        public AgentRuntimeContext Context { get; }
        public ChiefOfStaffAgent Agent { get; set; }
        public FakeModel Model { get; }
        public int Completions { get; private set; }
        public int CapturedHiringAnswers { get; private set; }
        public bool FailQuestions { get; set; }
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public Dictionary<string, string> Messages { get; } = [];
        public Dictionary<string, RequestUserInputRequest> Decisions { get; } = [];
        public Dictionary<string, JsonElement> Todos { get; } = [];
        private Dictionary<string, Guid> MessageIds { get; } = [];
        public Harness(string response, bool missingBusiness = false, HiringPolicyResponse? hiringPolicy = null)
        {
            Model = new FakeModel(response);
            Agent = NewAgent();
            var runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(request.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (request, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var state = new AgentOperatingStateResponse(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                        request.Status, request.SourceRevisions, request.ConditionCodes, request.DecisionFingerprint,
                        request.OpenCommitmentCorrelations, request.AttentionReviewId, request.Payload, 1, now, now);
                    States[request.StateKey] = state;
                    return Task.FromResult(state);
                })
                .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(ChiefOfStaffProfile.SendCommunicationMessageCapability, (request, _) =>
                {
                    Messages[request.IdempotencyKey] = request.Content;
                    if (!MessageIds.TryGetValue(request.IdempotencyKey, out var id)) MessageIds[request.IdempotencyKey] = id = Guid.NewGuid();
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { id }));
                })
                .RegisterCapability<RequestUserInputRequest, RequestUserInputResponse>(ChiefOfStaffProfile.RequestUserInputCapability, (request, _) =>
                {
                    if (FailQuestions)
                        throw new PlatformCapabilityException(ChiefOfStaffProfile.RequestUserInputCapability,
                            PlatformCapabilityErrorCode.Unavailable, "Question service is temporarily unavailable.", retryable: true);
                    Decisions[request.IdempotencyKey] = request;
                    return Task.FromResult(new RequestUserInputResponse(Guid.NewGuid()));
                })
                .RegisterCapability<JsonElement, JsonElement>("work.personal-todo.read.v1", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { boards = Array.Empty<object>() })))
                .RegisterCapability<JsonElement, JsonElement>("work.personal-todo.add.v1", (request, _) =>
                {
                    Todos[request.GetProperty("idempotencyKey").GetString()!] = request;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid() }));
                })
                .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(AgentLifecycleCapabilities.CompleteOnboarding, (_, _) =>
                {
                    Completions++;
                    return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow));
                });
            if (!missingBusiness)
                runtime.RegisterCapability<JsonElement, BusinessProfileResponse>(PlatformCapabilities.BusinessProfileRead, (_, _) => Task.FromResult(new BusinessProfileResponse(
                    OrganizationId, "Example", "SaaS", "Software", "subscription accounting", null, "Validation",
                    [], [], null, [], null, [], [], null, "UTC", 1, 0.2m, new Dictionary<string, ProfileFieldProvenance>())));
            if (hiringPolicy is not null)
            {
                runtime.RegisterCapability<JsonElement, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, (_, _) => Task.FromResult(hiringPolicy));
                runtime.RegisterCapability<CaptureHiringPolicyDecisionRequest, HiringPolicyResponse>(HiringAutonomyCapabilities.CaptureDecision, (request, _) =>
                {
                    CapturedHiringAnswers++;
                    hiringPolicy = hiringPolicy with { Revision = hiringPolicy.Revision + 1, SetupStage = "publishers" };
                    return Task.FromResult(hiringPolicy);
                });
            }
            Context = runtime.CreateContext(OrganizationId.ToString("D"), identity: new AgentIdentity(EmployeeId.ToString("D"), "Chief", null, null, null, [], null, null, null));
        }
        public ChiefOfStaffAgent NewAgent() => new(Model, NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
        public Task HireAsync() => Agent.HandleEventAsync(Event(ChiefOfStaffProfile.OnboardedEvent, EventId,
            new AgentOnboardedEvent(OrganizationId, EmployeeId, Guid.NewGuid(), ConversationId, DateTimeOffset.UtcNow)), Context, CancellationToken.None);
    }

    private sealed class FakeModel(string response) : IChatClient, IAgentLlmClientFactory
    {
        public string Response { get; set; } = response;
        public string Prompt { get; private set; } = "";
        public int Calls { get; private set; }
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompt = string.Join("\n", messages.Select(x => x.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Response)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
