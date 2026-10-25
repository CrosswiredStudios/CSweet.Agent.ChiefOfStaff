using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.ChiefOfStaff.Tests.Agent;

public sealed class ChiefLeadershipTests
{
    [Fact]
    public async Task InvalidCoverageIsBoundedAndBecomesDurableBlockedWork()
    {
        var model = new Model("not valid JSON"); var state = State();
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, state.Assessments[0], state, new(null, null, null, null, null, null, []), default);
        Assert.Equal(2, model.Calls);
        Assert.Equal("Blocked", result.NextAction);
        Assert.Null(result.AccountableOwnerId);
        Assert.Empty(result.EvidenceOrganizationUserIds);
    }

    [Fact]
    public async Task UnknownEvidenceCannotBecomeCoverage()
    {
        var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("AdequatelyCovered", "Agent", Guid.NewGuid(),
            "Unsupported claimed owner", "None", [Guid.NewGuid()])));
        var state = State();
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, state.Assessments[0], state, new(null, null, null, null, null, null, []), default);
        Assert.Equal(2, model.Calls);
        Assert.Equal("Blocked", result.NextAction);
    }

    [Fact]
    public async Task ClarificationDoesNotAssertUnprovenCeoOwnershipOrUnusedHiringFields()
    {
        var owner = Guid.NewGuid();
        var state = State() with { Assignments = [new(owner, null, true, ["chief-executive-officer"])] };
        var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("AtRisk", "Unresolved", owner,
            "Legal requires a licensed professional; the CEO title does not establish that coverage.", "Question", [owner],
            Question: "Who will provide licensed legal coverage?", RoleKey: "product-manager", RoleTitle: "Unused role", RoleObjective: "Unused objective")));
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, state.Assessments[0], state,
            new(null, null, null, null, null, null, []), default);
        Assert.Equal("Question", result.NextAction); Assert.Null(result.AccountableOwnerId);
        Assert.Null(result.RoleKey); Assert.Null(result.RoleTitle); Assert.Null(result.RoleObjective);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task InitialExplicitPriorityUsesTheAuthenticatedTurnWithoutAskingOrModelInference()
    {
        var model = new Model("must not run"); var source = Guid.NewGuid(); var turn = Guid.NewGuid(); var conversation = Guid.NewGuid();
        PresentChiefFocusRequest? captured = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read,
                (_, _) => Task.FromResult(new ChiefLeadershipSnapshot(0, null, [], [], false, false, Guid.NewGuid(), null, "inputs")))
            .RegisterCapability<PresentChiefFocusRequest, PresentedChiefFocus>(ChiefLeadershipCapabilities.Present, (r, _) =>
            { captured = r; return Task.FromResult(new PresentedChiefFocus(null, r.MessageId, 1)); });
        var context = runtime.CreateContext(Guid.NewGuid().ToString());
        var incoming = new UserMessageReceived(Guid.NewGuid(), conversation.ToString(), Guid.NewGuid().ToString(), "Focus on product", null, turn, MessageId: source);
        await new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator()).HandleEventAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), ChiefOfStaffProfile.UserMessageReceivedEvent,
                JsonSerializer.SerializeToElement(incoming, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow, "initial"), context, default);
        Assert.NotNull(captured); Assert.Equal("product", captured.ExplicitDomainId); Assert.Equal(turn, captured.OwnerTurnId);
        Assert.Equal(source, captured.MessageId); Assert.Equal(conversation, captured.ConversationId); Assert.Equal(0, model.Calls);
        Assert.Contains(runtime.Progress, x => x.GetProperty("isFinal").GetBoolean());
        Assert.Contains(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains("Leadership assessment cannot start", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains("I’ll assess", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, null, "Leadership assessment cannot start")]
    [InlineData(true, true, null, "Proactive leadership is paused")]
    [InlineData(true, false, "This profile requires its own proactive acceptance fixtures.", "Leadership assessment cannot start")]
    [InlineData(true, false, null, "I’ll assess the matching leadership responsibility")]
    public async Task FocusAnswerReportsThePostCaptureExecutionState(bool enabled, bool paused, string? block, string expected)
    {
        var before = State() with { AnsweredDecisionId = Guid.NewGuid() };
        var after = before with { ProactiveEnabled = enabled, Paused = paused, ConfigurationBlock = block,
            NextAssessmentId = enabled && !paused && block is null ? before.NextAssessmentId : null };
        var model = new Model("must not run"); var captures = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, (_, _) => Task.FromResult(captures == 0 ? before : after))
            .RegisterCapability<CaptureChiefFocusRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Capture, (_, _) =>
            { captures++; return Task.FromResult(before); }); // Historic idempotency receipt is not current execution state.
        await DeliverLeadershipInputAsync(runtime, model, "Decision: Where should we focus?\nAnswer: Product");
        Assert.Equal(1, captures); Assert.Equal(0, model.Calls);
        Assert.Contains(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains(expected, StringComparison.Ordinal));
        if (!enabled || paused || block is not null)
            Assert.DoesNotContain(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains("I’ll assess", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplayedFocusAnswerStillExplainsThePlatformBlockWithoutRecapturing()
    {
        var state = State();
        state = state with { AnsweredDecisionId = state.Focus!.SourceDecisionId, ProactiveEnabled = false,
            NextAssessmentId = null, ConfigurationBlock = "Proactive leadership is paused by the platform rollout control." };
        var model = new Model("must not run");
        var runtime = new AgentTestRuntime().RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(
            ChiefLeadershipCapabilities.Read, (_, _) => Task.FromResult(state));
        await DeliverLeadershipInputAsync(runtime, model, "Decision: Where should we focus?\nAnswer: Product");
        Assert.Equal(0, model.Calls);
        Assert.Contains(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains(state.ConfigurationBlock, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerResumeDoesNotClaimExecutionWhenThePlatformGateRemainsDisabled()
    {
        var state = State() with { Paused = true };
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, (_, _) => Task.FromResult(state))
            .RegisterCapability<ControlChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Control, (_, _) =>
            { state = state with { Paused = false, ProactiveEnabled = false, NextAssessmentId = null }; return Task.FromResult(state); });
        await DeliverLeadershipInputAsync(runtime, new Model("must not run"), "Resume proactive leadership");
        Assert.Contains(runtime.Progress, x => x.GetProperty("delta").GetString()!.Contains("Leadership assessment cannot start", StringComparison.Ordinal));
    }

    private static Task DeliverLeadershipInputAsync(AgentTestRuntime runtime, Model model, string text)
    {
        var incoming = new UserMessageReceived(Guid.NewGuid(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            text, null, Guid.NewGuid(), MessageId: Guid.NewGuid());
        return new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator()).HandleEventAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), ChiefOfStaffProfile.UserMessageReceivedEvent,
                JsonSerializer.SerializeToElement(incoming, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow, "focus"),
            runtime.CreateContext(Guid.NewGuid().ToString()), default);
    }

    [Fact]
    public async Task DuplicateWakeAfterAcceptedResultDoesNotRepeatModelOrEffects()
    {
        var state = State(); var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("NotApplicable", "Unresolved", null,
            "This domain is not applicable to the current documented business scope.", "None", [])));
        var recorded = new List<RecordChiefAssessmentRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, (_, _) => Task.FromResult(state))
            .RegisterCapability<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess, (request, _) =>
            {
                recorded.Add(request);
                var row = state.Assessments[0] with { ExecutionStatus = "Complete", CoverageStatus = request.Result.CoverageStatus, Revision = 2 };
                state = state with { Assessments = [row], NextAssessmentId = null };
                return Task.FromResult(row);
            });
        var agent = new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator());
        await runtime.DeliverEventAsync(agent, ChiefLeadershipCapabilities.Changed, new { });
        // Restart the agent, retain platform state, then redeliver a wake hint.
        agent = new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator());
        await runtime.DeliverEventAsync(agent, ChiefLeadershipCapabilities.Changed, new { });
        Assert.Single(recorded); Assert.Equal(1, model.Calls);
        Assert.Equal("None", recorded[0].Result.NextAction);
    }

    [Fact]
    public async Task PlatformRolloutControlPreventsProactiveModelCalls()
    {
        var state = State() with { ProactiveEnabled = false, NextAssessmentId = null };
        var model = new Model("must not run");
        var runtime = new AgentTestRuntime().RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read,
            (_, _) => Task.FromResult(state));
        await runtime.DeliverEventAsync(new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator()),
            ChiefLeadershipCapabilities.Changed, new { });
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task StaleCoverageResultDoesNotTurnOwnerPreemptionIntoBlockedEvidence()
    {
        var state = State(); var writes = 0;
        var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("NotApplicable", "Unresolved", null,
            "Current scope does not require this domain.", "None", [])));
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadChiefLeadershipRequest, ChiefLeadershipSnapshot>(ChiefLeadershipCapabilities.Read, (_, _) => Task.FromResult(state))
            .RegisterCapability<RecordChiefAssessmentRequest, ChiefCoverageAssessment>(ChiefLeadershipCapabilities.Assess, (_, _) =>
            { writes++; throw new PlatformCapabilityException(ChiefLeadershipCapabilities.Assess, PlatformCapabilityErrorCode.Conflict, "Owner focus changed."); });
        await runtime.DeliverEventAsync(new ChiefOfStaffAgent(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator()),
            ChiefLeadershipCapabilities.Changed, new { });
        Assert.Equal(1, writes); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task CancellationDuringAssessmentPropagatesWithoutInventingAResult()
    {
        var state = State(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChiefOfStaffAgent.InterpretCoverageAsync(new Model("{}"),
            state.Assessments[0], state, new(null, null, null, null, null, null, []), cancellation.Token));
    }

    [Fact]
    public async Task GameStudioCreativeProductFocusRecommendsTheDirector()
    {
        var state = State();
        var assessment = state.Assessments[0] with { ProfileKey = "game-studio", DomainId = "creative-product" };
        var model = new Model("must not run");
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, assessment, state,
            new(null, null, null, null, null, null, []), default, "game-studio");
        Assert.Equal("Hire", result.NextAction);
        Assert.Equal("game-director", result.RoleKey);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("research", "research-lead")]
    [InlineData("financial", "finance-lead")]
    [InlineData("legal", "legal-lead")]
    public async Task GameStudioOtherFocusUsesItsOwnLeadershipAssessment(string domain, string role)
    {
        var state = State();
        var assessment = state.Assessments[0] with { ProfileKey = "game-studio", DomainId = domain };
        state = state with { Focus = state.Focus! with { DomainId = domain }, Assessments = [assessment] };
        var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("NeedsCoverage", "Unresolved", null,
            "The selected responsibility needs a qualified lead; no current assignment covers it.", "Hire", [],
            RoleKey: role, RoleTitle: "Functional lead", RoleObjective: "Lead the selected responsibility.")));
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, assessment, state,
            new(null, null, null, null, null, null, []), default);
        Assert.Equal(1, model.Calls); Assert.Equal(role, result.RoleKey); Assert.Equal("Hire", result.NextAction);
        Assert.DoesNotContain("regardless of focus", model.SystemPrompt);
    }

    [Theory]
    [InlineData("research")]
    [InlineData("financial")]
    [InlineData("legal")]
    public async Task GameStudioCannotRedirectAnotherFocusToTheDirector(string domain)
    {
        var state = State(); var row = state.Assessments[0] with { ProfileKey = "game-studio", DomainId = domain };
        var model = new Model(JsonSerializer.Serialize(new ChiefCoverageResult("NeedsCoverage", "Unresolved", null,
            "Incorrect Director-first result.", "Hire", [], RoleKey: "game-director", RoleTitle: "Video Game Director", RoleObjective: "Wrong focus.")));
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, row, state,
            new(null, null, null, null, null, null, []), default);
        Assert.Equal(2, model.Calls); Assert.Equal("Blocked", result.NextAction); Assert.Null(result.RoleKey);
    }

    [Fact]
    public void GameStudioPromptsRouteTheSelectedFocusRatherThanAlwaysHiringADirector()
    {
        var overlay = CSweet.Agents.ChiefOfStaff.Profiles.BusinessOperatingProfiles.Find("game-studio")!.PromptOverlay;
        Assert.Contains("selected focus", overlay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("regardless of focus", overlay);
        Assert.DoesNotContain("regardless of focus", ChiefOfStaffProfile.SystemPrompt);
        Assert.DoesNotContain("regardless of focus", CSweet.Agents.ChiefOfStaff.Agent.ChiefOfStaffProfile.SystemPrompt);
    }

    private static ChiefLeadershipSnapshot State()
    {
        var row = new ChiefCoverageAssessment(Guid.NewGuid(), "general", 1, "product", "Product", "Product ownership", 1,
            "Unassessed", "Ready", 1, null, null, null, null, null, null, null, null, null, null);
        return new(1, new(Guid.NewGuid(), 1, "product", null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, null),
            [row], [], true, false, Guid.NewGuid(), row.Id, "inputs");
    }

    [Fact]
    public async Task RecommendationDeliveryRecoversLostAcknowledgmentAndThenSkipsCompletedEffects()
    {
        var state = State(); var chief = Guid.NewGuid(); var recommendationId = Guid.NewGuid();
        var assessment = state.Assessments[0] with { ExecutionStatus = "WaitingHire", RecommendationId = recommendationId, Rationale = "Verified product gap." };
        var recommendation = new HiringRecommendationResponse(recommendationId, null, "Product Manager", "Own outcomes", "Suggested", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var stored = new Dictionary<string, AgentOperatingStateResponse>();
        var messages = new Dictionary<string, Guid>(); var actions = new HashSet<string>(); var attempts = 0; var failAck = true;
        var model = new Model("must not run");
        var item = JsonSerializer.Deserialize<PersonalTodoItem>(JsonSerializer.Serialize(new { correlationId = $"hiring-recommendation:{recommendationId:N}", status = "Ready" }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(stored.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) =>
            {
                if (r.Status == "Acknowledged" && failAck) throw new InvalidOperationException("Lost acknowledgment");
                var prior = stored.GetValueOrDefault(r.StateKey); Assert.Equal(prior?.Revision, r.ExpectedRevision);
                var now = DateTimeOffset.UtcNow;
                var saved = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload, (prior?.Revision ?? 0) + 1, now, now);
                stored[r.StateKey] = saved; return Task.FromResult(saved);
            })
            .RegisterCapability<JsonElement, HiringBacklogResponse>(PlatformCapabilities.HiringRecommendationList, (_, _) => Task.FromResult(new HiringBacklogResponse([recommendation])))
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), chief, "Chief", null, null, 1, [item])], chief)))
            .RegisterCapability<JsonElement, CommunicationHubResponse>(ChiefOfStaffProfile.ReadCommunicationCapability, (_, _) => Task.FromResult(new CommunicationHubResponse(chief, true,
                [new(Guid.NewGuid(), "Owner", null, true, true, true, false, DateTimeOffset.UtcNow,
                    [new(chief, "Chief", "Agent", "Member"), new(Guid.NewGuid(), "Owner", "Human", "Member")], null, null, 0)], [], [])))
            .RegisterCapability<SendCommunicationMessageRequest, JsonElement>(ChiefOfStaffProfile.SendCommunicationMessageCapability, (r, _) =>
            { attempts++; if (!messages.ContainsKey(r.IdempotencyKey)) messages[r.IdempotencyKey] = Guid.NewGuid(); return Task.FromResult(JsonSerializer.SerializeToElement(new { id = messages[r.IdempotencyKey] })); })
            .RegisterCapability<SelectHiringCandidateRequest, HiringCandidateSelectionResponse>(HiringAutonomyCapabilities.SelectCandidate, (r, _) =>
                Task.FromResult(new HiringCandidateSelectionResponse(r.RecommendationId, HiringSelectionMode.RecommendCandidates, null, "Manual review")))
            .RegisterCapability<SuggestUserActionRequest, SuggestedUserActionResponse>(PlatformCapabilities.UserActionSuggest, (r, _) =>
            { actions.Add(r.IdempotencyKey); return Task.FromResult(new SuggestedUserActionResponse(Guid.NewGuid(), "browse", "Browse", null, "/", "Pending", DateTimeOffset.UtcNow)); });
        var context = runtime.CreateContext(identity: new(chief.ToString(), "Chief", null, null, null, [], null, null, null));
        ChiefOfStaffAgent Agent() => new(model, NullLogger<ChiefOfStaffAgent>.Instance, new Orchestrator());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Agent().DeliverCoverageRecommendationAsync(assessment, context, default));
        failAck = false;
        await Agent().DeliverCoverageRecommendationAsync(assessment, context, default);
        await Agent().DeliverCoverageRecommendationAsync(assessment, context, default);
        Assert.Equal(2, attempts); Assert.Single(messages); Assert.Single(actions); Assert.Equal(0, model.Calls);
        Assert.Equal("Acknowledged", Assert.Single(stored).Value.Status);
    }
    private sealed class Orchestrator : IChiefOfStaffOrchestrator
    {
        public Task<ChiefOperatingContext> AssembleContextAsync(AgentRuntimeContext context, CancellationToken token) => Task.FromResult(new ChiefOperatingContext(null, null, null, null, null, null, []));
        public string BuildGroundedPrompt(string prompt, string capability, ChiefOperatingContext context, AgentSettings settings) => prompt;
        public Task CaptureExplicitFactsAsync(IChatClient model, AssistantCapabilityInput input, ChiefOperatingContext context, AgentRuntimeContext runtime, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Model(string response) : IChatClient, IAgentLlmClientFactory
    {
        public int Calls;
        public string SystemPrompt = "";
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; SystemPrompt = messages.FirstOrDefault(x => x.Role == ChatRole.System)?.Text ?? ""; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response))); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
