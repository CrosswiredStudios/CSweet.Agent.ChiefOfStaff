using System.Text.Json;
using CSweet.Agent.SDK;
namespace CSweet.Agents.ChiefOfStaff;

public sealed partial class ChiefOfStaffAgent
{
    private static async Task ResumeProjectAssistanceAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        foreach (var intake in await context.Platform.Projects.ListAssistanceAsync(ct))
        {
            var status = await context.Platform.Projects.StaffAsync(new(intake.Id, null, $"project-staffing:{intake.Id:N}"), ct);
            if (status.Candidates.Count > 0)
            {
                var manager = status.Candidates.OrderBy(x => x.Id).First();
                await context.Platform.Projects.StaffAsync(new(intake.Id, manager.Id, $"project-manager:{intake.Id:N}:{manager.Id:N}"), ct);
            }
        }
    }
    public override async Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(AgentCoordinationTurnRequest request, AgentRuntimeContext context, CancellationToken ct)
    {
        if (request.SourceKind == "ProjectIntake" && request.IsFinalization)
            return AgentCoordinationTurnResult.Completed("The project manager has responded. The developer will continue only after project creation and assignment are confirmed.");
        var artifact = request.Transcript.OrderByDescending(x => x.Ordinal).Select(x => x.Artifact).FirstOrDefault(x => x?.Type == "project-manager-assistance.v1");
        if (artifact is null) return AgentCoordinationTurnResult.Blocked("Please send a typed project-manager assistance request.");
        var assistance = artifact.Payload.Deserialize<ProjectManagerAssistanceRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (assistance is null) return AgentCoordinationTurnResult.Blocked("The project request is missing. The developer can retain it again or provide the manual setup link.");
        var status = await context.Platform.Projects.StaffAsync(new(assistance.IntakeId, null, $"project-staffing:{assistance.IntakeId:N}"), ct);
        if (status.Candidates.Count > 0)
        {
            var manager = status.Candidates.OrderBy(x => x.Id).First();
            await context.Platform.Projects.StaffAsync(new(assistance.IntakeId, manager.Id, $"project-manager:{assistance.IntakeId:N}:{manager.Id:N}"), ct);
            return AgentCoordinationTurnResult.Completed($"I’ve asked {manager.Name} to arrange project setup. Development is waiting for the project and your assignment to be confirmed.");
        }
        return AgentCoordinationTurnResult.Completed(status.Intake.Issue ?? "A project-manager hiring recommendation is awaiting human review. The user can also create the project using the setup link.");
    }
}
