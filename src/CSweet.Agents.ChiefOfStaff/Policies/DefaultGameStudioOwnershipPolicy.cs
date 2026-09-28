using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Policies;

/// <summary>
/// Default <see cref="IGameStudioOwnershipPolicy"/> that delegates to the existing
/// static <see cref="GameStudioOwnershipPolicy"/> rules.
/// Keeps behavior identical while allowing substitution in tests or future policies (OCP/LSP).
/// </summary>
public sealed class DefaultGameStudioOwnershipPolicy : IGameStudioOwnershipPolicy
{
    public GameProducerOwnershipDecision Assess(
        OrganizationSnapshotResponse? organization,
        HiringBacklogResponse? backlog,
        Guid? requestedWorkstreamId) =>
        GameStudioOwnershipPolicy.Assess(organization, backlog, requestedWorkstreamId);

    public bool IsCreativeDirectorRole(string? roleKey, string? title) =>
        GameStudioOwnershipPolicy.IsCreativeDirectorRole(roleKey, title);

    public bool IsGameProducer(HiringRecommendationResponse recommendation) =>
        GameStudioOwnershipPolicy.IsGameProducer(recommendation);

    public bool IsConflictingChiefProductManager(
        HiringRecommendationResponse recommendation,
        OrganizationSnapshotResponse? organization,
        Guid? creativeDirectorWorkstreamId) =>
        GameStudioOwnershipPolicy.IsConflictingChiefProductManager(recommendation, organization, creativeDirectorWorkstreamId);
}
