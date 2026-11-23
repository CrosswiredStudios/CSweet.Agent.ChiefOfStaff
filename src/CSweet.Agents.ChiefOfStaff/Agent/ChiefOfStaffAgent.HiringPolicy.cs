using System.Text.Json;
using Microsoft.Extensions.Logging;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Profiles;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    internal static async Task<string> ReadHiringInstructionsAsync(AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            if (policy.SetupComplete && policy.Settings.Mode == HiringSelectionMode.Automatic)
                return $"Live platform hiring policy: Automatic hiring; authority {policy.Settings.AutomaticLevel}; publishers {policy.Settings.Publishers}. " +
                    "Once the owner chooses a focus and a leadership gap is supported, upsert the recommendation and let the deterministic runtime hire within this policy. " +
                    "Do not ask whether to proceed, move to Marketplace, review candidates, or confirm the selected hire. " +
                    "Do not claim completion before the platform reports it. A real configuration or authority blocker needs only the missing input, not renewed hiring approval.";
            return "Live platform hiring policy: owner candidate review is required. Select a supported staffing recommendation without asking whether to open Marketplace; the runtime supplies the review action.";
        }
        catch (PlatformCapabilityException)
        {
            return "Live hiring policy is unavailable. Do not infer automatic hiring authority from conversation or memory.";
        }
    }

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
        if (turnId == Guid.Empty || !IsHiringPolicyChangeRequest(text)) return false;
        try
        {
            var policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            await AskHiringPolicyQuestionAsync(conversationId, policy with { SetupStage = "mode" }, context, token, turnId.ToString("N"));
            return true;
        }
        catch (PlatformCapabilityException) { return false; }
    }

    internal static bool IsHiringPolicyChangeRequest(string text)
    {
        text = text.Trim();
        // A question about policy is informational. Do not open setup from quoted history,
        // an answer's presentation text, or the assistant's response.
        if (text.StartsWith("Decision:", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "change ", "update ", "set ", "switch ", "please change ", "please update ", "please set ", "please switch ",
                "let's change ", "let’s change ", "i want to change ", "i would like to update ",
                "can you change ", "could you update ", "would you change ", "hire automatically", "automatically hire" }
                .Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            !(text.Contains("hire automatically", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("automatically hire", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("hiring", StringComparison.OrdinalIgnoreCase) &&
              (text.Contains("mode", StringComparison.OrdinalIgnoreCase) || text.Contains("autonomy", StringComparison.OrdinalIgnoreCase) || text.Contains("preferences", StringComparison.OrdinalIgnoreCase)))) return false;
        return true;
    }

    private sealed record HiringPolicyAnswerResult(Guid? QuestionMessageId,
        string Acknowledgment = "Your hiring preferences are saved.");

    private async Task<HiringPolicyAnswerResult?> HandleHiringPolicyAnswerAsync(Guid conversationId, Guid turnId, string text,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (turnId == Guid.Empty || !text.StartsWith("Decision: ", StringComparison.Ordinal) ||
            !(text.Contains("hiring", StringComparison.OrdinalIgnoreCase) || text.Contains("publisher", StringComparison.OrdinalIgnoreCase))) return null;
        HiringPolicyResponse policy;
        try
        {
            policy = await context.Platform.InvokeAsync<object, HiringPolicyResponse>(HiringAutonomyCapabilities.Read, new { }, token);
            policy = await context.Platform.InvokeAsync<CaptureHiringPolicyDecisionRequest, HiringPolicyResponse>(
                    HiringAutonomyCapabilities.CaptureDecision, new(Guid.Empty, policy.Revision, turnId), token);
        }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.ValidationFailed)
        {
            // The platform could not bind this answer to the current authorized decision.
            // Do not create another wizard or report a successful save from presentation text.
            return new(null, "That hiring choice is no longer current. Your preferences were not changed.");
        }
        catch (PlatformCapabilityException) { return null; }
        if (!policy.SetupComplete) return new(await AskHiringPolicyQuestionAsync(conversationId, policy, context, token));
        else
        {
            var operating = await _orchestrator.AssembleContextAsync(context, token);
            var focus = await BeginFocusOnboardingAsync(conversationId,
                operating, BusinessOperatingProfiles.Resolve(Settings), context, token, turnId);
            // Only this answer's own question message can complete its turn. Later settings
            // changes acknowledge the save without reopening focus or reusing an older turn.
            return new(focus.AnswerTurnId == turnId ? focus.MessageId : null);
        }
    }

    private static async Task<Guid> AskHiringPolicyQuestionAsync(Guid conversationId, HiringPolicyResponse policy,
        AgentRuntimeContext context, CancellationToken token, string? retry = null)
    {
        var (prompt, options, recommended) = policy.SetupStage switch
        {
            "level" => ("How much automatic hiring authority should I have?", new List<RequestUserInputOption>
            {
                new("approved", "Approved packages", "I’d use only agents and permissions you’ve already approved.",
                    "I could fill a role without asking again. You’d still decide whether to allow a new package or new access."),
                new("bounded", "Within limits", "I’d hire within the spending and access limits you set.",
                    "You’d set a budget per hire, a total budget, and the access I can grant. I could hire on my own inside those limits."),
                new("broad", "Broad delegation", "I could hire the leaders I recommend and fill approved team roles. I could install eligible packages and grant their requested permissions.",
                    "I wouldn’t ask you to review each hire first, so a new hire could get more access than you intended.")
            }, "approved"),
            "publishers" => ("Which publishers should I consider for hiring?", new List<RequestUserInputOption>
            {
                new("prefer", "Prefer first-party", "I’d look for a suitable C-Sweet agent first.",
                    "If I couldn’t find a good match there, I could still consider other publishers for your team."),
                new("first", "First-party only", "I’d consider only agents from C-Sweet.",
                    "If another publisher had a better match for your role, I’d leave that candidate out."),
                new("best", "Best fit", "I’d look for the closest match to your role across publishers.",
                    "I could pick an agent from another publisher even when C-Sweet has a candidate.")
            }, "prefer"),
            "costs" => ("What automatic hiring cost limits should I use?", new List<RequestUserInputOption>
            {
                new("free", "Free agents only", "I’d only hire agents with a package price of $0.",
                    "I couldn’t commit you to a paid package through automatic hiring."),
                new("skip", "Keep recommending", "I’d keep bringing candidates to you for review.",
                    "You’d approve each hire and could set a hiring budget later.")
            }, "free"),
            "permissions" => ("Which permissions may new hiring packages request?", new List<RequestUserInputOption>
            {
                new("none", "No capabilities or network access", "I’d only install new agents that need no special permissions or network access.",
                    "If you wanted an agent that needed access to a tool or website, I couldn’t install it automatically."),
                new("existing", "Permissions already approved here", "I’d stay within the access your active agents already have.",
                    "A new hire could use tools or network connections you’ve allowed here. I couldn’t introduce a new kind of access."),
                new("skip", "Keep recommending", "I’d keep bringing candidates to you for review.",
                    "You’d approve each hire and could choose access limits later in settings.")
            }, "none"),
            _ => ("How should I help with hiring?", new List<RequestUserInputOption>
            {
                new("recommend", "Recommend candidates", "I’d recommend someone for each role.",
                    "You’d review my pick and decide whether to hire them."),
                new("choose", "Choose candidates yourself", "I’d explain the role and show you matching candidates.",
                    "You’d compare the candidates and choose who to hire."),
                new("automatic", "Automatic hiring", "I could hire the leaders I recommend and fill approved team roles.",
                    "You’d choose how much authority to give me next. Then I could make eligible hires without asking about each one.")
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
        return id;
    }

    private async Task RecoverApprovedHiringAsync(AgentRuntimeContext context, CancellationToken token)
    {
        await RunResourceChangeAsync(Guid.Empty, () => ReconcileApprovedHiringAsync(context, token), context, token);
    }

    private async Task ReconcileApprovedHiringAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var plans = await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(Statuses: ["Approved"]), token);
        foreach (var plan in plans.Requests.Take(100))
            await RunResourceChangeAsync(plan.Id, () => ReconcileApprovedResourceChangeAsync(plan, context, token), context, token);
        var backlog = await context.Platform.ListHiringRecommendationsAsync(token);
        foreach (var recommendation in backlog.Recommendations.Where(x => x.FulfilledHeadcount < x.Headcount).Take(100))
            _ = await PrepareHiringCandidateAsync(recommendation.Id, context, token);
    }

    private async Task<bool> PrepareHiringCandidateAsync(Guid recommendationId, AgentRuntimeContext context, CancellationToken token)
    {
        var comparing = false;
        try
        {
            var selected = await context.Platform.InvokeAsync<SelectHiringCandidateRequest, HiringCandidateSelectionResponse>(
                HiringAutonomyCapabilities.SelectCandidate, new(recommendationId), token);
            if (selected.Brief is { } brief)
            {
                comparing = true;
                if (brief.Question is null || brief.Answer is not null)
                {
                    var decision = await CompareCandidatesAsync(brief, context, token);
                    selected = await context.Platform.InvokeAsync<SelectHiringCandidateRequest, HiringCandidateSelectionResponse>(
                        HiringAutonomyCapabilities.SelectCandidate, new(recommendationId) { Decision = decision }, token);
                }
                if (selected.Brief is { } pending)
                {
                    if (pending is { DispatchKey: { } key, PlanAuthorId: { } author, Question: { } question })
                        await context.Platform.Communication.SendDirectAgentMessageAsync(author,
                            $"{pending.Title} — {pending.ProjectGoal ?? pending.Objective}\n\n{question}", key, token);
                    return selected.Mode == HiringSelectionMode.RecommendCandidates;
                }
            }
            if (selected.Mode == HiringSelectionMode.Automatic && selected.Candidate is not null)
            {
                var result = await context.Platform.InvokeAsync<SubmitDelegatedHireRequest, DelegatedHireResponse>(
                    HiringAutonomyCapabilities.Submit, new(recommendationId), token);
                if (result.Status is "Succeeded" or "Starting" or "Queued" or "Building" or "CompletingHire") return false;
            }
        }
        catch (PlatformCapabilityException) when (!comparing) { /* Existing recommendations retain their marketplace action. */ }
        return true;
    }
}
