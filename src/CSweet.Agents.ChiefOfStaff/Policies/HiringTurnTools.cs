using Microsoft.Extensions.AI;

namespace CSweet.Agents.ChiefOfStaff.Policies;

// One response may ask an essential question or change staffing, never both.
// Guard the tool effects before persistence; formatting the reply is too late.
internal sealed class HiringTurnTools
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool staffingChanged;
    private bool questionAsked;

    public bool QuestionAsked => questionAsked;

    public AITool Wrap(AITool tool) => tool is AIFunction function &&
        function.Name is "ask_user" or "upsert_hiring_recommendation" or "withdraw_hiring_recommendation" or
            "resolve_hiring_recommendation" or "propose_workforce_plan" or "propose_resource_change" or
            "decide_resource_change" or "stage_hiring_workflow" or "select_hiring_candidate" or "submit_delegated_hire"
            ? new GuardedFunction(function, this) : tool;

    private sealed class GuardedFunction(AIFunction inner, HiringTurnTools turn) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await turn.gate.WaitAsync(cancellationToken);
            try
            {
                var asking = Name == "ask_user";
                if (asking && turn.questionAsked)
                    return new { status = "not_created", reason = "One essential question is already awaiting the owner. Do not create another question in this response." };
                if (asking && turn.staffingChanged)
                    return new { status = "not_created", reason = "Staffing has already changed in this response. Report the result without another question. The deterministic runtime handles candidate selection and delegated hiring." };
                if (!asking && turn.questionAsked)
                    return new { status = "not_changed", reason = "An essential question is already awaiting the owner. Wait for that answer before changing staffing." };
                var result = await InnerFunction.InvokeAsync(arguments, cancellationToken);
                if (asking) turn.questionAsked = true;
                else turn.staffingChanged = true;
                return result;
            }
            finally { turn.gate.Release(); }
        }
    }
}
