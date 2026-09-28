using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Policies;

/// <summary>
/// Abstraction over the game-studio ownership rules so role-matching literals and
/// workstream policies can be substituted or extended without modifying consumers (OCP),
/// and the agent depends on an abstraction (DIP).
/// </summary>
public interface IGameStudioOwnershipPolicy
{
    GameProducerOwnershipDecision Assess(
        OrganizationSnapshotResponse? organization,
        HiringBacklogResponse? backlog,
        Guid? requestedWorkstreamId);

    bool IsCreativeDirectorRole(string? roleKey, string? title);

    bool IsGameProducer(HiringRecommendationResponse recommendation);

    bool IsConflictingChiefProductManager(
        HiringRecommendationResponse recommendation,
        OrganizationSnapshotResponse? organization,
        Guid? creativeDirectorWorkstreamId);
}
