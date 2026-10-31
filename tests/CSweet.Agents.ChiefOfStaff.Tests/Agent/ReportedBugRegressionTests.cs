using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Contracts;
using CSweet.Agents.ChiefOfStaff.Formatting;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests.Agent;

public sealed class ReportedBugRegressionTests
{
    [Theory]
    [InlineData("Sound good?")]
    [InlineData("Okay with that?")]
    [InlineData("Any concerns?")]
    [InlineData("Shall I proceed?")]
    [InlineData("Want me to start?")]
    [InlineData("Ready?")]
    public void RecommendationPreservesExplanationBeforeUnlistedQuestion(string question)
    {
        const string explanation = "The Producer owns the milestone plan and keeps team spend under your monthly cap.";
        var formatter = new ResponsePolicyFormatter();
        var response = $"Priority 1 Hire: Game Producer.\n{explanation} {question}";
        Assert.Equal($"Priority 1 Hire: Game Producer.\n{explanation}", formatter.EnforceResponseMode(response));
        Assert.Contains(explanation, formatter.FormatOnboardingMessage(response));
        Assert.DoesNotContain(question, formatter.FormatOnboardingMessage(response));
    }

    [Theory]
    [InlineData(". ", "Sound good?")]
    [InlineData("; ", "Who should own delivery?")]
    [InlineData(": ", "Any concerns?")]
    public void InlineRecommendationUsesSameSegmentationForDetectionAndFiltering(string separator, string question)
    {
        var response = $"I recommend a Producer{separator}{question}";
        Assert.Equal($"I recommend a Producer{separator.TrimEnd()}", new ResponsePolicyFormatter().EnforceResponseMode(response));
    }

    [Fact]
    public void QuestionFirstAndMultipleStatementsRetainOnlyStatements()
    {
        const string response = "Ready? I recommend a Producer. They own delivery! Any concerns? Budget remains capped.";
        Assert.Equal("I recommend a Producer. They own delivery! Budget remains capped.", new ResponsePolicyFormatter().EnforceResponseMode(response));
    }

    [Theory]
    [InlineData("What should our first hire own? Sound good?")]
    [InlineData("Before I can recommend a first hire, I need context. What business are you building?")]
    [InlineData("Priority 1 Hire: Who should fill this role?")]
    public void ClarificationWithoutRecommendationIsUnchanged(string response) =>
        Assert.Equal(response, new ResponsePolicyFormatter().EnforceResponseMode(response));

    [Fact]
    public void QuestionWithTitleIsRemovedWithoutLeavingAFragment()
    {
        const string response = "I recommend a Producer. Could Dr. Smith own delivery?";
        Assert.Equal("I recommend a Producer.", new ResponsePolicyFormatter().EnforceResponseMode(response));
    }

    [Fact]
    public void LabelsAndDecimalBudgetSurviveQuestionRemoval()
    {
        const string statements = "Priority 1 Hire: Game Producer. Budget: $2500.50; ownership: delivery.";
        Assert.Equal(statements, new ResponsePolicyFormatter().EnforceResponseMode($"{statements} Ready?"));
    }

    [Theory]
    [InlineData("targetCustomers", "[\"parents of young children\"]", "cozy farming game")]
    [InlineData("businessType", "\"consultancy\"", "a")]
    [InlineData("businessType", "\"studio\"", "we")]
    [InlineData("targetCustomers", "[\"cozy farming game\",\"parents\"]", "cozy farming game")]
    [InlineData("businessType", "\"game studio\"", "two-person studio")]
    [InlineData("businessType", "42", "two-person studio")]
    [InlineData("businessType", "[\"studio\"]", "two-person studio")]
    [InlineData("targetCustomers", "\"cozy farming game\"", "cozy farming game")]
    [InlineData("tools", "[]", "cozy farming game")]
    [InlineData("tools", "[null]", "cozy farming game")]
    [InlineData("businessType", "\"stud\"", "two-person studio")]
    [InlineData("businessType", "\"\"", "two-person studio")]
    [InlineData("businessType", "null", "two-person studio")]
    [InlineData("businessType", "{\"name\":\"studio\"}", "two-person studio")]
    [InlineData("businessType", "\"studio\"", "a different studio")]
    [InlineData("mission", "\"cozy farming game\"", "cozy farming game")]
    [InlineData("budget", "\"cozy farming game\"", "cozy farming game")]
    [InlineData("businessType", "\"studio\"", "")]
    public async Task UnsupportedOrMalformedFactMakesNoExplicitUpdate(string field, string value, string evidence)
    {
        var result = await CaptureAsync(field, value, evidence);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("businessType", "\"studio\"", "two-person studio")]
    [InlineData("offerings", "[\"cozy farming game\"]", "cozy farming game")]
    [InlineData("offerings", "[\"COZY   FARMING game\"]", "cozy farming game")]
    [InlineData("offerings", "[\"cozy\",\"farming game\"]", "cozy farming game")]
    [InlineData("businessType", "\"studio\"", "two-person\tstudio")]
    public async Task ExtractiveTypedFactPreservesSourceAndRevision(string field, string value, string evidence)
    {
        var request = Assert.Single(await CaptureAsync(field, value, evidence));
        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(value, request.Changes[field].GetRawText());
        Assert.Equal($"explicit-facts:{request.MessageId}", request.IdempotencyKey);
        Assert.True(Guid.TryParse(request.ConversationId, out _));
        Assert.True(Guid.TryParse(request.UserId, out _));
    }

    [Fact]
    public async Task InvalidFactDoesNotDiscardAnIndependentValidFact()
    {
        var request = Assert.Single(await CaptureAsync("targetCustomers", "[\"parents\"]", "cozy farming game",
            additionalFact: """,{"field":"businessType","value":"studio","evidence":"two-person studio"}"""));
        Assert.Single(request.Changes);
        Assert.Equal("studio", request.Changes["businessType"].GetString());
    }

    [Fact]
    public async Task EvidenceInConversationHistoryButAbsentFromCurrentMessageIsRejected()
    {
        Assert.Empty(await CaptureAsync("businessType", "\"studio\"", "two-person studio",
            message: "Earlier: two-person studio\n<current_user_message>What time is it?</current_user_message>"));
    }

    private static async Task<List<ExplicitBusinessProfileUpdateRequest>> CaptureAsync(string field, string value, string evidence,
        string message = "We're a two-person studio making a cozy farming game for Switch.", string additionalFact = "")
    {
        var writes = new List<ExplicitBusinessProfileUpdateRequest>();
        var runtime = new AgentTestRuntime().RegisterCapability<ExplicitBusinessProfileUpdateRequest, MutationResponse>(
            PlatformCapabilities.BusinessProfileUpdateExplicit, (request, _) =>
            {
                writes.Add(request);
                return Task.FromResult(new MutationResponse(true, 8, null, null));
            });
        var profile = new BusinessProfileResponse(Guid.NewGuid(), "Example", null, null, null, null, null,
            [], [], null, [], null, [], [], null, "UTC", 7, 0, new Dictionary<string, ProfileFieldProvenance>());
        var context = new ChiefOperatingContext(profile, null, null, null, null, null, []);
        var input = new AssistantCapabilityInput(Guid.NewGuid(), Guid.NewGuid().ToString("D"),
            message, null, Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var json = $$"""{"facts":[{"field":"{{field}}","value":{{value}},"evidence":{{JsonSerializer.Serialize(evidence)}}}{{additionalFact}}]}""";
        await new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance)
            .CaptureExplicitFactsAsync(new Model(json), input, context, runtime.CreateContext(), CancellationToken.None);
        return writes;
    }

    private sealed class Model(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
