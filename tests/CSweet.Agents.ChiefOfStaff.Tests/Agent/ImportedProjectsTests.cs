using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Agent;

namespace CSweet.Agents.ChiefOfStaff.Tests.Agent;

public sealed class ImportedProjectsTests
{
    [Theory]
    [InlineData("Software", "Accepted", "software-product-manager")]
    [InlineData("VideoGame", "Accepted", "game-director")]
    [InlineData("Software", "Review", null)]
    [InlineData("Unknown", "Accepted", null)]
    public async Task RecoveryUsesAcceptedCategoryAndStableProjectBoundRecommendations(string category, string status, string? role)
    {
        var project = new WorkstreamSummary(Guid.NewGuid(), "Existing", "Retain work", "Proposed", "Onboarding", null, null, null, null)
        { ImportedProject = new(Guid.NewGuid(), 1, status, category, "Owner-approved purpose", null) };
        var runtime = new AgentTestRuntime();
        var writes = new List<UpsertHiringRecommendationRequest>();
        runtime.RegisterCapability<JsonElement, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead,
            (_, _) => Task.FromResult(new OrganizationSnapshotResponse(Guid.NewGuid(), "Active", [], [], [], [project], [], DateTimeOffset.UtcNow)));
        runtime.RegisterCapability<UpsertHiringRecommendationRequest, HiringRecommendationResponse>(PlatformCapabilities.HiringRecommendationUpsert,
            (r, _) => { writes.Add(r); return Task.FromResult(new HiringRecommendationResponse(Guid.NewGuid(), r.WorkstreamId, r.Title, r.Objective, "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)); });
        var context = runtime.CreateContext(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await ChiefOfStaffAgent.ResumeImportedProjectsAsync(context, default);
        await ChiefOfStaffAgent.ResumeImportedProjectsAsync(context, default);
        if (role is null) { Assert.Empty(writes); return; }
        Assert.Equal(2, writes.Count);
        Assert.Equal(writes[0].IdempotencyKey, writes[1].IdempotencyKey);
        Assert.All(writes, x => { Assert.Equal(role, x.RoleKey); Assert.Empty(x.CandidateReferences); Assert.Equal(1, x.Headcount); });
        Assert.Equal(category == "Software", writes[0].AssignAsProjectManager);
        Assert.Equal(category == "Software" ? project.Id : (Guid?)null, writes[0].WorkstreamId);
    }
}
