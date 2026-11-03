using CSweet.Agent.SDK;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    internal static string? ImportedProjectManagerRole(WorkstreamSummary project) => project.ImportedProject is not { ProfileStatus: "Accepted" } profile
        ? null : profile.Category switch { "Software" => "software-product-manager", "VideoGame" => "game-director", _ => null };

    internal static async Task ResumeImportedProjectsAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var snapshot = await context.Platform.ReadOrganizationSnapshotAsync(ct);
        foreach (var project in snapshot.Workstreams.Where(x => x.ImportedProject is not null && x.Status == "Proposed" && x.AccountableManagerOrganizationUserId is null).Take(50))
        {
            var role = ImportedProjectManagerRole(project);
            if (role is null) continue; // Missing or unaccepted profiles need owner clarification, not guessed staffing.
            var game = role == "game-director";
            if (game && snapshot.People.Any(x => x.IsActive && x.AgentInstallationId.HasValue && snapshot.Roles.Any(r => r.Id == x.RoleId &&
                (r.Name.Equals("Video Game Director", StringComparison.OrdinalIgnoreCase) || r.Name.Equals("Game Director", StringComparison.OrdinalIgnoreCase))))) continue;
            await context.Platform.UpsertHiringRecommendationAsync(new(
                game ? "Video Game Director" : "Project manager for " + project.Name,
                game ? "Review imported game projects and establish the existing Director/Producer leadership path." : "Manage the imported project and propose its approved staffing plan. " + (project.ImportedProject!.Purpose ?? project.Outcome)[..Math.Min(1500, (project.ImportedProject!.Purpose ?? project.Outcome).Length)],
                game ? null : project.Id, [], null,
                game ? "imported-game-portfolio:director" : $"imported-project-manager:{project.Id:N}")
            { RoleKey = role, AssignAsProjectManager = !game, Headcount = 1 }, ct);
        }
    }
}
