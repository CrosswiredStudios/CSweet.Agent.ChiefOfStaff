using System.Text.Json;
using Microsoft.Extensions.Logging;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Profiles;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    internal static string ReadCurrentUserMessage(UserMessageReceived incoming)
    {
        if (incoming.CurrentMessageContent is { } current) return current.Trim();
        // Compatibility with platforms that wrap the message but do not send its
        // separate field. Only the final current-message block controls setup.
        const string opening = "<current_user_message>";
        const string closing = "</current_user_message>";
        var text = incoming.Message.Trim();
        var start = text.LastIndexOf(opening, StringComparison.Ordinal);
        if (start >= 0 && text.EndsWith(closing, StringComparison.Ordinal))
            return text[(start + opening.Length)..^closing.Length].Trim();
        const string contextEnd = "</platform_interaction_context>";
        if (text.StartsWith("<platform_interaction_context>", StringComparison.Ordinal) &&
            text.IndexOf(contextEnd, StringComparison.Ordinal) is var end && end >= 0)
            return text[(end + contextEnd.Length)..].Trim();
        return text;
    }

    private async Task<bool> BeginHiringPolicySetupAsync(Guid conversationId, AgentRuntimeContext context, CancellationToken token)
    {
        HiringPolicyResponse policy;
        try { policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token); }
        catch (PlatformCapabilityException) { return false; } // Older platforms retain their existing onboarding.
        if (policy.SetupComplete) return false;
        await AskHiringPolicyQuestionAsync(conversationId, policy, context, token);
        return true;
    }

    private static async Task<bool> TryStartHiringPolicyChangeAsync(Guid conversationId, Guid turnId, string text, AgentRuntimeContext context, CancellationToken token)
    {
        if (text.StartsWith("Decision: ", StringComparison.Ordinal) ||
            !(text.Contains("hire automatically", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("automatically hire", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("hiring", StringComparison.OrdinalIgnoreCase) &&
              (text.Contains("change", StringComparison.OrdinalIgnoreCase) || text.Contains("autonomy", StringComparison.OrdinalIgnoreCase) || text.Contains("preferences", StringComparison.OrdinalIgnoreCase)))) return false;
        try
        {
            var policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            await AskHiringPolicyQuestionAsync(conversationId, policy with { SetupStage = "mode" }, context, token, turnId.ToString("N"));
            return true;
        }
        catch (PlatformCapabilityException) { return false; }
    }

    private async Task<bool> HandleHiringPolicyAnswerAsync(Guid conversationId, Guid turnId, string text,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (!text.StartsWith("Decision: ", StringComparison.Ordinal) ||
            !(text.Contains("hiring", StringComparison.OrdinalIgnoreCase) || text.Contains("publisher", StringComparison.OrdinalIgnoreCase))) return false;
        HiringPolicyResponse policy;
        try
        {
            policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            policy = await context.Platform.InvokeAsync<CaptureHiringPolicyDecisionRequest, HiringPolicyResponse>(
                    HiringAutonomyCapabilities.CaptureDecision, new(Guid.Empty, policy.Revision, turnId), token);
        }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.ValidationFailed)
        {
            var unchanged = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            await AskHiringPolicyQuestionAsync(conversationId, unchanged, context, token, turnId.ToString("N"));
            return true;
        }
        catch (PlatformCapabilityException) { return false; }
        if (!policy.SetupComplete) await AskHiringPolicyQuestionAsync(conversationId, policy, context, token);
        else
        {
            var operating = await _orchestrator.AssembleContextAsync(context, token);
            await BeginFocusOnboardingAsync(conversationId, $"hiring-policy-complete:{policy.InstallationId:N}:{policy.Revision}",
                operating, BusinessOperatingProfiles.Resolve(Settings), context, token);
        }
        return true;
    }

    private static async Task AskHiringPolicyQuestionAsync(Guid conversationId, HiringPolicyResponse policy,
        AgentRuntimeContext context, CancellationToken token, string? retry = null)
    {
        var (prompt, options, recommended) = policy.SetupStage switch
        {
            "level" => ("How much automatic hiring authority should I have?", new List<RequestUserInputOption>
            {
                new("approved", "Approved packages", "Reuse packages and permissions you’ve already approved."),
                new("bounded", "Within limits", "Install packages within your explicit cost and permission limits."),
                new("broad", "Broad delegation", "Install eligible packages and grant their requested permissions for approved roles. This gives me the most authority and carries the greatest risk.")
            }, "approved"),
            "publishers" => ("Which publishers should I consider for hiring?", new List<RequestUserInputOption>
            {
                new("prefer", "Prefer first-party", "Use suitable first-party agents first, with third-party fallback."),
                new("first", "First-party only", "Only consider agents from the platform first-party catalog."),
                new("best", "Best fit", "Choose by role fit regardless of publisher.")
            }, "prefer"),
            "costs" => ("What automatic hiring cost limits should I use?", new List<RequestUserInputOption>
            {
                new("free", "Free agents only", "Zero per hire and zero total package cost, in USD."),
                new("skip", "Keep recommending", "Choose hiring cost limits later.")
            }, "free"),
            "permissions" => ("Which permissions may new hiring packages request?", new List<RequestUserInputOption>
            {
                new("none", "No capabilities or network access", "New packages cannot request capabilities or network access."),
                new("existing", "Permissions already approved here", "Allow capability keys and network entries already granted to enabled agents in this business."),
                new("skip", "Keep recommending", "Choose exact permissions in settings later.")
            }, "none"),
            _ => ("How should I help with hiring?", new List<RequestUserInputOption>
            {
                new("recommend", "Recommend candidates", "I’ll select a candidate; you review and confirm each hire."),
                new("choose", "Choose candidates yourself", "I’ll explain the role and take you to filtered candidates."),
                new("automatic", "Automatic hiring", "I’ll fill approved roles after we agree on my authority.")
            }, "recommend")
        };
        var key = $"hiring-policy:{policy.InstallationId:N}:{policy.SetupStage}:{policy.Revision}:{retry}";
        var opening = policy.SetupStage == "mode"
            ? "Before we choose the company’s focus, let’s agree on how I should help with hiring. I’ll recommend candidates unless you choose another mode."
            : policy.SetupStage == "costs"
                ? "Choose free agents only, or tell me your limits—for example: 10 USD per hire, 100 USD total. You can also set exact limits in Chief hiring settings."
                : "Let’s finish your hiring preferences.";
        var id = await SendCommunicationMessageAsync(conversationId, opening, key, context, token);
        _ = await context.Platform.InvokeAsync<RequestUserInputRequest, RequestUserInputResponse>(
            ChiefOfStaffProfile.RequestUserInputCapability,
            new(conversationId, null, id, prompt, options, recommended, key), token);
    }

    private async Task RecoverApprovedHiringAsync(AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var plans = await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(Statuses: ["Approved"]), token);
            foreach (var plan in plans.Requests.Take(100))
                await ReconcileApprovedResourceChangeAsync(plan, context, token);
            var backlog = await context.Platform.ListHiringRecommendationsAsync(token);
            foreach (var recommendation in backlog.Recommendations.Where(x => x.SourceResourceChangeRequestId.HasValue && x.FulfilledHeadcount < x.Headcount).Take(100))
                _ = await PrepareHiringCandidateAsync(recommendation.Id, context, token);
        }
        catch (PlatformCapabilityException exception)
        {
            _logger.LogInformation("Hiring recovery requires review or unavailable capabilities: {Code}", exception.Code);
        }
    }

    private static async Task<bool> PrepareHiringCandidateAsync(Guid recommendationId, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var selected = await context.Platform.InvokeAsync<SelectHiringCandidateRequest, HiringCandidateSelectionResponse>(
                HiringAutonomyCapabilities.SelectCandidate, new(recommendationId), token);
            if (selected.Mode == HiringSelectionMode.Automatic && selected.Candidate is not null)
            {
                var result = await context.Platform.InvokeAsync<SubmitDelegatedHireRequest, DelegatedHireResponse>(
                    HiringAutonomyCapabilities.Submit, new(recommendationId), token);
                if (result.Status == "Succeeded") return false;
            }
        }
        catch (PlatformCapabilityException) { /* A candidate-selection failure preserves the manual marketplace action. */ }
        return true;
    }
}
