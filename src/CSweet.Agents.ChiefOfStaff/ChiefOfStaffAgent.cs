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

namespace CSweet.Agents.ChiefOfStaff;

public sealed class ChiefOfStaffAgent : CSweetAgentBase
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

        if (string.Equals(message.EventType, ChiefOfStaffProfile.EmployeeHiredEvent, StringComparison.Ordinal))
        {
            await HandleEmployeeHiredAsync(message, context, cancellationToken);
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

        await PublishChunkAsync(context, message.EventId, new AssistantResponseChunk(
            conversationId,
            sequence++,
            "Chief of Staff accepted the request.",
            IsFinal: false,
            TurnId: incoming.TurnId,
            Kind: "progress",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = "accepted"
            },
            Attempt: incoming.Attempt), cancellationToken);

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
                    incoming.MessageId),
                ChiefOfStaffProfile.ConverseCapability,
                context,
                operatingContext: null,
                cancellationToken))
            {
                if (update.Usage is not null)
                {
                    usage.Add(update.Usage);
                }

                if (string.IsNullOrEmpty(update.Delta))
                {
                    continue;
                }

                builder.Append(update.Delta);

                _logger.LogInformation(
                    "Chief of Staff publishing chunk for conversation {ConversationId}. Sequence {Sequence}. DeltaLength {DeltaLength}.",
                    conversationId,
                    sequence,
                    update.Delta.Length);

                await PublishChunkAsync(context, message.EventId, new AssistantResponseChunk(
                    conversationId,
                    sequence++,
                    update.Delta,
                    IsFinal: false,
                    TurnId: incoming.TurnId,
                    Attempt: incoming.Attempt), cancellationToken);
            }
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

            await PublishAgentErrorAsync(
                context,
                message.EventId,
                conversationId,
                sequence,
                BuildSafeFailureMessage(exception),
                incoming.TurnId,
                incoming.Attempt,
                cancellationToken);
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

        if (builder.Length == 0)
        {
            _logger.LogWarning(
                "Chief of Staff generated an empty response for conversation {ConversationId}.",
                conversationId);

            await PublishAgentErrorAsync(
                context,
                message.EventId,
                conversationId,
                sequence,
                "The Chief of Staff could not complete the request because the model provider returned an empty response.",
                incoming.TurnId,
                incoming.Attempt,
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

        await PublishChunkAsync(context, message.EventId, new AssistantResponseChunk(
            conversationId,
            sequence,
            Delta: string.Empty,
            IsFinal: true,
            TurnId: incoming.TurnId,
            Kind: "final",
            Attempt: incoming.Attempt), cancellationToken);

        _logger.LogInformation(
            "Chief of Staff completed streaming for conversation {ConversationId}. Chunks {ChunkCount}. ResponseLength {ResponseLength}.",
            conversationId,
            sequence,
            builder.Length);

        try
        {
            await AttachMentionedHiringActionAsync(
                incoming.TurnId,
                builder.ToString(),
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
            builder.ToString(),
            "Completed",
            startedAt,
            stopwatch.ElapsedMilliseconds,
            usage,
            failureMessage: null,
            cancellationToken);

        await PushProductManagerContextUpdatesAsync(message.EventId, context, cancellationToken);
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
                return AgentWorkResult.Failure("Only an active Product Manager direct report may request a role brief.");
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
                return AgentWorkResult.Failure("Only an active Product Manager direct report may submit a product plan.");
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
                return AgentWorkResult.Failure("Only an active Product Manager direct report may escalate a decision.");
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

        var openingMessage = await GenerateOnboardingMessageAsync(
            onboarding,
            eventId,
            context,
            cancellationToken);
        var openingMessageId = await SendCommunicationMessageAsync(
            onboarding.ConversationId,
            openingMessage,
            $"agent-onboarded:{eventId:N}",
            context,
            cancellationToken);
        await AttachTopHiringActionAsync(
            openingMessageId,
            $"agent-onboarded:{eventId:N}",
            context,
            cancellationToken);
        await ReconcileApprovedResourceChangesAsync(context, cancellationToken);

        _ = await context.Platform.Lifecycle.CompleteOnboardingAsync(
            message,
            cancellationToken);

        _logger.LogInformation(
            "Chief of Staff completed onboarding event {EventId} in conversation {ConversationId}.",
            eventId,
            onboarding.ConversationId);
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
        if (request is not null)
            await ReconcileApprovedResourceChangeAsync(request, context, cancellationToken);
    }

    private async Task ReconcileApprovedResourceChangesAsync(
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        await RequireActiveChiefAsync(context, cancellationToken);
        var read = await context.Platform.ReadResourceChangesAsync(
            new ResourceChangeReadRequest(Statuses: ["Approved"]),
            cancellationToken);
        foreach (var request in read.Requests.OrderBy(x => x.DecidedAt))
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
            var stableRoleKey = $"{request.RequesterOrganizationUserId:N}:{delta.Role.RoleKey}";
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

            var recommendation = await context.Platform.UpsertHiringRecommendationAsync(
                new UpsertHiringRecommendationRequest(
                    delta.Role.Title,
                    delta.Role.Purpose,
                    null,
                    [],
                    null,
                    $"resource-role:{request.RequesterOrganizationUserId:N}:{NormalizeKey(delta.Role.RoleKey)}")
                {
                    Priority = Math.Max(1, delta.Role.Priority),
                    RoleKey = stableRoleKey,
                    Headcount = delta.Role.Headcount,
                    SourceResourceChangeRequestId = request.Id
                },
                cancellationToken);

            if (delta.ChangeKind is "Add" or "Increase")
                actionableRecommendations.Add((delta, recommendation));
        }

        if (request.Deltas.Count == 0) return;
        var messageId = await SendCommunicationMessageAsync(
            managerChat.Id,
            BuildResourceChangeManagerBrief(request),
            $"resource-change:{request.Id:N}:manager-brief",
            context,
            cancellationToken);
        var top = actionableRecommendations
            .OrderBy(x => x.Delta.Role.Priority)
            .ThenBy(x => x.Delta.Role.Title, StringComparer.Ordinal)
            .FirstOrDefault();
        if (top != default)
        {
            await SuggestMarketplaceActionAsync(
                messageId,
                top.Delta.Role.Title,
                $"resource-change:{request.Id:N}:action:{top.Recommendation.Id:N}",
                context,
                cancellationToken);
        }
    }

    internal static string BuildResourceChangeManagerBrief(ResourceChangeRequestResponse request)
    {
        var content = new StringBuilder();
        content.Append("Approved product-team staffing update for **")
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
            .Append("Added and increased roles are now candidate-free hiring suggestions. ")
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
        var roleName = self.RoleId.HasValue
            ? organization.Roles.SingleOrDefault(x => x.Id == self.RoleId.Value)?.Name
            : null;
        if (!self.DisplayName.Contains("Chief of Staff", StringComparison.OrdinalIgnoreCase) &&
            !(roleName?.Contains("Chief of Staff", StringComparison.OrdinalIgnoreCase) ?? false))
            throw new InvalidOperationException("This installation is not assigned the Chief of Staff role.");
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

    private async Task HandleEmployeeHiredAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var hired = DeserializePayload<EmployeeHiredEvent>(message.Payload);
        if (hired is null ||
            hired.OrganizationId == Guid.Empty ||
            hired.OrganizationUserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(hired.RoleTitle) ||
            !string.Equals(context.BusinessId, hired.OrganizationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignored malformed employee hired event {EventId}.", message.EventId);
            return;
        }

        var backlog = await context.Platform.ListHiringRecommendationsAsync(cancellationToken);
        var hiredRole = NormalizeRoleIdentity(hired.RoleTitle);
        var matches = backlog.Recommendations
            .Where(x => string.Equals(NormalizeRoleIdentity(x.Title), hiredRole, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
        {
            if (matches.Count > 1)
                _logger.LogWarning(
                    "Employee hired event {EventId} matched {Count} Chief suggestions for role {Role}; no suggestion was resolved.",
                    message.EventId,
                    matches.Count,
                    hired.RoleTitle);
            return;
        }

        var matched = matches[0];
        var next = backlog.Recommendations
            .Where(x => x.Id != matched.Id)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefault();
        var ownerChat = await FindOwnerChatAsync(context, cancellationToken);
        var content = next is null
            ? $"{hired.RoleTitle} is now hired. Your current hiring backlog is complete."
            : $"{hired.RoleTitle} is now hired. The next priority is **{next.Title}**: {next.Objective}";
        var sentMessageId = await SendCommunicationMessageAsync(
            ownerChat.Id,
            content,
            $"employee-hired:{message.EventId}:next",
            context,
            cancellationToken);
        if (next is not null)
        {
            await SuggestMarketplaceActionAsync(
                sentMessageId,
                next.Title,
                $"employee-hired:{message.EventId}:action:{next.Id:N}",
                context,
                cancellationToken);
        }
        _ = await context.Platform.InvokeAsync<ResolveHiringRecommendationRequest, JsonElement>(
            ChiefOfStaffProfile.ResolveHiringRecommendationCapability,
            new ResolveHiringRecommendationRequest(
                matched.Id,
                hired.OrganizationUserId,
                $"employee-hired:{message.EventId}:resolve:{matched.Id:N}"),
            cancellationToken);
    }

    private async Task AttachTopHiringActionAsync(
        Guid messageId,
        string idempotencyPrefix,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
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
            $"{idempotencyPrefix}:action:{next.Id:N}",
            context,
            cancellationToken);
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

        _ = await context.Platform.InvokeAsync<SuggestUserActionRequest, JsonElement>(
            ChiefOfStaffProfile.SuggestUserActionCapability,
            new SuggestUserActionRequest(
                null,
                chatTurnId,
                ChiefOfStaffProfile.HiringMarketplaceBrowseWorkflow,
                "Browse candidates",
                $"Review Marketplace candidates for the {next.Title} role.",
                JsonSerializer.SerializeToElement(new { role = next.Title }),
                $"{idempotencyPrefix}:action:{next.Id:N}"),
            cancellationToken);
    }

    private static async Task SuggestMarketplaceActionAsync(
        Guid messageId,
        string role,
        string idempotencyKey,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        _ = await context.Platform.InvokeAsync<SuggestUserActionRequest, JsonElement>(
            ChiefOfStaffProfile.SuggestUserActionCapability,
            new SuggestUserActionRequest(
                messageId,
                null,
                ChiefOfStaffProfile.HiringMarketplaceBrowseWorkflow,
                "Browse candidates",
                $"Review Marketplace candidates for the {role} role.",
                JsonSerializer.SerializeToElement(new { role }),
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

    private async Task<string> GenerateOnboardingMessageAsync(
        AgentOnboardedEvent onboarding,
        Guid eventId,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var operatingContext = await _orchestrator.AssembleContextAsync(context, cancellationToken);
        var fallback = ChiefOfStaffOrchestrator.BuildContextualOnboardingFallback(operatingContext);
        var providerProfileId = Settings.GetGuid("llmProviderId");
        if (providerProfileId is null || providerProfileId == Guid.Empty)
        {
            _logger.LogWarning(
                "Chief of Staff onboarding used the contextual fallback because no LLM provider is configured for installation {InstallationId}.",
                context.InstallationId);
            return fallback;
        }

        const string onboardingRequest = """
This is your first message after being hired into this business. Review the authoritative business, financial, organization, and hiring-backlog context before responding.

Do not use a generic welcome or ask the owner to repeat facts already present in the business profile. If the available data is sufficient, give a brief business-specific assessment, name the compact role map, identify the single most important role to fill first and why, and begin the normal ranked-hiring-backlog workflow. If the available data is insufficient to choose the first role responsibly, state what you already understand and ask only the single highest-value clarification. Do not use a multi-part intake questionnaire.
""";

        try
        {
            var response = await GenerateResponseAsync(
                new AssistantCapabilityInput(
                    providerProfileId.Value,
                    onboarding.ConversationId.ToString("D"),
                    onboardingRequest,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["userId"] = onboarding.HiringOrganizationUserId.ToString("D"),
                        ["onboardingEventId"] = eventId.ToString("D")
                    },
                    onboarding.HiringOrganizationUserId.ToString("D")),
                ChiefOfStaffProfile.ConverseCapability,
                context,
                cancellationToken,
                operatingContext);

            if (!string.IsNullOrWhiteSpace(response.Response))
            {
                return FormatOnboardingMessage(response.Response);
            }

            _logger.LogWarning(
                "Chief of Staff onboarding generation returned no content for installation {InstallationId}.",
                context.InstallationId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Chief of Staff onboarding generation failed for installation {InstallationId}; using contextual fallback.",
                context.InstallationId);
        }

        return fallback;
    }

    internal static string FormatOnboardingMessage(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        if (lines.Count == 0) return string.Empty;

        var sections = new List<string>(lines.Count + 2);
        foreach (var line in lines)
        {
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

        var selection = new AgentLlmSelection(
            input.ProviderProfileId,
            Settings.GetString("llmModel"));
        var chatClient = _llmClientFactory is null
            ? new PlatformChatClient(runtimeContext.Platform, selection)
            : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);

        operatingContext ??= await _orchestrator.AssembleContextAsync(runtimeContext, cancellationToken);
        await _orchestrator.CaptureExplicitFactsAsync(chatClient, input, operatingContext, runtimeContext, cancellationToken);

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

        var tools = (await runtimeContext.GetModelToolsAsync(cancellationToken)).ToList();
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
                "Consult the active Product Manager direct report for product strategy, discovery, roadmap, requirements, priorities, or product-team design."));
        }

        AIAgent agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = ChiefOfStaffProfile.AgentId,
                Name = runtimeContext.Identity?.DisplayName ?? ChiefOfStaffProfile.DefaultDisplayName,
                ChatOptions = new ChatOptions
                {
                    Instructions = ChiefOfStaffProfile.SystemPrompt,
                    Tools = tools
                },
                AIContextProviders = [memoryProvider]
            });

        var prompt = _orchestrator.BuildGroundedPrompt(input.Prompt, capability, operatingContext, Settings);

        AgentSession session = await agent.CreateSessionAsync(cancellationToken);
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

        _logger.LogInformation(
            "Chief of Staff starting MAF streaming for conversation {ConversationId}. Capability {Capability}. PromptLength {PromptLength}.",
            input.ConversationId,
            capability,
            prompt.Length);

        await foreach (var update in agent.RunStreamingAsync(prompt, session, options: null, cancellationToken))
        {
            var usage = ExtractUsage(update.Contents);
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new AssistantStreamUpdate(update.Text, usage);
            }
            else if (usage is not null)
            {
                yield return new AssistantStreamUpdate(string.Empty, usage);
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
            builder.ToString(),
            ProposedActions: [],
            DateTimeOffset.UtcNow);
    }

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
        var productManager = organization.People
            .Where(person =>
            {
                if (!person.IsActive ||
                    person.ReportsToId != chiefId ||
                    person.AgentInstallationId is null ||
                    !person.EmployeeType.Equals("Agent", StringComparison.OrdinalIgnoreCase))
                    return false;
                var roleName = person.RoleId.HasValue
                    ? organization.Roles.SingleOrDefault(x => x.Id == person.RoleId.Value)?.Name
                    : null;
                return (roleName?.Contains("Product Manager", StringComparison.OrdinalIgnoreCase) ?? false) ||
                       person.DisplayName.Contains("Product Manager", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(x => x.DisplayName)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No active Product Manager reports to this Chief of Staff.");
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
        var productManager = operatingContext.Organization?.People.SingleOrDefault(x =>
            x.Id == productManagerId &&
            x.IsActive &&
            x.EmployeeType.Equals("Agent", StringComparison.OrdinalIgnoreCase) &&
            x.AgentInstallationId == productManagerInstallationId);
        if (productManager?.ReportsToId != chiefId) return false;
        var roleName = productManager.RoleId.HasValue
            ? operatingContext.Organization?.Roles.SingleOrDefault(x => x.Id == productManager.RoleId.Value)?.Name
            : null;
        return (roleName?.Contains("Product Manager", StringComparison.OrdinalIgnoreCase) ?? false) ||
               productManager.DisplayName.Contains("Product Manager", StringComparison.OrdinalIgnoreCase);
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
        var sourceId = sourceEventId;
        var productManagers = organization.People.Where(person =>
        {
            if (!person.IsActive ||
                person.ReportsToId != chiefId ||
                person.AgentInstallationId is null ||
                !person.EmployeeType.Equals("Agent", StringComparison.OrdinalIgnoreCase))
                return false;
            var roleName = person.RoleId.HasValue
                ? organization.Roles.SingleOrDefault(x => x.Id == person.RoleId.Value)?.Name
                : null;
            return (roleName?.Contains("Product Manager", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   person.DisplayName.Contains("Product Manager", StringComparison.OrdinalIgnoreCase);
        }).ToList();

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

    private sealed record AssistantStreamUpdate(string Delta, UsageDetails? Usage);
}
