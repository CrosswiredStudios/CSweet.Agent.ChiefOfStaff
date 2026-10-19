using CSweet.Agents.ChiefOfStaff.Policies;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.ChiefOfStaff.Tests.Policies;

public sealed class HiringTurnToolsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuestionAndStaffingChangeCannotBothPersistInOneTurn(bool questionFirst)
    {
        var questions = 0; var hires = 0;
        var guard = new HiringTurnTools();
        var question = (AIFunction)guard.Wrap(AIFunctionFactory.Create(() => ++questions, "ask_user"));
        var hire = (AIFunction)guard.Wrap(AIFunctionFactory.Create(() => ++hires, "upsert_hiring_recommendation"));
        await (questionFirst ? question : hire).InvokeAsync(new());
        await (questionFirst ? hire : question).InvokeAsync(new());
        if (questionFirst) await question.InvokeAsync(new());
        Assert.Equal(questionFirst ? 1 : 0, questions);
        Assert.Equal(questionFirst ? 0 : 1, hires);
        // Schema and identity remain the platform tool's exact contract.
        Assert.Equal("ask_user", question.Name);
        Assert.Equal("object", question.JsonSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task StaffingFailureStillAllowsAnEssentialQuestion()
    {
        var guard = new HiringTurnTools();
        var hire = (AIFunction)guard.Wrap(AIFunctionFactory.Create((Func<int>)(() => throw new InvalidOperationException("Missing role")), "upsert_hiring_recommendation"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hire.InvokeAsync(new()).AsTask());
        var asked = false;
        var question = (AIFunction)guard.Wrap(AIFunctionFactory.Create(() => asked = true, "ask_user"));
        await question.InvokeAsync(new());
        Assert.True(asked);
    }
}
