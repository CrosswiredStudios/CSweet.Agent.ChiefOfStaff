using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    private const string ResourceReviewPrefix = "chief-resource-review:";
    private sealed class ChiefWorkBlockedException(string code, string reason) : Exception(reason)
    {
        public string Code { get; } = code;
    }

    private async Task DispatchResourceChangeAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
    {
        ResourceChangeDecisionEvent? hint;
        try { hint = DeserializePayload<ResourceChangeDecisionEvent>(message.Payload); }
        catch (JsonException) { return; }
        if (hint is null || hint.RequestId == Guid.Empty || hint.OrganizationId == Guid.Empty ||
            !Guid.TryParse(context.BusinessId, out var organization) || organization != hint.OrganizationId) return;
        await RunResourceChangeAsync(hint.RequestId, async () =>
        {
            if (message.EventType == ManagementEvents.ResourceChangeRequested)
                await HandleResourceChangeRequestedAsync(message, context, token);
            else
                await HandleResourceChangeDecidedAsync(message, context, token);
        }, context, token);
    }

    // Only expected permanent failures become review work. Cancellation, transport failures,
    // unavailable services and unexpected defects remain failures of the durable delivery.
    private async Task<bool> RunResourceChangeAsync(Guid requestId, Func<Task> work, AgentRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await work(); }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.Conflict)
        {
            // The command used a stale revision. Recovery reads current approved requests again.
            return false;
        }
        catch (PlatformCapabilityException exception) when (exception.Code is PlatformCapabilityErrorCode.Denied or
            PlatformCapabilityErrorCode.NotFound or PlatformCapabilityErrorCode.ValidationFailed or
            PlatformCapabilityErrorCode.ApprovalRequired or PlatformCapabilityErrorCode.BudgetExceeded)
        {
            await SaveResourceBlockAsync(requestId, "chief.resource_" + exception.Code.ToString().ToLowerInvariant(),
                $"Resource-change follow-through is blocked ({exception.Code}). Review current access, approval, budget and request data, then retry this task.", context, token);
            return false;
        }
        catch (ChiefWorkBlockedException exception)
        {
            await SaveResourceBlockAsync(requestId, exception.Code, exception.Message, context, token);
            return false;
        }
        var key = ResourceReviewPrefix + requestId.ToString("N");
        var stored = await context.Platform.ReadOperatingStateAsync(new(key), token);
        if (stored?.Status == "Blocked")
        {
            await context.Platform.WriteOperatingStateAsync(new(key, stored.SchemaId, stored.SchemaVersion, "Resolved",
                stored.SourceRevisions, [], key, [], Guid.Empty, stored.Payload, stored.Revision,
                $"{key}:resolved:{stored.Revision}"), token);
            // Repair an interrupted resolution projection on the next reconciliation too.
        }
        if (stored is not null)
        {
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            var item = directory.Boards.SelectMany(x => x.Items).SingleOrDefault(x => x.CorrelationId == key && x.ArchivedAt is null &&
                x.Status is not (PersonalTodoStatuses.Completed or PersonalTodoStatuses.Cancelled));
            if (item?.Status == PersonalTodoStatuses.Blocked)
                await context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision, $"{key}:resume:{item.Revision}"), token);
        }
        return true;
    }

    private async Task SaveResourceBlockAsync(Guid requestId, string code, string reason, AgentRuntimeContext context, CancellationToken token)
    {
        var key = ResourceReviewPrefix + requestId.ToString("N");
        var stored = await context.Platform.ReadOperatingStateAsync(new(key), token);
        if (stored?.Status != "Blocked" || !stored.ConditionCodes.Contains(code))
            stored = await context.Platform.WriteOperatingStateAsync(new(key, "com.csweet.chief.resource-review", 1, "Blocked",
                new Dictionary<string, string>(), [code], key, [key], Guid.Empty,
                SerializePayload(new { requestId, reason }), stored?.Revision, $"{key}:block:{stored?.Revision ?? 0}:{code}"), token);
        // Persist source first; a crash before projection retries the same card creation key.
        var directory = await context.Platform.PersonalTodo.ListAsync(token);
        if (!directory.Boards.SelectMany(x => x.Items).Any(x => x.CorrelationId == key && x.ArchivedAt is null &&
            x.Status is not (PersonalTodoStatuses.Completed or PersonalTodoStatuses.Cancelled)))
            await context.Platform.PersonalTodo.AddAsync(new("Review blocked resource-change follow-through", reason,
                WorkPriorities.High, null, $"{key}:card:{stored!.Revision}", CorrelationId: key), token);
        _logger.LogWarning("Resource request {RequestId} requires review: {Code}.", requestId, code);
    }

    private async Task<PersonalTodoResult> RetryResourceReviewAsync(string correlation, AgentRuntimeContext context, CancellationToken token)
    {
        if (!Guid.TryParseExact(correlation[ResourceReviewPrefix.Length..], "N", out var requestId))
            return PersonalTodoResult.Blocked("The resource-review correlation is invalid.");
        var succeeded = await RunResourceChangeAsync(requestId, async () =>
        {
            var read = await context.Platform.ReadResourceChangesAsync(new ResourceChangeReadRequest(requestId), token);
            var request = read.Requests.SingleOrDefault(x => x.Id == requestId);
            if (request is null) throw new ChiefWorkBlockedException("chief.resource_change_missing", "The resource change is unavailable. Review its visibility before retrying this task.");
            if (request.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                await ReconcileApprovedResourceChangeAsync(request, context, token);
            else if (request.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                throw new ChiefWorkBlockedException("chief.resource_approval_pending", "The resource change is awaiting its manager's decision.");
            // Superseded/rejected/withdrawn source work is settled without applying old effects.
        }, context, token);
        return succeeded ? PersonalTodoResult.Completed("Current resource-change follow-through is settled.")
            : PersonalTodoResult.Blocked("Resource-change follow-through still requires review. See the persisted block reason.");
    }
}
