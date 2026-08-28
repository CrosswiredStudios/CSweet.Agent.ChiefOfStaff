using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class GameStudioOwnershipPolicyTests
{
    [Fact]
    public void SingleProjectCreativeDirectorSuppressesModelProductManagerRecommendation()
    {
        var creativeDirectorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workstreamId = Guid.NewGuid();
        var organization = Organization(
            [new OrganizationPerson(creativeDirectorId, "Creative Director", "Agent", roleId, null, Guid.NewGuid(), true)],
            [new OrganizationRole(roleId, "Video Game Creative Director", "", "{}")],
            [new WorkstreamSummary(workstreamId, "First game", "Ship it", "Active", "Idea", creativeDirectorId, null, null, null)]);

        var decision = GameStudioOwnershipPolicy.Assess(
            organization, new HiringBacklogResponse([]), requestedWorkstreamId: null);
        var guarded = ChiefOfStaffAgent.EnforceGameStudioProductManagerOwnership(
            "Priority 1 Hire: Product Manager", decision);

        Assert.Equal(ProductManagerOwnershipDecision.DelegateToCreativeDirector, decision);
        Assert.Contains("delegated", guarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Priority 1 Hire", guarded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleUnscopedProjectsRequireExactlyOneClarification()
    {
        var creativeDirectorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var organization = Organization(
            [new OrganizationPerson(creativeDirectorId, "Creative Director", "Agent", roleId, null, Guid.NewGuid(), true)],
            [new OrganizationRole(roleId, "Creative Director", "", "{}")],
            [
                new WorkstreamSummary(Guid.NewGuid(), "Game A", "Ship A", "Active", "Idea", creativeDirectorId, null, null, null),
                new WorkstreamSummary(Guid.NewGuid(), "Game B", "Ship B", "Active", "Idea", null, null, null, null)
            ]);

        var decision = GameStudioOwnershipPolicy.Assess(
            organization, new HiringBacklogResponse([]), requestedWorkstreamId: null);
        var guarded = ChiefOfStaffAgent.EnforceGameStudioProductManagerOwnership(
            "Hire a Product Manager.", decision);

        Assert.Equal(ProductManagerOwnershipDecision.ClarifyProject, decision);
        Assert.Equal(1, guarded.Count(x => x == '?'));
    }

    [Fact]
    public void DifferentProjectAndLeadAuthoredProductManagersRemainUntouched()
    {
        var creativeProjectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var organization = Organization([], [], [
            new WorkstreamSummary(creativeProjectId, "Game A", "Ship A", "Active", "Idea", null, null, null, null),
            new WorkstreamSummary(otherProjectId, "Game B", "Ship B", "Active", "Idea", null, null, null, null)
        ]);
        var differentProject = Recommendation(otherProjectId, sourceRequestId: null);
        var leadAuthored = Recommendation(creativeProjectId, Guid.NewGuid());
        var sameProject = Recommendation(creativeProjectId, sourceRequestId: null);

        Assert.False(GameStudioOwnershipPolicy.IsConflictingChiefProductManager(
            differentProject, organization, creativeProjectId));
        Assert.False(GameStudioOwnershipPolicy.IsConflictingChiefProductManager(
            leadAuthored, organization, creativeProjectId));
        Assert.True(GameStudioOwnershipPolicy.IsConflictingChiefProductManager(
            sameProject, organization, creativeProjectId));
    }

    [Fact]
    public void PendingCreativeDirectorSuppressesSameProjectProductManager()
    {
        var workstreamId = Guid.NewGuid();
        var pendingCreativeDirector = new HiringRecommendationResponse(
            Guid.NewGuid(), workstreamId, "Video Game Creative Director", "Own the vision.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            RoleKey = "creative-director"
        };

        var decision = GameStudioOwnershipPolicy.Assess(
            Organization([], [], [new WorkstreamSummary(
                workstreamId, "Game", "Ship it", "Active", "Idea", null, null, null, null)]),
            new HiringBacklogResponse([pendingCreativeDirector]),
            workstreamId);

        Assert.Equal(ProductManagerOwnershipDecision.DelegateToCreativeDirector, decision);
    }

    private static OrganizationSnapshotResponse Organization(
        IReadOnlyList<OrganizationPerson> people,
        IReadOnlyList<OrganizationRole> roles,
        IReadOnlyList<WorkstreamSummary> workstreams) =>
        new(Guid.NewGuid(), "Active", people, roles, [], workstreams, [], DateTimeOffset.UtcNow);

    private static HiringRecommendationResponse Recommendation(
        Guid? workstreamId,
        Guid? sourceRequestId) =>
        new(Guid.NewGuid(), workstreamId, "Product Manager", "Own product outcomes.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            RoleKey = "product-manager",
            SourceResourceChangeRequestId = sourceRequestId
        };
}
