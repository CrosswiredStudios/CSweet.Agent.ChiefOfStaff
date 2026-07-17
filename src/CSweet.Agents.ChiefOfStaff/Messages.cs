using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff;

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
    string? Error = null);

public sealed record ChiefOperatingContext(
    BusinessProfileResponse? BusinessProfile,
    FinancialOperatingProfileResponse? FinancialProfile,
    OrganizationSnapshotResponse? Organization,
    BusinessPatternSearchResponse? Patterns,
    ManagementCycleResponse? ManagementCycle,
    IReadOnlyList<string> UnavailableCapabilities);
