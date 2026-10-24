using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Contracts;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests.Agent;

public sealed class TargetModelLeadershipTests(ITestOutputHelper output)
{
    [TargetModelTheory]
    [InlineData("ceo-only")]
    [InlineData("existing-product-manager")]
    [InlineData("game-studio")]
    [InlineData("licensed-legal")]
    public async Task CurrentTargetModelChoosesSupportedCoverageWithoutInventingAuthority(string scenario)
    {
        var owner = Guid.NewGuid(); var assigned = Guid.NewGuid();
        var game = scenario == "game-studio";
        var domain = game ? "creative-product" : scenario == "licensed-legal" ? "legal" : "product";
        var row = new ChiefCoverageAssessment(Guid.NewGuid(), game ? "game-studio" : "general", 1, domain, domain,
            "Assess documented accountability, expertise and available capacity.", 1, "Unassessed", "Ready", 1,
            null, null, null, null, null, null, null, null, null, null);
        ChiefCoverageAssignment[] assignments = [new(owner, null, true, ["chief-executive-officer"])];
        if (scenario is "existing-product-manager" or "game-studio")
            assignments = [.. assignments, new(assigned, Guid.NewGuid(), false, [game ? "creative-director" : "product-manager"])];
        var state = new ChiefLeadershipSnapshot(1, new(Guid.NewGuid(), 1, domain, null, owner, Guid.NewGuid(),
            Guid.NewGuid(), null, DateTimeOffset.UtcNow, null), [row], assignments, true, false, Guid.NewGuid(), row.Id, "fixture");
        var facts = scenario switch
        {
            "ceo-only" => "The CEO has not accepted product accountability and has no documented product expertise or capacity. No product responsibility is assigned. Ask who will own product decisions before recommending staffing.",
            "existing-product-manager" => "The current product-manager assignment has explicit product accountability, relevant expertise and sufficient capacity for this company's current product scope. The owner approved this existing responsibility. No additional hire is needed.",
            "game-studio" => "The current creative-director assignment explicitly owns creative and product decisions, has relevant expertise and sufficient capacity for this game. The director owns subordinate team design. No company-level Product Manager or Producer is needed.",
            _ => "The company needs jurisdiction-specific legal representation requiring a licensed human lawyer. No current employee has that license. An agent cannot provide the required professional engagement. Ask the owner about an external professional."
        };
        var operating = new ChiefOperatingContext(new(Guid.NewGuid(), "Disposable acceptance fixture", null,
            game ? "Video games" : "Software", facts, null, "Early", [], [], null, [], null, [], [], null, "UTC", 1, 1,
            new Dictionary<string, ProfileFieldProvenance>()),
            null, null, null, null, null, []);
        using var model = new LocalTargetModel(output);
        var timer = Stopwatch.StartNew();
        var result = await ChiefOfStaffAgent.InterpretCoverageAsync(model, row, state, operating, default);
        output.WriteLine(JsonSerializer.Serialize(new { scenario, elapsedMs = timer.ElapsedMilliseconds, calls = model.Calls, result }));
        Assert.InRange(model.Calls, 1, 2);
        Assert.NotEqual("Blocked", result.NextAction);
        if (scenario is "existing-product-manager" or "game-studio")
        {
            Assert.Equal("None", result.NextAction); Assert.Equal("AdequatelyCovered", result.CoverageStatus);
            Assert.Equal(assigned, result.AccountableOwnerId); Assert.Contains(assigned, result.EvidenceOrganizationUserIds);
        }
        else
        {
            Assert.Contains(result.NextAction, new[] { "Question", "External" });
            Assert.NotEqual("AdequatelyCovered", result.CoverageStatus);
            Assert.Null(result.AccountableOwnerId);
            Assert.Null(result.RoleKey);
        }
    }

    private sealed class TargetModelTheoryAttribute : TheoryAttribute
    {
        public TargetModelTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_CHIEF_MODEL_URL")))
                Skip = "Set CSWEET_CHIEF_MODEL_URL and CSWEET_CHIEF_MODEL for explicit local target-model evaluations.";
        }
    }

    // Only synthetic fixtures are sent; no provider credentials, live company facts or private reasoning.
    private sealed class LocalTargetModel(ITestOutputHelper output) : IChatClient
    {
        private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
        public int Calls;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var endpoint = new Uri(Environment.GetEnvironmentVariable("CSWEET_CHIEF_MODEL_URL")!.TrimEnd('/') + "/chat/completions");
            Assert.True(endpoint.IsLoopback, "This opt-in fixture only permits a local model endpoint.");
            Calls++;
            using var response = await client.PostAsJsonAsync(endpoint, new {
                model = Environment.GetEnvironmentVariable("CSWEET_CHIEF_MODEL"),
                messages = messages.Select(x => new { role = x.Role.Value, content = x.Text }),
                max_tokens = options?.MaxOutputTokens ?? 3000, temperature = 0
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("usage", out var usage)) output.WriteLine("providerUsage=" + usage.GetRawText());
            var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return new(new ChatMessage(ChatRole.Assistant, content));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() => client.Dispose();
    }
}
