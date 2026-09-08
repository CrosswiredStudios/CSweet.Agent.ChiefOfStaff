using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class OperatingProfileOnboardingTests
{
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
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public Dictionary<string, string> Messages { get; } = [];
        public Dictionary<string, RequestUserInputRequest> Decisions { get; } = [];
        public Dictionary<string, JsonElement> Todos { get; } = [];
        private Dictionary<string, Guid> MessageIds { get; } = [];
        public Harness(string response, bool missingBusiness = false)
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
