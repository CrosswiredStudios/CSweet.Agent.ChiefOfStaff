using CSweet.Agent.SDK;

using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Policies;

namespace CSweet.Agents.ChiefOfStaff.Tests.Policies;

public sealed class GameStudioOwnershipPolicyTests
{
    [Fact]
    public void SingleProjectGameDirectorSuppressesModelProductManagerRecommendation()
    {
        var creativeDirectorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workstreamId = Guid.NewGuid();
        var organization = Organization(
            [new OrganizationPerson(creativeDirectorId, "Video Game Director", "Agent", roleId, null, Guid.NewGuid(), true)],
            [new OrganizationRole(roleId, "Video Game Director", "", "{}")],
            [new WorkstreamSummary(workstreamId, "First game", "Ship it", "Active", "Idea", creativeDirectorId, null, null, null)]);

        var decision = GameStudioOwnershipPolicy.Assess(
            organization, new HiringBacklogResponse([]), requestedWorkstreamId: null);
        var guarded = ChiefOfStaffAgent.EnforceGameStudioProducerOwnership(
            "Priority 1 Hire: Game Producer", decision);

        Assert.Equal(GameProducerOwnershipDecision.DelegateToGameDirector, decision);
        Assert.Contains("delegated", guarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Priority 1 Hire", guarded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BusinessWideDirectorOwnsEveryProject()
    {
        var creativeDirectorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var organization = Organization(
            [new OrganizationPerson(creativeDirectorId, "Video Game Director", "Agent", roleId, null, Guid.NewGuid(), true)],
            [new OrganizationRole(roleId, "Video Game Director", "", "{}")],
            [
                new WorkstreamSummary(Guid.NewGuid(), "Game A", "Ship A", "Active", "Idea", creativeDirectorId, null, null, null),
                new WorkstreamSummary(Guid.NewGuid(), "Game B", "Ship B", "Active", "Idea", null, null, null, null)
            ]);

        var decision = GameStudioOwnershipPolicy.Assess(
            organization, new HiringBacklogResponse([]), requestedWorkstreamId: null);
        var guarded = ChiefOfStaffAgent.EnforceGameStudioProducerOwnership(
            "Hire a Game Producer.", decision);

        Assert.Equal(GameProducerOwnershipDecision.DelegateToGameDirector, decision);
        Assert.DoesNotContain("?", guarded);
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
    public void PendingGameDirectorSuppressesSameProjectProductManager()
    {
        var workstreamId = Guid.NewGuid();
        var pendingGameDirector = new HiringRecommendationResponse(
            Guid.NewGuid(), workstreamId, "Video Game Director", "Own the vision.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            RoleKey = "game-director"
        };

        var decision = GameStudioOwnershipPolicy.Assess(
            Organization([], [], [new WorkstreamSummary(
                workstreamId, "Game", "Ship it", "Active", "Idea", null, null, null, null)]),
            new HiringBacklogResponse([pendingGameDirector]),
            workstreamId);

        Assert.Equal(GameProducerOwnershipDecision.DelegateToGameDirector, decision);
    }

    private static OrganizationSnapshotResponse Organization(
        IReadOnlyList<OrganizationPerson> people,
        IReadOnlyList<OrganizationRole> roles,
        IReadOnlyList<WorkstreamSummary> workstreams) =>
        new(Guid.NewGuid(), "Active", people, roles, [], workstreams, [], DateTimeOffset.UtcNow);

    private static HiringRecommendationResponse Recommendation(
        Guid? workstreamId,
        Guid? sourceRequestId) =>
        new(Guid.NewGuid(), workstreamId, "Game Producer", "Own game delivery.", "Suggested",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            RoleKey = "game-producer",
            SourceResourceChangeRequestId = sourceRequestId
        };
}
