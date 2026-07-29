using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff;

public sealed record AgentOnboardedEvent(
    Guid OrganizationId,
    Guid AgentOrganizationUserId,
    Guid HiringOrganizationUserId,
    Guid ConversationId,
    DateTimeOffset OccurredAt,
    Guid EventId = default);

public sealed record SendCommunicationMessageRequest(
    Guid ChatId,
    string Content,
    string IdempotencyKey);

internal sealed record CreateCommunicationChatRequest(
    string? Title,
    string? Description,
    bool IsDirect,
    bool IsPrivate,
    IReadOnlyList<Guid> ParticipantOrganizationUserIds,
    IReadOnlyList<Guid>? AudienceRoleIds = null,
    IReadOnlyList<Guid>? AudienceWorkstreamIds = null);

internal sealed record CommunicationHubActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    CommunicationChatResponse? Chat = null);

public sealed record EmployeeHiredEvent(
    Guid OrganizationId,
    Guid OrganizationUserId,
    string EmployeeType,
    Guid? RoleId,
    string? RoleTitle,
    Guid? AgentInstallationId,
    Guid? WorkerId,
    Guid? ReportsToOrganizationUserId,
    Guid? HiringOrganizationUserId,
    string Source,
    DateTimeOffset OccurredAt);

public sealed record ResolveHiringRecommendationRequest(
    Guid RecommendationId,
    Guid ResultOrganizationUserId,
    string IdempotencyKey);

public sealed record SuggestUserActionRequest(
    Guid? MessageId,
    Guid? ChatTurnId,
    string WorkflowType,
    string Label,
    string? Description,
    System.Text.Json.JsonElement Parameters,
    string IdempotencyKey);

public sealed record CompleteAgentOnboardingRequest(Guid EventId);

public sealed record UserMessageReceived(
    Guid ProviderProfileId,
    string ConversationId,
    string UserId,
    string Message,
    IReadOnlyDictionary<string, string>? Context,
    Guid TurnId = default,
    int Attempt = 0,
    Guid MessageId = default);

public sealed record AssistantCapabilityInput(
    Guid ProviderProfileId,
    string ConversationId,
    string Prompt,
    IReadOnlyDictionary<string, string>? Context,
    string? UserId = null,
    Guid MessageId = default);

public sealed record AssistantResponseCreated(
    string ConversationId,
    string Response,
    IReadOnlyList<ProposedAction> ProposedActions,
    DateTimeOffset CreatedAt);

public sealed record ProposedAction(
    string ActionType,
    string Summary,
    string ParametersJson,
    bool RequiresApproval);

public sealed record AssistantResponseChunk(
    string ConversationId,
    int Sequence,
    string Delta,
    bool IsFinal,
    string? Error = null,
    Guid TurnId = default,
    string Kind = "output",
    IReadOnlyDictionary<string, string>? Metadata = null,
    int Attempt = 0);

public sealed record ChiefOperatingContext(
    BusinessProfileResponse? BusinessProfile,
    FinancialOperatingProfileResponse? FinancialProfile,
    OrganizationSnapshotResponse? Organization,
    BusinessPatternSearchResponse? Patterns,
    ManagementCycleResponse? ManagementCycle,
    HiringBacklogResponse? HiringBacklog,
    IReadOnlyList<string> UnavailableCapabilities);
