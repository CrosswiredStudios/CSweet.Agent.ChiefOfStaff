using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CSweet.Memory;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.ChiefOfStaff;

public sealed class ChiefOfStaffAgent : CSweetAgentBase, IAgentActivationHandler
{
    private readonly IAgentLlmClientFactory? _llmClientFactory;
    private readonly ILogger<ChiefOfStaffAgent> _logger;
    private readonly ChiefOfStaffOrchestrator _orchestrator;

    public ChiefOfStaffAgent(ILogger<ChiefOfStaffAgent> logger, ChiefOfStaffOrchestrator orchestrator)
    {
        _logger = logger;
        _orchestrator = orchestrator;
    }

    public ChiefOfStaffAgent(
        IAgentLlmClientFactory llmClientFactory,
        ILogger<ChiefOfStaffAgent> logger,
        ChiefOfStaffOrchestrator orchestrator)
    {
        _llmClientFactory = llmClientFactory;
        _logger = logger;
        _orchestrator = orchestrator;
    }

    public override string AgentId => ChiefOfStaffProfile.AgentId;

    public override string Version => ChiefOfStaffProfile.Version;

    public async Task OnActivatedAsync(
        AgentActivationContext activation,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        await SyncHiringPersonalTodosAsync(context, null, null, cancellationToken);
        _logger.LogInformation(
            "Chief of Staff reconciled its personal hiring queue during {ActivationReason} activation {TickId}.",
            activation.Reason,
            activation.TickId);
    }

    protected override string ConfigurationSchemaVersion => ChiefOfStaffProfile.ConfigurationSchemaVersion;

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder)
    {
        return builder
            .LlmProvider(
                "llmProviderId",
                "LLM Provider",
                required: true,
                description: "Selects the provider profile the Chief of Staff should use when it is allowed to call a user-configured model.")
            .LlmModel(
                "llmModel",
                "Model",
                dependsOnFieldKey: "llmProviderId",
                required: true,
                description: "Selects the chat model to use from the chosen provider profile.")
            .Select(
                BusinessOperatingProfiles.ConfigurationKey,
                "Business Operating Profile",
                BusinessOperatingProfiles.ConfigurationOptions,
                required: true,
                description: "Applies business-type organizational defaults while preserving the Chief's core boundaries.",
                defaultValue: BusinessOperatingProfiles.GeneralKey)
            .TextArea(
                BusinessOperatingProfiles.CustomDescriptionKey,
                "Custom Business Description",
                description: "Optional organizational context used when the Custom operating profile is selected.",
                placeholder: "Describe the business model and any unusual organizational needs.")
            .Select(
                "responseTone",
                "Response Tone",
                [
                    new AgentConfigurationOption("concise", "Concise"),
                    new AgentConfigurationOption("balanced", "Balanced"),
                    new AgentConfigurationOption("detailed", "Detailed")
                ],
                required: true,
                description: "Controls how much detail the assistant uses in executive responses.",
                defaultValue: "concise")
            .Boolean(
                "proactivePlanning",
                "Proactive Planning",
                required: true,
                description: "Allows the assistant to suggest organization and staffing plans without being explicitly asked.",
                defaultValue: true)
            .Number(
                "maxPlanItems",
                "Maximum Plan Items",
                required: true,
                description: "Caps the number of roles the assistant proposes in a single staffing plan.",
                minimum: 3,
                maximum: 20,
                step: 1,
                defaultValue: 3)
            .Number(
                "maxAlternatives",
                "Maximum Alternatives",
                required: true,
                description: "Caps materially useful alternatives in an executive recommendation.",
                minimum: 0,
                maximum: 2,
                step: 1,
                defaultValue: 2)
            .TextArea(
                "customInstructions",
                "Custom Instructions",
                description: "Optional operating guidance that is appended to the assistant's built-in instructions.",
                placeholder: "Example: Prefer short plans with clear owners and approval points.");
    }

    public override async Task HandleEventAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (string.Equals(message.EventType, ChiefOfStaffProfile.OnboardedEvent, StringComparison.Ordinal))
        {
            await HandleOnboardedAsync(message, context, cancellationToken);
            return;
        }

        if (string.Equals(message.EventType, ChiefOfStaffProfile.RecommendationFulfilledEvent, StringComparison.Ordinal))
        {
            await HandleRecommendationFulfilledAsync(message, context, cancellationToken);
            return;
        }

        if (string.Equals(message.EventType, ManagementEvents.ResourceChangeRequested, StringComparison.Ordinal))
        {
            await HandleResourceChangeRequestedAsync(message, context, cancellationToken);
            return;
        }

        if (string.Equals(message.EventType, ManagementEvents.ResourceChangeDecided, StringComparison.Ordinal))
        {
            await HandleResourceChangeDecidedAsync(message, context, cancellationToken);
            return;
        }

        if (string.Equals(message.EventType, ManagementEvents.ReviewDue, StringComparison.Ordinal))
        {
            await HandleManagementReviewAsync(message, context, cancellationToken);
            return;
        }

        if (message.EventType is "com.csweet.action.completed.v1" or
            "com.csweet.action.rejected.v1" or
            "com.csweet.approval.completed.v1" or
            ManagementEvents.WorkstreamChanged or
            ManagementEvents.WorkforcePlanDecided)
        {
            await PushProductManagerContextUpdatesAsync(message.EventId, context, cancellationToken);
            return;
        }

        if (!string.Equals(message.EventType, ChiefOfStaffProfile.UserMessageReceivedEvent, StringComparison.Ordinal))
        {
            return;
        }

        var incoming = DeserializePayload<UserMessageReceived>(message.Data);

        if (incoming is null ||
            incoming.ProviderProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(incoming.Message))
        {
            _logger.LogWarning(
                "Ignored malformed user message event {EventId}.",
                message.EventId);
            return;
        }

        var conversationId = incoming.ConversationId;
        var builder = new System.Text.StringBuilder();
        var usage = new UsageDetails();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sequence = 0;
        await using var turnStream = context.CreateTurnStream(
            conversationId,
            incoming.TurnId,
            incoming.Attempt);

        await turnStream.ActivityStartedAsync(
            "Chief of Staff accepted the request.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = "accepted"
            },
            cancellationToken);

        _logger.LogInformation(
            "Chief of Staff received user message event {EventId} for conversation {ConversationId}. Provider {ProviderProfileId}. MessageLength {MessageLength}.",
            message.EventId,
            conversationId,
            incoming.ProviderProfileId,
            incoming.Message.Length);

        try
        {
            await foreach (var update in StreamAssistantDeltasAsync(
                new AssistantCapabilityInput(
                    incoming.ProviderProfileId,
                    conversationId,
                    incoming.Message,
                    incoming.Context,
                    incoming.UserId,
                    incoming.MessageId,
                    incoming.TurnId),
                ChiefOfStaffProfile.ConverseCapability,
                context,
                operatingContext: null,
                cancellationToken))
            {
                if (update.Usage is not null)
                {
                    usage.Add(update.Usage);
                }

                foreach (var activity in update.Activities ?? [])
                {
                    if (activity.Kind == AgentTurnStreamKinds.ActivityStarted)
                        await turnStream.ActivityStartedAsync(activity.Title, activity.Metadata, cancellationToken);
                    else if (activity.Kind == AgentTurnStreamKinds.ActivityCompleted)
                        await turnStream.ActivityCompletedAsync(activity.Title, activity.Metadata, cancellationToken);
                    else
                        await turnStream.ActivityFailedAsync(activity.Title, activity.Metadata, cancellationToken);
                }

                if (update.StartsNewDraft)
                {
                    builder.Clear();
                    await turnStream.ResetDraftAsync(
                        "The model started a consolidated draft after using a tool.",
                        cancellationToken);
                }

                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    await turnStream.WriteReasoningAsync(update.ReasoningDelta, cancellationToken);
                }

                if (!string.IsNullOrEmpty(update.Delta))
                {
                    builder.Append(update.Delta);
                    await turnStream.WriteDraftAsync(update.Delta, cancellationToken);
                }
            }

            await turnStream.CompleteReasoningAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Chief of Staff failed to generate a response for conversation {ConversationId}.",
                conversationId);

            await turnStream.FailAsync(BuildSafeFailureMessage(exception), cancellationToken);
            await WriteRunLogAsync(
                incoming.ProviderProfileId,
                incoming.Message,
                output: null,
                status: "Failed",
                startedAt,
                stopwatch.ElapsedMilliseconds,
                usage: null,
                exception.Message,
                cancellationToken);
            return;
        }

        var response = EnforceResponseMode(builder.ToString());
        if (string.IsNullOrWhiteSpace(response))
        {
            _logger.LogWarning(
                "Chief of Staff generated an empty response for conversation {ConversationId}.",
                conversationId);

            await turnStream.FailAsync(
                "The Chief of Staff could not complete the request because the model provider returned an empty response.",
                cancellationToken);
            await WriteRunLogAsync(
                incoming.ProviderProfileId,
                incoming.Message,
                output: null,
                status: "Failed",
                startedAt,
                stopwatch.ElapsedMilliseconds,
                usage,
                "The model provider returned an empty response.",
                cancellationToken);
            return;
        }

        if (!string.Equals(response, builder.ToString(), StringComparison.Ordinal))
        {
            await turnStream.ResetDraftAsync(
                "The Chief of Staff response policy replaced the provisional draft.",
                cancellationToken);
            await turnStream.WriteDraftAsync(response, cancellationToken);
        }

        await turnStream.ActivityStartedAsync(
            "Validating executive follow-up actions.",
            cancellationToken: cancellationToken);
        await EnsureDefaultProductManagerRecommendationAsync(
            response,
            $"user-message:{message.EventId:N}",
            context,
            cancellationToken);
        await SyncHiringPersonalTodosAsync(
            context,
            Guid.TryParse(incoming.ConversationId, out var sourceConversationId)
                ? sourceConversationId
                : null,
            incoming.MessageId == Guid.Empty ? null : incoming.MessageId,
            cancellationToken);
        await turnStream.ActivityCompletedAsync(
            "Validated executive follow-up actions.",
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Chief of Staff publishing validated response for conversation {ConversationId}. Sequence {Sequence}. ResponseLength {ResponseLength}.",
            conversationId,
            sequence,
            response.Length);

        await turnStream.CommitAsync(response, cancellationToken);

        _logger.LogInformation(
            "Chief of Staff completed streaming for conversation {ConversationId}. Chunks {ChunkCount}. ResponseLength {ResponseLength}.",
            conversationId,
            sequence,
            response.Length);

        try
        {
            await AttachMentionedHiringActionAsync(
                incoming.TurnId,
                response,
                $"user-message:{message.EventId}",
                context,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Chief of Staff could not attach a suggested hiring action to chat turn {TurnId}.",
                incoming.TurnId);
        }

        await WriteRunLogAsync(
            incoming.ProviderProfileId,
            incoming.Message,
            response,
            "Completed",
            startedAt,
            stopwatch.ElapsedMilliseconds,
            usage,
            failureMessage: null,
            cancellationToken);

        await PushProductManagerContextUpdatesAsync(message.EventId, context, cancellationToken);
    }

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(
        PersonalTodoItem item, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        if (TryGetHiringRecommendationId(item.CorrelationId, out var recommendationId))
        {
            var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
            var recommendation = backlog.Recommendations.SingleOrDefault(x => x.Id == recommendationId);
            if (recommendation is not null)
            {
                return PersonalTodoResult.InProgress(
                    $"Awaiting the manager's review and hiring action for {recommendation.Title}.");
            }

            await ActivateNextHiringTodoAsync(backlog, context, cancellationToken);
            return PersonalTodoResult.Completed(
                "The hiring recommendation resolved and the next role was activated.");
        }

        var mentionContext = string.Join(", ", item.Mentions.Select(x =>
            $"{x.DisplayName} ({x.EmployeeType}, organizationUserId={x.OrganizationUserId:D})"));
        var response = await GenerateResponseAsync(
            new AssistantCapabilityInput(
                Settings.GetGuid("llmProviderId") ?? Guid.Empty,
                (item.SourceConversationId ?? item.Id).ToString("D"),
                $"""
Execute this claimed personal task within your existing Chief of Staff authority and currently
granted platform tools. Do not request broader authority. Authoritative mentioned identities:
{(string.IsNullOrEmpty(mentionContext) ? "none" : mentionContext)}

Task: {item.Title}
Details: {item.Description}

Use brokered actions for every effect. Return `BLOCKED: <durable reason>` if the task is unsupported,
impossible, or denied. Otherwise perform the task and return a concise completion summary.
""",
                new Dictionary<string, string>
                {
                    ["personalTodoItemId"] = item.Id.ToString("D"),
                    ["sourceMessageId"] = item.SourceMessageId?.ToString("D") ?? string.Empty
                },
                MessageId: item.SourceMessageId ?? Guid.Empty),
            ChiefOfStaffProfile.ConverseCapability, context, cancellationToken);
        return response.Response.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            ? PersonalTodoResult.Blocked(response.Response[8..].Trim())
            : PersonalTodoResult.Completed(response.Response);
    }

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedCapability(request.Capability))
        {
            return AgentWorkResult.Failure(
                $"Capability '{request.Capability}' is not supported by the Chief of Staff.");
        }

        if (request.Capability == ChiefOfStaffProfile.ManagementCheckInCapability)
        {
            var checkIn = DeserializePayload<ManagementCheckInRequest>(request.Payload);
            if (checkIn is null) return AgentWorkResult.Failure("The management check-in input is invalid.");
            var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
            return new AgentWorkResult(true, SerializePayload(ChiefOfStaffOrchestrator.BuildManagementReport(checkIn, operatingContext)));
        }

        if (request.Capability == ProductManagementCapabilities.RoleBrief)
        {
            var roleBriefRequest = DeserializePayload<ProductRoleBriefRequest>(request.Payload);
            if (roleBriefRequest is null)
                return AgentWorkResult.Failure("The Product Manager role-brief request is invalid.");
            var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
            if (!IsAuthorizedProductManagerRequest(request.RequestingAgentId, roleBriefRequest, context, operatingContext))
                return AgentWorkResult.Failure("Only an active Product Manager sharing this Chief's CEO manager may request a role brief.");
            if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId))
                return AgentWorkResult.Failure("The Chief employee identity is unavailable.");
            return new AgentWorkResult(true, SerializePayload(
                ChiefOfStaffOrchestrator.BuildProductRoleBrief(
                    operatingContext,
                    chiefId,
                    roleBriefRequest.ProductManagerOrganizationUserId)));
        }

        if (request.Capability == ProductManagementCapabilities.PlanReview)
        {
            var reviewRequest = DeserializePayload<ProductPlanReviewRequest>(request.Payload);
            if (reviewRequest is null)
                return AgentWorkResult.Failure("The Product Manager plan-review request is invalid.");
            var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
            if (!IsAuthorizedProductManagerRequest(request.RequestingAgentId, reviewRequest, context, operatingContext))
                return AgentWorkResult.Failure("Only an active Product Manager sharing this Chief's CEO manager may submit a product plan.");
            return new AgentWorkResult(true, SerializePayload(
                ChiefOfStaffOrchestrator.BuildProductPlanReview(reviewRequest, operatingContext)));
        }

        if (request.Capability == ProductManagementCapabilities.Escalation)
        {
            var escalation = DeserializePayload<ProductEscalationRequest>(request.Payload);
            if (escalation is null)
                return AgentWorkResult.Failure("The Product Manager escalation is invalid.");
            var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
            if (!IsAuthorizedProductManagerRequest(request.RequestingAgentId, escalation, context, operatingContext))
                return AgentWorkResult.Failure("Only an active Product Manager sharing this Chief's CEO manager may escalate a decision.");
            try
            {
                var response = await EscalateProductDecisionToOwnerAsync(escalation, context, cancellationToken);
                return new AgentWorkResult(true, SerializePayload(response));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Chief could not deliver Product Manager escalation {Topic}.", escalation.Topic);
                return AgentWorkResult.Failure(
                    "The Chief could not deliver the Product Manager's executive question.");
            }
        }

        var input = DeserializePayload<AssistantCapabilityInput>(request.Payload);

        if (input is null ||
            input.ProviderProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.Prompt))
        {
            return AgentWorkResult.Failure(
                "The capability input is missing a provider profile or prompt.");
        }

        try
        {
            var response = await GenerateResponseAsync(
                input,
                request.Capability,
                context,
                cancellationToken);

            return new AgentWorkResult(true, SerializePayload(response));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Chief of Staff failed capability {Capability}.",
                request.Capability);

            return AgentWorkResult.Failure(
                "The Chief of Staff could not complete the request.");
        }
    }

    private async Task HandleOnboardedAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var onboarding = DeserializePayload<AgentOnboardedEvent>(message.Payload)
            ?? throw new InvalidOperationException("The onboarding event payload is empty.");
        var eventId = message.EventId;
        if (onboarding.OrganizationId == Guid.Empty ||
            onboarding.AgentOrganizationUserId == Guid.Empty ||
            onboarding.HiringOrganizationUserId == Guid.Empty ||
            onboarding.ConversationId == Guid.Empty ||
            !string.Equals(context.BusinessId, onboarding.OrganizationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The onboarding event identity is invalid for this Chief of Staff instance.");

        var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
        var profile = BusinessOperatingProfiles.Resolve(Settings);
        var openingMessage = BuildFocusOnboardingMessage(operatingContext, profile);
        await EnsureLeadershipCoverageAgendaAsync(profile, context, cancellationToken);
        var openingMessageId = await SendCommunicationMessageAsync(
            onboarding.ConversationId,
            openingMessage,
            $"agent-onboarded:{eventId:N}",
            context,
            cancellationToken);
        await AttachOnboardingFocusDecisionAsync(
            onboarding.ConversationId,
            openingMessageId,
            profile,
            $"agent-onboarded:{eventId:N}:focus",
            context,
            cancellationToken);
        _ = await context.Platform.Lifecycle.CompleteOnboardingAsync(
            message,
            cancellationToken);

        _logger.LogInformation(
            "Chief of Staff completed onboarding event {EventId} in conversation {ConversationId}.",
            eventId,
            onboarding.ConversationId);
    }

    internal static string BuildFocusOnboardingMessage(
        ChiefOperatingContext context,
        BusinessOperatingProfile profile)
    {
        var business = context.BusinessProfile;
        if (business is null)
            return $"I’m ready to help shape the company using the {profile.Label} operating profile. Choose the area where you want to focus first.";

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(business.Industry)) details.Add($"in {business.Industry.Trim()}");
        if (!string.IsNullOrWhiteSpace(business.Mission)) details.Add($"with the mission “{business.Mission.Trim()}”");
        else if (!string.IsNullOrWhiteSpace(business.Description)) details.Add(business.Description.Trim());
        var understood = details.Count == 0
            ? $"I’ve reviewed the business profile for {business.Name}."
            : $"I’ve reviewed {business.Name}, {string.Join(" ", details)}.";
        return $"{understood} I’ll help establish the leadership coverage the company needs without overloading you with a full organization plan. Choose the area where you want to focus first.";
    }

    private async Task AttachOnboardingFocusDecisionAsync(
        Guid conversationId,
        Guid messageId,
        BusinessOperatingProfile profile,
        string idempotencyKey,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = profile.FocusOptions
                .Select(x => new RequestUserInputOption(x.Id, x.Label, x.Description))
                .ToList();
            _ = await context.Platform.InvokeAsync<RequestUserInputRequest, RequestUserInputResponse>(
                ChiefOfStaffProfile.RequestUserInputCapability,
                new RequestUserInputRequest(
                    conversationId,
                    null,
                    messageId,
                    "Where would you like the company to focus first?",
                    options,
                    options[0].Id,
                    idempotencyKey),
                cancellationToken);
        }
        catch (PlatformCapabilityException exception)
        {
            _logger.LogWarning(
                exception,
                "Chief of Staff could not attach the onboarding focus decision to message {MessageId}.",
                messageId);
        }
    }

    private async Task EnsureLeadershipCoverageAgendaAsync(
        BusinessOperatingProfile profile,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId))
            throw new InvalidOperationException("The Chief of Staff employee identity is unavailable.");
        var directory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var existing = directory.Boards
            .SingleOrDefault(x => x.OwnerOrganizationUserId == chiefId)?
            .Items.Select(x => x.CorrelationId)
            .Where(x => x is not null)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        foreach (var item in profile.LeadershipCoverage)
        {
            var correlationId = $"leadership-coverage:{profile.Key}:{item.Id}";
            if (existing.Contains(correlationId)) continue;
            _ = await context.Platform.PersonalTodo.AddAsync(
                new AddPersonalTodoItemRequest(
                    item.Title,
                    item.Description,
                    WorkPriorities.Medium,
                    null,
                    $"leadership-coverage:{profile.Key}:{item.Id}:personal-todo",
                    null,
                    null,
                    null,
                    correlationId)
                {
                    StartInBacklog = true
                },
                cancellationToken);
        }
    }

    private async Task HandleResourceChangeRequestedAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var resourceEvent = DeserializePayload<ResourceChangeDecisionEvent>(message.Payload);
        if (resourceEvent is null) return;
        var read = await context.Platform.ReadResourceChangesAsync(
            new ResourceChangeReadRequest(resourceEvent.RequestId),
            cancellationToken);
        var request = read.Requests.SingleOrDefault(x => x.Id == resourceEvent.RequestId);
        if (request is null) return;

        var (_, self, organization) = await RequireActiveChiefAsync(context, cancellationToken);
        var requester = organization.People.SingleOrDefault(x =>
            x.Id == request.RequesterOrganizationUserId &&
            x.IsActive &&
            x.ReportsToId == self.Id);
        if (request.ManagerOrganizationUserId != self.Id || requester is null)
            return;

        var missingPurpose = request.Roles.FirstOrDefault(x =>
            string.IsNullOrWhiteSpace(x.Purpose) || x.RequiredCapabilities.Count == 0);
        var decision = missingPurpose is null
            ? ResourceChangeDecisionKinds.Approve
            : ResourceChangeDecisionKinds.RequestRevision;
        var comment = missingPurpose is null
            ? "Approved as an organizational role-set decision. Spending, candidate selection, and each hire remain separately controlled."
            : $"Clarify the purpose and required capabilities for {missingPurpose.Title}.";
        _ = await context.Platform.DecideResourceChangeAsync(
            new ResourceChangeDecisionRequest(
                request.Id,
                decision,
                comment,
                $"chief-resource-change:{request.Id:N}:{decision}"),
            cancellationToken);
    }

    private async Task HandleResourceChangeDecidedAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var resourceEvent = DeserializePayload<ResourceChangeDecisionEvent>(message.Payload);
        if (resourceEvent is null ||
            !string.Equals(resourceEvent.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            return;
        var read = await context.Platform.ReadResourceChangesAsync(
            new ResourceChangeReadRequest(resourceEvent.RequestId),
            cancellationToken);
        var request = read.Requests.SingleOrDefault(x =>
            x.Id == resourceEvent.RequestId &&
            x.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase));
        if (request is null)
        {
            throw new InvalidOperationException(
                $"Approved resource change {resourceEvent.RequestId:D} was delivered to the Chief of Staff but is not visible through its granted organization resource-change capability.");
        }

        await ReconcileApprovedResourceChangeAsync(request, context, cancellationToken);
    }

    private async Task ReconcileApprovedResourceChangeAsync(
        ResourceChangeRequestResponse request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        await RequireActiveChiefAsync(context, cancellationToken);
        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        var managerChat = await FindManagerChatAsync(context, cancellationToken);
        var actionableRecommendations =
            new List<(ResourceChangeRoleDelta Delta, HiringRecommendationResponse Recommendation)>();
        foreach (var delta in request.Deltas.OrderBy(x => x.Role.Priority))
        {
            var stableRoleKey = delta.Role.RoleKey;
            if (delta.ChangeKind.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var existing in backlog.Recommendations.Where(x =>
                             string.Equals(x.RoleKey, stableRoleKey, StringComparison.Ordinal)))
                {
                    _ = await context.Platform.WithdrawHiringRecommendationAsync(
                        new WithdrawHiringRecommendationRequest(
                            existing.Id,
                            $"Removed by approved resource-change request {request.Id:D}.",
                            $"resource-change:{request.Id:N}:withdraw:{NormalizeKey(delta.Role.RoleKey)}"),
                        cancellationToken);
                }
                continue;
            }

            if (delta.ChangeKind is not ("Add" or "Increase"))
                continue;
            var requestedHeadcount = delta.ChangeKind == "Increase"
                ? delta.Role.Headcount - (delta.PreviousRole?.Headcount ?? 0)
                : delta.Role.Headcount;
            if (requestedHeadcount <= 0)
                continue;

            var recommendation = await context.Platform.UpsertHiringRecommendationAsync(
                new UpsertHiringRecommendationRequest(
                    delta.Role.Title,
                    delta.Role.Purpose,
                    null,
                    [],
                    null,
                    $"resource-change:{request.Id:N}:role:{NormalizeKey(delta.Role.RoleKey)}")
                {
                    Priority = Math.Max(1, delta.Role.Priority),
                    RoleKey = stableRoleKey,
                    Headcount = requestedHeadcount,
                    SourceResourceChangeRequestId = request.Id,
                    TeamId = request.TeamId
                },
                cancellationToken);

            actionableRecommendations.Add((delta, recommendation));
        }

        await SyncHiringPersonalTodosAsync(context, null, null, cancellationToken);

        if (request.Deltas.Count == 0) return;
        var messageId = await SendCommunicationMessageAsync(
            managerChat.Id,
            BuildResourceChangeManagerBrief(request),
            $"resource-change:{request.Id:N}:manager-brief",
            context,
            cancellationToken);
        foreach (var actionable in actionableRecommendations
                     .OrderBy(x => x.Delta.Role.Priority)
                     .ThenBy(x => x.Delta.Role.Title, StringComparer.Ordinal))
        {
            await SuggestMarketplaceActionAsync(
                messageId,
                actionable.Delta.Role.Title,
                actionable.Recommendation.Id,
                $"resource-change:{request.Id:N}:action:{actionable.Recommendation.Id:N}",
                context,
                cancellationToken);
        }
    }

    internal static string BuildResourceChangeManagerBrief(ResourceChangeRequestResponse request)
    {
        var content = new StringBuilder();
        content.Append("CEO-approved, Product Manager-authored staffing update for **")
            .Append(request.ProductGoal)
            .AppendLine("**")
            .AppendLine();
        foreach (var delta in request.Deltas
                     .OrderBy(x => x.Role.Priority)
                     .ThenBy(x => x.Role.Title, StringComparer.Ordinal))
        {
            content.Append("- **")
                .Append(delta.ChangeKind)
                .Append(": ")
                .Append(delta.Role.Title)
                .Append("** — priority ")
                .Append(delta.Role.Priority)
                .Append(", headcount ")
                .Append(delta.Role.Headcount)
                .Append(", ")
                .Append(delta.Role.Timing)
                .Append(". ")
                .AppendLine(delta.Role.Purpose);
        }

        content.AppendLine()
            .Append("Added and increased roles are now candidate-free hiring suggestions administered by the Chief on behalf of the Product Manager. ")
            .Append("Marketplace review, spending, installation, and each hire remain separately approved.");
        return content.ToString();
    }

    private async Task<(Guid InstallationId, OrganizationPerson Self, OrganizationSnapshotResponse Organization)>
        RequireActiveChiefAsync(
            AgentRuntimeContext context,
            CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.InstallationId, out var installationId) ||
            !Guid.TryParse(context.Identity?.EmployeeId, out var employeeId))
            throw new InvalidOperationException("The Chief of Staff identity is unavailable.");
        var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
        var organization = operatingContext.Organization
            ?? throw new InvalidOperationException("The organization snapshot is unavailable.");
        var self = organization.People.SingleOrDefault(x =>
            x.Id == employeeId &&
            x.AgentInstallationId == installationId &&
            x.IsActive)
            ?? throw new InvalidOperationException("This installation is not the active Chief of Staff.");
        return (installationId, self, organization);
    }

    private async Task<CommunicationChatResponse> FindManagerChatAsync(
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var (_, self, organization) = await RequireActiveChiefAsync(context, cancellationToken);
        var manager = self.ReportsToId.HasValue
            ? organization.People.SingleOrDefault(x => x.Id == self.ReportsToId.Value && x.IsActive)
            : null;
        if (manager is null)
            throw new InvalidOperationException("The Chief of Staff has no active manager.");
        var hub = await context.Platform.InvokeAsync<JsonElement, CommunicationHubResponse>(
            ChiefOfStaffProfile.ReadCommunicationCapability,
            JsonSerializer.Deserialize<JsonElement>("{}"),
            cancellationToken);
        var existing = hub.Chats
            .Where(x => x.IsDirect &&
                        x.Participants.Any(p => p.OrganizationUserId == self.Id) &&
                        x.Participants.Any(p => p.OrganizationUserId == manager.Id))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefault();
        if (existing is not null) return existing;
        var created = await context.Platform.InvokeAsync<CreateCommunicationChatRequest, CommunicationHubActionResponse>(
            ChiefOfStaffProfile.CreateCommunicationCapability,
            new CreateCommunicationChatRequest(
                null,
                "Private Chief of Staff manager conversation.",
                true,
                true,
                [manager.Id]),
            cancellationToken);
        return created.Succeeded && created.Chat is not null
            ? created.Chat
            : throw new InvalidOperationException($"The Chief could not open its manager chat: {created.Message}");
    }

    private async Task HandleRecommendationFulfilledAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var fulfilled = DeserializePayload<HiringRecommendationFulfilledEvent>(message.Payload);
        if (fulfilled is null ||
            fulfilled.OrganizationId == Guid.Empty ||
            fulfilled.RecommendationId == Guid.Empty ||
            fulfilled.RequestingInstallationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(fulfilled.RoleTitle) ||
            fulfilled.FulfilledHeadcount < fulfilled.RequestedHeadcount ||
            !Guid.TryParse(context.InstallationId, out var installationId) ||
            fulfilled.RequestingInstallationId != installationId ||
            !string.Equals(context.BusinessId, fulfilled.OrganizationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignored unrelated or malformed hiring recommendation fulfillment event {EventId}.", message.EventId);
            return;
        }

        await ResumeFulfilledHiringTodoAsync(
            fulfilled.RecommendationId, context, cancellationToken);

        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        var next = backlog.Recommendations
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefault();
        var ownerChat = await FindOwnerChatAsync(context, cancellationToken);
        var content = next is null
            ? $"The {fulfilled.RoleTitle} recommendation is fulfilled ({fulfilled.FulfilledHeadcount}/{fulfilled.RequestedHeadcount}). Your current hiring backlog is complete."
            : $"The {fulfilled.RoleTitle} recommendation is fulfilled ({fulfilled.FulfilledHeadcount}/{fulfilled.RequestedHeadcount}). The next priority is **{next.Title}**: {next.Objective}";
        var sentMessageId = await SendCommunicationMessageAsync(
            ownerChat.Id,
            content,
            $"hiring-recommendation-fulfilled:{message.EventId}:next",
            context,
            cancellationToken);
        if (next is not null)
        {
            await SuggestMarketplaceActionAsync(
                sentMessageId,
                next.Title,
                next.Id,
                $"hiring-recommendation-fulfilled:{message.EventId}:action:{next.Id:N}",
                context,
                cancellationToken);
        }
    }

    private async Task EnsureDefaultProductManagerRecommendationAsync(
        string response,
        string idempotencyPrefix,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var recommendsProductManager = SplitResponseSegments(response).Any(segment =>
            segment.Contains("Product Manager", StringComparison.OrdinalIgnoreCase) &&
            IsHiringRecommendationLine(segment));
        if (!recommendsProductManager) return;

        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        if (backlog.Recommendations.Any(x =>
                NormalizeRoleIdentity(x.Title) == "productmanager"))
            return;

        _ = await context.Platform.UpsertHiringRecommendationAsync(
            new UpsertHiringRecommendationRequest(
                "Product Manager",
                "Own customer discovery, product outcomes, strategy, roadmap, prioritization, requirements, and product-team design.",
                null,
                [],
                null,
                $"{idempotencyPrefix}:recommendation:product-manager")
            {
                Priority = 1,
                RoleKey = "product-manager",
                Headcount = 1
            },
            cancellationToken);
    }

    private async Task SyncHiringPersonalTodosAsync(
        AgentRuntimeContext context,
        Guid? sourceConversationId,
        Guid? sourceMessageId,
        CancellationToken cancellationToken)
    {
        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        if (backlog.Recommendations.Count == 0) return;
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId))
            throw new InvalidOperationException("The Chief of Staff employee identity is unavailable.");

        var directory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var board = directory.Boards.SingleOrDefault(x => x.OwnerOrganizationUserId == chiefId);
        var items = board?.Items ?? [];
        var active = items.Any(x =>
            TryGetHiringRecommendationId(x.CorrelationId, out _) &&
            x.Status is PersonalTodoStatuses.Ready or PersonalTodoStatuses.Running or
                PersonalTodoStatuses.Blocked);

        foreach (var recommendation in backlog.Recommendations
                     .OrderBy(x => x.Priority)
                     .ThenBy(x => x.CreatedAt))
        {
            var correlationId = HiringTodoCorrelationId(recommendation.Id);
            var existing = items.SingleOrDefault(x =>
                string.Equals(x.CorrelationId, correlationId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Status == PersonalTodoStatuses.Blocked)
                {
                    _ = await context.Platform.PersonalTodo.RequeueAsync(
                        new RequeuePersonalTodoItemRequest(
                            existing.Id,
                            existing.Revision,
                            $"migrate-hiring-recommendation-to-doing:{recommendation.Id:N}"),
                        cancellationToken);
                    active = true;
                    continue;
                }
                if (!active && existing.Status == PersonalTodoStatuses.Backlog)
                {
                    _ = await context.Platform.PersonalTodo.ActivateAsync(
                        new ActivatePersonalTodoItemRequest(
                            existing.Id,
                            existing.Revision,
                            $"activate-hiring-recommendation:{recommendation.Id:N}"),
                        cancellationToken);
                    active = true;
                }
                continue;
            }

            var request = BuildHiringTodoRequest(
                recommendation,
                startInBacklog: active,
                sourceConversationId,
                sourceMessageId);
            _ = await context.Platform.PersonalTodo.AddAsync(request, cancellationToken);
            if (!request.StartInBacklog) active = true;
        }
    }

    private async Task ResumeFulfilledHiringTodoAsync(
        Guid recommendationId,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId)) return;
        var directory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var item = directory.Boards
            .SingleOrDefault(x => x.OwnerOrganizationUserId == chiefId)?
            .Items.SingleOrDefault(x => string.Equals(
                x.CorrelationId,
                HiringTodoCorrelationId(recommendationId),
                StringComparison.Ordinal));
        if (item is null) return;

        if (item.Status is PersonalTodoStatuses.Blocked or PersonalTodoStatuses.Running)
        {
            _ = await context.Platform.PersonalTodo.RequeueAsync(
                new RequeuePersonalTodoItemRequest(
                    item.Id,
                    item.Revision,
                    $"resolve-hiring-recommendation:{recommendationId:N}"),
                cancellationToken);
        }
        else if (item.Status == PersonalTodoStatuses.Backlog)
        {
            _ = await context.Platform.PersonalTodo.ActivateAsync(
                new ActivatePersonalTodoItemRequest(
                    item.Id,
                    item.Revision,
                    $"resolve-hiring-recommendation:{recommendationId:N}"),
                cancellationToken);
        }
    }

    private static async Task ActivateNextHiringTodoAsync(
        HiringBacklogResponse backlog,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId)) return;
        var activeRecommendationIds = backlog.Recommendations.Select(x => x.Id).ToHashSet();
        var directory = await context.Platform.PersonalTodo.ListAsync(cancellationToken);
        var next = directory.Boards
            .SingleOrDefault(x => x.OwnerOrganizationUserId == chiefId)?
            .Items.Where(x => x.Status == PersonalTodoStatuses.Backlog &&
                TryGetHiringRecommendationId(x.CorrelationId, out var id) &&
                activeRecommendationIds.Contains(id))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (next is null) return;

        _ = await context.Platform.PersonalTodo.ActivateAsync(
            new ActivatePersonalTodoItemRequest(
                next.Id,
                next.Revision,
                $"activate-next-hiring-todo:{next.Id:N}"),
            cancellationToken);
    }

    internal static AddPersonalTodoItemRequest BuildHiringTodoRequest(
        HiringRecommendationResponse recommendation,
        bool startInBacklog,
        Guid? sourceConversationId = null,
        Guid? sourceMessageId = null)
    {
        if (sourceConversationId.HasValue != sourceMessageId.HasValue)
        {
            sourceConversationId = null;
            sourceMessageId = null;
        }

        return new AddPersonalTodoItemRequest(
            $"Hire {recommendation.Title}",
            $"Advance hiring recommendation {recommendation.Id:D}. Objective: {recommendation.Objective}",
            recommendation.Priority == 1 ? WorkPriorities.High : WorkPriorities.Medium,
            null,
            $"hiring-recommendation:{recommendation.Id:N}:personal-todo",
            null,
            sourceConversationId,
            sourceMessageId,
            HiringTodoCorrelationId(recommendation.Id))
        {
            StartInBacklog = startInBacklog
        };
    }

    internal static string HiringTodoCorrelationId(Guid recommendationId) =>
        $"hiring-recommendation:{recommendationId:N}";

    internal static bool TryGetHiringRecommendationId(string? correlationId, out Guid recommendationId)
    {
        const string prefix = "hiring-recommendation:";
        recommendationId = Guid.Empty;
        return correlationId?.StartsWith(prefix, StringComparison.Ordinal) == true &&
               Guid.TryParseExact(correlationId[prefix.Length..], "N", out recommendationId);
    }

    internal async Task AttachTopHiringActionAsync(
        Guid messageId,
        string idempotencyPrefix,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
            var next = backlog.Recommendations
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.CreatedAt)
                .FirstOrDefault();
            if (next is null) return;
            await SuggestMarketplaceActionAsync(
                messageId,
                next.Title,
                next.Id,
                $"{idempotencyPrefix}:action:{next.Id:N}",
                context,
                cancellationToken);
        }
        catch (PlatformCapabilityException exception)
        {
            // The greeting and onboarding acknowledgement are the durable primary effects.
            // A convenience CTA must never make that completed onboarding work fail.
            _logger.LogWarning(
                exception,
                "Chief of Staff could not attach the optional hiring action to onboarding message {MessageId}.",
                messageId);
        }
    }

    private async Task AttachMentionedHiringActionAsync(
        Guid chatTurnId,
        string response,
        string idempotencyPrefix,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (chatTurnId == Guid.Empty || string.IsNullOrWhiteSpace(response)) return;
        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        var next = backlog.Recommendations
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (next is null ||
            !response.Contains(next.Title, StringComparison.OrdinalIgnoreCase))
            return;

        _ = await context.Platform.SuggestUserActionAsync(
            new SuggestUserActionRequest(
                null,
                chatTurnId,
                ChiefOfStaffProfile.HiringMarketplaceBrowseWorkflow,
                "Browse candidates",
                $"Review Marketplace candidates for the {next.Title} role.",
                JsonSerializer.SerializeToElement(new { role = next.Title, recommendationId = next.Id }),
                $"{idempotencyPrefix}:action:{next.Id:N}"),
            cancellationToken);
    }

    private static async Task SuggestMarketplaceActionAsync(
        Guid messageId,
        string role,
        Guid recommendationId,
        string idempotencyKey,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        _ = await context.Platform.SuggestUserActionAsync(
            new SuggestUserActionRequest(
                messageId,
                null,
                ChiefOfStaffProfile.HiringMarketplaceBrowseWorkflow,
                "Browse candidates",
                $"Review Marketplace candidates for the {role} role.",
                JsonSerializer.SerializeToElement(new { role, recommendationId }),
                idempotencyKey),
            cancellationToken);
    }

    private static async Task<Guid> SendCommunicationMessageAsync(
        Guid chatId,
        string content,
        string idempotencyKey,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var response = await context.Platform.InvokeAsync<SendCommunicationMessageRequest, JsonElement>(
            ChiefOfStaffProfile.SendCommunicationMessageCapability,
            new SendCommunicationMessageRequest(chatId, content, idempotencyKey),
            cancellationToken);
        if (response.TryGetProperty("id", out var id) && id.TryGetGuid(out var messageId))
            return messageId;
        throw new InvalidOperationException("The communication service did not return the created message identity.");
    }

    private static async Task<CommunicationChatResponse> FindOwnerChatAsync(
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId))
            throw new InvalidOperationException("The Chief employee identity is unavailable.");
        var hub = await context.Platform.InvokeAsync<JsonElement, CommunicationHubResponse>(
            ChiefOfStaffProfile.ReadCommunicationCapability,
            JsonSerializer.Deserialize<JsonElement>("{}"),
            cancellationToken);
        return hub.Chats
            .Where(x => x.IsDirect &&
                        x.Participants.Any(p => p.OrganizationUserId == chiefId) &&
                        x.Participants.Any(p => p.EmployeeType.Equals("Human", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.IsDeletionProtected)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The Chief has no protected owner conversation.");
    }

    internal static string NormalizeRoleIdentity(string value)
    {
        var cleaned = value.Trim();
        if (cleaned.EndsWith("(Agent)", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^"(Agent)".Length].TrimEnd();
        return new string(cleaned
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    internal static string FormatOnboardingMessage(string value)
    {
        var lines = EnforceResponseMode(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        if (lines.Count == 0) return string.Empty;

        var containsRecommendation = lines.Any(IsHiringRecommendationLine);
        var sections = new List<string>(lines.Count + 2);
        foreach (var line in lines)
        {
            if (containsRecommendation && line.EndsWith("?", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("Role Map:", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add($"- **Role map:** {line["Role Map:".Length..].Trim()}");
                continue;
            }
            if (line.StartsWith("Priority 1 Hire:", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add($"- **Priority 1 hire:** {line["Priority 1 Hire:".Length..].Trim()}");
                continue;
            }
            if (line.EndsWith("?", StringComparison.Ordinal))
            {
                sections.Add($"**Question for you**\n\n{line}");
                continue;
            }

            sections.Add(line);
        }

        return string.Join("\n\n", sections);
    }

    private static bool IsHiringRecommendationLine(string line)
    {
        if (line.TrimEnd().EndsWith("?", StringComparison.Ordinal) ||
            line.Contains("cannot recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("can't recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unable to recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("before I recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("before I can recommend", StringComparison.OrdinalIgnoreCase))
            return false;

        return
            line.StartsWith("Priority 1 Hire:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("priority-one hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("priority 1 hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("first hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("highest priority is to hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("should hire", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("recommend a ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("recommend an ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("I recommend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("hiring backlog", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("browse candidates", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("browse marketplace", StringComparison.OrdinalIgnoreCase);
    }

    internal static string EnforceResponseMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (!SplitResponseSegments(normalized).Any(IsHiringRecommendationLine)) return value;

        var retained = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("Question for you", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = System.Text.RegularExpressions.Regex.Split(
                line,
                @"(?<=[.!?;:])\s+(?=(?:Who|What|When|Where|Why|How|Which|Do|Does|Did|Is|Are|Can|Could|Would|Should|Will)\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var statements = parts
                .Where(part => !part.TrimEnd().EndsWith("?", StringComparison.Ordinal))
                .ToList();
            if (statements.Count > 0)
                retained.Add(string.Join(" ", statements));
        }

        return string.Join("\n", retained).Trim();
    }

    private static IEnumerable<string> SplitResponseSegments(string value) =>
        System.Text.RegularExpressions.Regex.Split(
            value,
            @"\n|(?<=[.!?;:])\s+(?=(?:Who|What|When|Where|Why|How|Which|Do|Does|Did|Is|Are|Can|Could|Would|Should|Will)\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static Task PublishChunkAsync(
        AgentRuntimeContext context,
        Guid eventId,
        AssistantResponseChunk chunk,
        CancellationToken cancellationToken)
    {
        _ = eventId;
        return context.ReportProgressAsync(chunk, cancellationToken);
    }

    private static Task PublishAgentErrorAsync(
        AgentRuntimeContext context,
        Guid eventId,
        string conversationId,
        int sequence,
        string message,
        Guid turnId,
        int attempt,
        CancellationToken cancellationToken)
    {
        return PublishChunkAsync(context, eventId, new AssistantResponseChunk(
            conversationId,
            sequence,
            message,
            IsFinal: true,
            Error: "agent_error",
            TurnId: turnId,
            Kind: "error",
            Attempt: attempt), cancellationToken);
    }

    private static string BuildSafeFailureMessage(Exception exception)
    {
        var candidates = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];

        var httpException = candidates
            .SelectMany(EnumerateExceptionChain)
            .OfType<HttpRequestException>()
            .FirstOrDefault();

        if (httpException is not null)
        {
            return $"The model provider could not be reached: {httpException.Message}";
        }

        return "The Chief of Staff could not complete the request. Check the Chief of Staff logs for details.";
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private async IAsyncEnumerable<AssistantStreamUpdate> StreamAssistantDeltasAsync(
        AssistantCapabilityInput input,
        string capability,
        AgentRuntimeContext runtimeContext,
        ChiefOperatingContext? operatingContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Chief of Staff resolving chat client for provider {ProviderProfileId} and conversation {ConversationId}.",
            input.ProviderProfileId,
            input.ConversationId);

        operatingContext ??= await _orchestrator.AssembleContextAsync(runtimeContext, cancellationToken);
        var conversationId = Guid.TryParse(input.ConversationId, out var parsedConversationId)
            ? parsedConversationId
            : (Guid?)null;
        var invocation = new AgentLlmInvocationContext(
            conversationId,
            input.ChatTurnId == Guid.Empty ? null : input.ChatTurnId,
            "primary");
        var selection = new AgentLlmSelection(
            input.ProviderProfileId,
            Settings.GetString("llmModel"),
            invocation);
        var extractionSelection = selection with
        {
            Invocation = invocation with { InvocationKind = "business-fact-extraction" }
        };
        var extractionChatClient = _llmClientFactory is null
            ? new PlatformChatClient(runtimeContext.Platform, extractionSelection)
            : await _llmClientFactory.CreateChatClientAsync(extractionSelection, cancellationToken);
        await _orchestrator.CaptureExplicitFactsAsync(
            extractionChatClient, input, operatingContext, runtimeContext, cancellationToken);
        var chatClient = _llmClientFactory is null
            ? new PlatformChatClient(runtimeContext.Platform, selection)
            : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);

        _logger.LogInformation(
            "Chief of Staff created chat client for provider {ProviderProfileId} and conversation {ConversationId}.",
            input.ProviderProfileId,
            input.ConversationId);

        var memoryOptions = Options.Create(new AgentMemoryOptions
        {
            DefaultScope = MemoryScope.User,
            ContextTokenBudget = 2_000,
            StoreAssistantMessages = true,
            FailOpen = true
        });
        var memoryStore = new ChiefPlatformMemoryStore(runtimeContext.Platform);
        var memoryEngine = new MemoryEngine(
            memoryStore,
            memoryOptions,
            authorizer: new DelegatedMemoryScopeAuthorizer(),
            namespaceResolver: new WorkContextMemoryNamespaceResolver());
        var memoryProvider = new AgentMemoryContextProvider(
            memoryEngine,
            new SessionStateMemoryPartitionResolver(memoryOptions),
            memoryOptions);

        var tools = (await runtimeContext.GetModelToolsAsync(cancellationToken))
            .Where(tool => tool is not AIFunctionDeclaration function ||
                           IsModelToolAvailable(input, function.Name))
            .ToList();
        if (tools.Any(tool => tool is AIFunctionDeclaration function &&
                            function.Name == "product_management_plan"))
        {
            tools.Add(AIFunctionFactory.Create(
                (string focus, CancellationToken token) => ConsultProductManagerAsync(
                    focus,
                    input,
                    operatingContext,
                    runtimeContext,
                    token),
                "consult_product_manager",
                "Consult the active Product Manager sharing this Chief's CEO manager for product strategy, discovery, roadmap, requirements, priorities, or product-team design."));
        }

        var useAgentMemory = input.ChatTurnId == Guid.Empty;
        AIAgent agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = ChiefOfStaffProfile.AgentId,
                Name = runtimeContext.Identity?.DisplayName ?? ChiefOfStaffProfile.DefaultDisplayName,
                ChatOptions = new ChatOptions
                {
                    Instructions = ChiefOfStaffProfile.SystemPrompt,
                    Tools = tools,
                    Reasoning = new ReasoningOptions
                    {
                        Output = ReasoningOutput.Full
                    }
                },
                AIContextProviders = useAgentMemory ? [memoryProvider] : []
            });

        var prompt = _orchestrator.BuildGroundedPrompt(input.Prompt, capability, operatingContext, Settings);

        AgentSession session = await agent.CreateSessionAsync(cancellationToken);
        if (useAgentMemory)
        {
            session.ConfigureMemory(
                new MemoryPartition(
                    runtimeContext.BusinessId,
                    runtimeContext.InstallationId,
                    ChiefOfStaffProfile.AgentId,
                    input.UserId ?? ResolveUserId(input.Context),
                    input.ConversationId),
                MemoryScope.User,
                new MemoryPrincipal(
                    runtimeContext.BusinessId,
                    ChiefOfStaffProfile.AgentId,
                    ChiefOfStaffProfile.AgentId,
                    runtimeContext.InstallationId,
                    Attributes: new Dictionary<string, string>
                    {
                        ["memory.maxSensitivity"] = MemorySensitivity.Personal.ToString()
                    }));
        }

        _logger.LogInformation(
            "Chief of Staff starting MAF streaming for conversation {ConversationId}. Capability {Capability}. PromptLength {PromptLength}.",
            input.ConversationId,
            capability,
            prompt.Length);

        var modelActivities = new Dictionary<string, (string Name, System.Diagnostics.Stopwatch Stopwatch)>(StringComparer.Ordinal);
        await foreach (var update in agent.RunStreamingAsync(prompt, session, options: null, cancellationToken))
        {
            var usage = ExtractUsage(update.Contents);
            var reasoningDelta = string.Concat(
                update.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
            var startsNewDraft = update.Contents.Any(content => content is FunctionCallContent);
            var activities = new List<AssistantActivityUpdate>();
            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                modelActivities[call.CallId] = (call.Name, System.Diagnostics.Stopwatch.StartNew());
                activities.Add(new AssistantActivityUpdate(
                    AgentTurnStreamKinds.ActivityStarted,
                    $"Calling {call.Name}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tool"] = call.Name,
                        ["callId"] = call.CallId,
                        ["input"] = JsonSerializer.Serialize(call.Arguments)
                    }));
            }
            foreach (var result in update.Contents.OfType<FunctionResultContent>())
            {
                var activity = modelActivities.Remove(result.CallId, out var started)
                    ? started
                    : ("model tool", System.Diagnostics.Stopwatch.StartNew());
                activities.Add(new AssistantActivityUpdate(
                    AgentTurnStreamKinds.ActivityCompleted,
                    $"Completed {activity.Item1}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tool"] = activity.Item1,
                        ["callId"] = result.CallId,
                        ["durationMs"] = activity.Item2.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["output"] = JsonSerializer.Serialize(result.Result)
                    }));
            }
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new AssistantStreamUpdate(update.Text, reasoningDelta, usage, startsNewDraft, activities);
            }
            else if (usage is not null || !string.IsNullOrEmpty(reasoningDelta) || startsNewDraft || activities.Count > 0)
            {
                yield return new AssistantStreamUpdate(string.Empty, reasoningDelta, usage, startsNewDraft, activities);
            }
        }
    }

    private static string? ResolveUserId(IReadOnlyDictionary<string, string>? context) =>
        context is not null && context.TryGetValue("userId", out var userId) && !string.IsNullOrWhiteSpace(userId)
            ? userId
            : null;

    private async Task<AssistantResponseCreated> GenerateResponseAsync(
        AssistantCapabilityInput input,
        string capability,
        AgentRuntimeContext runtimeContext,
        CancellationToken cancellationToken,
        ChiefOperatingContext? operatingContext = null)
    {
        var builder = new System.Text.StringBuilder();

        await foreach (var update in StreamAssistantDeltasAsync(
            input,
            capability,
            runtimeContext,
            operatingContext,
            cancellationToken))
        {
            builder.Append(update.Delta);
        }

        return new AssistantResponseCreated(
            input.ConversationId,
            EnforceResponseMode(builder.ToString()),
            ProposedActions: [],
            DateTimeOffset.UtcNow);
    }

    internal static bool IsModelToolAvailable(AssistantCapabilityInput _, string toolName) =>
        !string.Equals(toolName, "add_personal_todo", StringComparison.Ordinal) &&
        !string.Equals(toolName, "suggest_user_action", StringComparison.Ordinal);

    internal static async Task<ProductPlanResponse> ConsultProductManagerAsync(
        string focus,
        AssistantCapabilityInput input,
        ChiefOperatingContext operatingContext,
        AgentRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runtimeContext.Identity?.EmployeeId, out var chiefId))
            throw new InvalidOperationException("The Chief employee identity is unavailable.");
        var organization = operatingContext.Organization
            ?? throw new InvalidOperationException("The organization snapshot is unavailable.");
        var chief = organization.People.SingleOrDefault(person =>
            person.Id == chiefId &&
            person.IsActive)
            ?? throw new InvalidOperationException("The Chief of Staff is not active in the organization snapshot.");
        var productManager = organization.People
            .Where(person => IsProductManagerLiaison(chief, person, organization))
            .OrderBy(x => x.DisplayName)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No active Product Manager shares this Chief of Staff's CEO manager.");
        var brief = ChiefOfStaffOrchestrator.BuildProductRoleBrief(
            operatingContext,
            chiefId,
            productManager.Id);
        var sourceId = input.MessageId != Guid.Empty ? input.MessageId : Guid.NewGuid();
        var request = new ProductPlanRequest(
            brief,
            string.IsNullOrWhiteSpace(focus) ? "Provide a product recommendation for the current executive request." : focus.Trim(),
            sourceId,
            $"chief-product-consult:{productManager.Id:D}:{sourceId:D}");
        _ = productManager.AgentInstallationId;
        _ = sourceId;
        return await runtimeContext.Platform.InvokeAsync<ProductPlanRequest, ProductPlanResponse>(
            ProductManagementCapabilities.Plan,
            request,
            cancellationToken);
    }

    private static bool IsSupportedCapability(string capability) =>
        capability is ChiefOfStaffProfile.ConverseCapability or
            ChiefOfStaffProfile.SummarizeActivityCapability or
            ChiefOfStaffProfile.PlanWorkCapability or
            ChiefOfStaffProfile.ManagementCheckInCapability or
            ProductManagementCapabilities.RoleBrief or
            ProductManagementCapabilities.PlanReview or
            ProductManagementCapabilities.Escalation;

    private static bool IsAuthorizedProductManagerRequest(
        string requestingAgentId,
        ProductRoleBriefRequest request,
        AgentRuntimeContext runtimeContext,
        ChiefOperatingContext operatingContext) =>
        IsAuthorizedProductManagerRequest(
            requestingAgentId,
            request.ProductManagerOrganizationUserId,
            request.ProductManagerInstallationId,
            runtimeContext,
            operatingContext);

    private static bool IsAuthorizedProductManagerRequest(
        string requestingAgentId,
        ProductPlanReviewRequest request,
        AgentRuntimeContext runtimeContext,
        ChiefOperatingContext operatingContext) =>
        IsAuthorizedProductManagerRequest(
            requestingAgentId,
            request.ProductManagerOrganizationUserId,
            request.ProductManagerInstallationId,
            runtimeContext,
            operatingContext);

    private static bool IsAuthorizedProductManagerRequest(
        string requestingAgentId,
        ProductEscalationRequest request,
        AgentRuntimeContext runtimeContext,
        ChiefOperatingContext operatingContext) =>
        IsAuthorizedProductManagerRequest(
            requestingAgentId,
            request.ProductManagerOrganizationUserId,
            request.ProductManagerInstallationId,
            runtimeContext,
            operatingContext);

    private static bool IsAuthorizedProductManagerRequest(
        string requestingAgentId,
        Guid productManagerId,
        Guid productManagerInstallationId,
        AgentRuntimeContext runtimeContext,
        ChiefOperatingContext operatingContext)
    {
        if (!string.Equals(requestingAgentId, "com.csweet.product-manager", StringComparison.Ordinal) ||
            !Guid.TryParse(runtimeContext.Identity?.EmployeeId, out var chiefId))
            return false;
        var organization = operatingContext.Organization;
        var chief = organization?.People.SingleOrDefault(x =>
            x.Id == chiefId &&
            x.IsActive);
        var productManager = operatingContext.Organization?.People.SingleOrDefault(x =>
            x.Id == productManagerId &&
            x.IsActive &&
            x.EmployeeType.Equals("Agent", StringComparison.OrdinalIgnoreCase) &&
            x.AgentInstallationId == productManagerInstallationId);
        return chief is not null &&
               productManager is not null &&
               organization is not null &&
               IsProductManagerLiaison(chief, productManager, organization);
    }

    internal static bool IsProductManagerLiaison(
        OrganizationPerson chief,
        OrganizationPerson candidate,
        OrganizationSnapshotResponse organization)
    {
        if (!chief.IsActive ||
            chief.ReportsToId is not { } ceoId ||
            candidate.Id == chief.Id ||
            !candidate.IsActive ||
            candidate.ReportsToId != ceoId ||
            candidate.AgentInstallationId is null ||
            !candidate.EmployeeType.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            return false;

        var ceo = organization.People.SingleOrDefault(person =>
            person.Id == ceoId &&
            person.IsActive &&
            person.EmployeeType.Equals("Human", StringComparison.OrdinalIgnoreCase));
        if (ceo is null) return false;

        var roleName = candidate.RoleId.HasValue
            ? organization.Roles.SingleOrDefault(role => role.Id == candidate.RoleId.Value)?.Name
            : null;
        return (roleName?.Contains("Product Manager", StringComparison.OrdinalIgnoreCase) ?? false) ||
               candidate.DisplayName.Contains("Product Manager", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProductEscalationResponse> EscalateProductDecisionToOwnerAsync(
        ProductEscalationRequest escalation,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId))
            throw new InvalidOperationException("The Chief employee identity is unavailable.");
        var hub = await context.Platform.InvokeAsync<JsonElement, CommunicationHubResponse>(
            ChiefOfStaffProfile.ReadCommunicationCapability,
            JsonSerializer.Deserialize<JsonElement>("{}"),
            cancellationToken);
        var ownerChat = hub.Chats
            .Where(x => x.IsDirect &&
                        x.Participants.Any(p => p.OrganizationUserId == chiefId) &&
                        x.Participants.Any(p => p.EmployeeType.Equals("Human", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.IsDeletionProtected)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The Chief has no protected owner conversation.");

        var content = new System.Text.StringBuilder();
        content.Append("The Product Manager needs one executive answer: ").Append(escalation.Question);
        if (!string.IsNullOrWhiteSpace(escalation.WhyItMatters))
            content.Append("\n\nWhy it matters: ").Append(escalation.WhyItMatters);
        if (escalation.Options.Count > 0)
        {
            content.Append("\n\nOptions: ").Append(string.Join("; ", escalation.Options.Take(2)));
            if (!string.IsNullOrWhiteSpace(escalation.RecommendedOption))
                content.Append("\n\nRecommended: ").Append(escalation.RecommendedOption);
        }
        _ = await context.Platform.InvokeAsync<SendCommunicationMessageRequest, JsonElement>(
            ChiefOfStaffProfile.SendCommunicationMessageCapability,
            new SendCommunicationMessageRequest(
                ownerChat.Id,
                content.ToString(),
                escalation.IdempotencyKey),
            cancellationToken);
        return new ProductEscalationResponse(
            true,
            "Delivered",
            "The Chief sent the Product Manager's highest-value question to the CEO.",
            DateTimeOffset.UtcNow);
    }

    private async Task PushProductManagerContextUpdatesAsync(
        Guid sourceEventId,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Identity?.EmployeeId, out var chiefId)) return;
        var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
        var organization = operatingContext.Organization;
        if (organization is null) return;
        var chief = organization.People.SingleOrDefault(person =>
            person.Id == chiefId &&
            person.IsActive);
        if (chief is null) return;
        var sourceId = sourceEventId;
        var productManagers = organization.People
            .Where(person => IsProductManagerLiaison(chief, person, organization))
            .ToList();

        foreach (var productManager in productManagers)
        {
            if (productManager.AgentInstallationId is not { } agentInstallationId)
                continue;
            var brief = ChiefOfStaffOrchestrator.BuildProductRoleBrief(
                operatingContext,
                chiefId,
                productManager.Id);
            _ = sourceEventId;
            var update = await context.Platform.InvokeAsync<ProductContextUpdateRequest, ProductContextUpdateResponse>(
                ProductManagementCapabilities.ContextUpdate,
                new ProductContextUpdateRequest(
                    brief,
                    sourceId,
                    $"product-context:{productManager.Id:D}:{sourceId:D}:{brief.ContextRevision}"),
                cancellationToken);
            if (!update.PlanRefreshRequired) continue;
            var plan = await context.Platform.InvokeAsync<ProductPlanRequest, ProductPlanResponse>(
                ProductManagementCapabilities.Plan,
                new ProductPlanRequest(
                    brief,
                    "Refresh the product strategy, roadmap themes, product-team structure, and hiring sequence after this authoritative context change.",
                    sourceId,
                    $"product-refresh-plan:{productManager.Id:D}:{sourceId:D}:{brief.ContextRevision}"),
                cancellationToken);
        }
    }

    private static string NormalizeKey(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(x => char.IsLetterOrDigit(x) ? x : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task HandleManagementReviewAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var due = DeserializePayload<ManagementReviewDueEvent>(message.Payload);
        if (due is null) { _logger.LogWarning("Ignored malformed management review event {EventId}.", message.EventId); return; }
        var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
        var checkIn = new ManagementCheckInRequest(due.CycleId, due.ReviewType, due.PeriodStart, due.PeriodEnd, [],
            ["outcomes", "blockers", "staffing", "budget", "decisions"], due.DueAt)
        {
            RequestId = due.RequestId
        };
        var report = ChiefOfStaffOrchestrator.BuildManagementReport(checkIn, operatingContext);
        _ = await context.Platform.InvokeAsync<ManagementStatusReport, JsonElement>(
            "platform.management.status-report.v1",
            report,
            cancellationToken);
    }

    private static Task WriteRunLogAsync(
        Guid providerProfileId,
        string prompt,
        string? output,
        string status,
        DateTimeOffset startedAt,
        long durationMs,
        UsageDetails? usage,
        string? failureMessage,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    private static UsageDetails? ExtractUsage(IEnumerable<AIContent> contents)
    {
        UsageDetails? usage = null;

        foreach (var usageContent in contents.OfType<UsageContent>())
        {
            usage ??= new UsageDetails();
            usage.Add(usageContent.Details);
        }

        return usage;
    }

    private sealed record AssistantStreamUpdate(
        string Delta,
        string ReasoningDelta,
        UsageDetails? Usage,
        bool StartsNewDraft = false,
        IReadOnlyList<AssistantActivityUpdate>? Activities = null);

    private sealed record AssistantActivityUpdate(
        string Kind,
        string Title,
        IReadOnlyDictionary<string, string> Metadata);
}
