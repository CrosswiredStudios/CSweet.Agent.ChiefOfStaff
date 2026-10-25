using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Formatting;

namespace CSweet.Agents.ChiefOfStaff.Policies;

public enum GameProducerOwnershipDecision
{
    Allow,
    DelegateToGameDirector,
    ClarifyProject
}

public static class GameStudioOwnershipPolicy
{
    internal static GameProducerOwnershipDecision Assess(
        OrganizationSnapshotResponse? organization,
        HiringBacklogResponse? backlog,
        Guid? requestedWorkstreamId)
    {
        // The Director owns the whole business portfolio, regardless of project count.
        return ActiveGameDirectorIds(organization).Count > 0 ||
               backlog?.Recommendations.Any(IsPendingGameDirectorRecommendation) == true
            ? GameProducerOwnershipDecision.DelegateToGameDirector
            : GameProducerOwnershipDecision.Allow;
    }

    private static readonly ResponsePolicyFormatter Formatter = new();

    internal static bool IsGameDirectorRole(string? roleKey, string? title)
    {
        var normalizedKey = Formatter.NormalizeRoleIdentity(roleKey ?? string.Empty);
        var normalizedTitle = Formatter.NormalizeRoleIdentity(title ?? string.Empty);
        return normalizedKey is "gamedirector" or "videogamedirector" ||
               normalizedTitle is "gamedirector" or "videogamedirector";
    }

    internal static bool IsGameProducer(HiringRecommendationResponse recommendation) =>
        Formatter.NormalizeRoleIdentity(recommendation.RoleKey ?? string.Empty) == "gameproducer" ||
        Formatter.NormalizeRoleIdentity(recommendation.Title) is "gameproducer" or "videogameproducer";

    internal static bool IsConflictingChiefProductManager(
        HiringRecommendationResponse recommendation,
        OrganizationSnapshotResponse? organization,
        Guid? creativeDirectorWorkstreamId)
    {
        if (!IsGameProducer(recommendation) || recommendation.SourceResourceChangeRequestId is not null)
            return false;

        if (creativeDirectorWorkstreamId is { } explicitScope)
        {
            if (recommendation.WorkstreamId == explicitScope)
                return true;
            return recommendation.WorkstreamId is null && ActiveWorkstreamCount(organization) <= 1;
        }

        if (ActiveWorkstreamCount(organization) <= 1)
            return recommendation.WorkstreamId is null ||
                   organization?.Workstreams.Any(x => x.Id == recommendation.WorkstreamId) == true;

        var creativeDirectorIds = ActiveGameDirectorIds(organization);
        return recommendation.WorkstreamId is { } recommendationScope &&
               organization?.Workstreams.Any(x =>
                   x.Id == recommendationScope &&
                   x.AccountableManagerOrganizationUserId is { } managerId &&
                   creativeDirectorIds.Contains(managerId)) == true;
    }

    private static HashSet<Guid> ActiveGameDirectorIds(OrganizationSnapshotResponse? organization)
    {
        if (organization is null) return [];
        var roleIds = organization.Roles
            .Where(x => IsGameDirectorRole(null, x.Name))
            .Select(x => x.Id)
            .ToHashSet();
        return organization.People
            .Where(x => x.IsActive &&
                        ((x.RoleId is { } roleId && roleIds.Contains(roleId)) ||
                         IsGameDirectorRole(null, x.DisplayName)))
            .Select(x => x.Id)
            .ToHashSet();
    }

    private static bool IsPendingGameDirectorRecommendation(HiringRecommendationResponse recommendation) =>
        recommendation.SourceResourceChangeRequestId is null &&
        !string.Equals(recommendation.Status, "Fulfilled", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(recommendation.Status, "Withdrawn", StringComparison.OrdinalIgnoreCase) &&
        IsGameDirectorRole(recommendation.RoleKey, recommendation.Title);

    private static int ActiveWorkstreamCount(OrganizationSnapshotResponse? organization) =>
        organization?.Workstreams.Count(IsActiveWorkstream) ?? 0;

    private static bool IsActiveWorkstream(WorkstreamSummary workstream) =>
        !workstream.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        !workstream.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) &&
        !workstream.Status.Equals("Archived", StringComparison.OrdinalIgnoreCase);
}
