using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff;

internal enum ProductManagerOwnershipDecision
{
    Allow,
    DelegateToCreativeDirector,
    ClarifyProject
}

internal static class GameStudioOwnershipPolicy
{
    internal static ProductManagerOwnershipDecision Assess(
        OrganizationSnapshotResponse? organization,
        HiringBacklogResponse? backlog,
        Guid? requestedWorkstreamId)
    {
        var activeWorkstreams = organization?.Workstreams
            .Where(IsActiveWorkstream)
            .ToList() ?? [];
        var creativeDirectorIds = ActiveCreativeDirectorIds(organization);
        var creativeOwnedWorkstreamIds = activeWorkstreams
            .Where(x => x.AccountableManagerOrganizationUserId is { } managerId &&
                        creativeDirectorIds.Contains(managerId))
            .Select(x => x.Id)
            .ToHashSet();
        var pendingCreativeDirectorScopes = backlog?.Recommendations
            .Where(IsPendingCreativeDirectorRecommendation)
            .Select(x => x.WorkstreamId)
            .ToList() ?? [];
        var hasCreativeOwnership = creativeDirectorIds.Count > 0 || pendingCreativeDirectorScopes.Count > 0;
        if (!hasCreativeOwnership)
            return ProductManagerOwnershipDecision.Allow;

        if (requestedWorkstreamId is { } workstreamId)
        {
            if (creativeOwnedWorkstreamIds.Contains(workstreamId) ||
                pendingCreativeDirectorScopes.Contains(workstreamId))
                return ProductManagerOwnershipDecision.DelegateToCreativeDirector;

            // A single early-stage game is the default scope for an unscoped Creative Director.
            if (activeWorkstreams.Count <= 1 &&
                (creativeDirectorIds.Count > 0 || pendingCreativeDirectorScopes.Contains(null)))
                return ProductManagerOwnershipDecision.DelegateToCreativeDirector;

            return ProductManagerOwnershipDecision.Allow;
        }

        return activeWorkstreams.Count > 1
            ? ProductManagerOwnershipDecision.ClarifyProject
            : ProductManagerOwnershipDecision.DelegateToCreativeDirector;
    }

    internal static bool IsCreativeDirectorRole(string? roleKey, string? title)
    {
        var normalizedKey = ChiefOfStaffAgent.NormalizeRoleIdentity(roleKey ?? string.Empty);
        var normalizedTitle = ChiefOfStaffAgent.NormalizeRoleIdentity(title ?? string.Empty);
        return normalizedKey is "creativedirector" or "gamedirector" or "videogamecreativedirector" ||
               normalizedTitle.Contains("creativedirector", StringComparison.Ordinal) ||
               normalizedTitle.Contains("gamedirector", StringComparison.Ordinal);
    }

    internal static bool IsProductManager(HiringRecommendationResponse recommendation) =>
        ChiefOfStaffAgent.NormalizeRoleIdentity(recommendation.RoleKey ?? string.Empty) == "productmanager" ||
        ChiefOfStaffAgent.NormalizeRoleIdentity(recommendation.Title) == "productmanager";

    internal static bool IsConflictingChiefProductManager(
        HiringRecommendationResponse recommendation,
        OrganizationSnapshotResponse? organization,
        Guid? creativeDirectorWorkstreamId)
    {
        if (!IsProductManager(recommendation) || recommendation.SourceResourceChangeRequestId is not null)
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

        var creativeDirectorIds = ActiveCreativeDirectorIds(organization);
        return recommendation.WorkstreamId is { } recommendationScope &&
               organization?.Workstreams.Any(x =>
                   x.Id == recommendationScope &&
                   x.AccountableManagerOrganizationUserId is { } managerId &&
                   creativeDirectorIds.Contains(managerId)) == true;
    }

    private static HashSet<Guid> ActiveCreativeDirectorIds(OrganizationSnapshotResponse? organization)
    {
        if (organization is null) return [];
        var roleIds = organization.Roles
            .Where(x => IsCreativeDirectorRole(null, x.Name))
            .Select(x => x.Id)
            .ToHashSet();
        return organization.People
            .Where(x => x.IsActive &&
                        ((x.RoleId is { } roleId && roleIds.Contains(roleId)) ||
                         IsCreativeDirectorRole(null, x.DisplayName)))
            .Select(x => x.Id)
            .ToHashSet();
    }

    private static bool IsPendingCreativeDirectorRecommendation(HiringRecommendationResponse recommendation) =>
        recommendation.SourceResourceChangeRequestId is null &&
        !string.Equals(recommendation.Status, "Fulfilled", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(recommendation.Status, "Withdrawn", StringComparison.OrdinalIgnoreCase) &&
        IsCreativeDirectorRole(recommendation.RoleKey, recommendation.Title);

    private static int ActiveWorkstreamCount(OrganizationSnapshotResponse? organization) =>
        organization?.Workstreams.Count(IsActiveWorkstream) ?? 0;

    private static bool IsActiveWorkstream(WorkstreamSummary workstream) =>
        !workstream.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        !workstream.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) &&
        !workstream.Status.Equals("Archived", StringComparison.OrdinalIgnoreCase);
}
