using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Agents.ChiefOfStaff.Agent;
using CSweet.Agents.ChiefOfStaff.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests.Agent;

public sealed class HiringCandidateJourneyTests(ITestOutputHelper output)
{
    // Selection-relevant listing fields from the C-Sweet first-party catalog. Both installed agents qualify for this slot.
    private static readonly AvailableAgent Daniel = new("catalog:001", "com.csweet.software-developer", AgentCatalogSource.FirstPartyCatalog, [],
        AgentAvailabilityState.AvailableToInstall, null, "Daniel Kim",
        "Implements production software from approved requirements while keeping changes reviewable, tested, and aligned with the product plan.",
        "C-Sweet", "Engineering", ["Software Developer", "Software Engineer", "Application Developer"],
        ["engineering", "implementation", "software delivery"], ["engineering.implementation", "github.repository", "software.delivery"],
        0, "USD", null, 0, null, null, 0.68m, "First party", "software-developer", "Software Developer",
        RoleCategoryKeys: ["software-developer"], SpecializationKeys: []);
    private static readonly AvailableAgent Adrian = new("catalog:999", "com.csweet.video-game-engineer", AgentCatalogSource.FirstPartyCatalog, [],
        AgentAvailabilityState.AvailableToInstall, null, "Adrian Silva",
        "Implements approved game and software requirements with the same planning, tested delivery, and compute recovery as the Video Game Engineer.",
        "C-Sweet", "Engineering", [],
        [], ["work.execution.build-readiness.v1", "work.execution.run", "software-development.implement.v1", "agent.configuration.describe.v1", "agent.configuration.update.v1"],
        0, "USD", null, 0, null, null, 0.68m, "First party", "game-engineer", "Video Game Engineer",
        RoleCategoryKeys: ["software-developer"], SpecializationKeys: ["video-game-development", "gameplay-programming", "gameplay-systems", "engine-integration", "automated-testing", "build-diagnostics"]);
    private const string Game = "Ship a playable breakout clone with paddle controls, ball collisions and a restartable level.";
    private const string Business = "Deliver an internal invoice reconciliation API with a database migration, audit history and operational reports.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_work_compares_every_profile_and_submits_once_after_restart(bool reverse)
    {
        var f = new Journey(Game, reverse);
        f.Model = (request, _) =>
        {
            var evidence = Evidence(request);
            Assert.Equal("work.execution.run", Assert.Single(evidence.GetProperty("verifiedForEveryCandidate").EnumerateArray()).GetString());
            var profiles = evidence.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(2, profiles.Length);
            Assert.All(profiles, c => Assert.DoesNotContain(c.GetProperty("additionalListedServices").EnumerateArray(),
                v => v.GetString() == "work.execution.run"));
            var game = profiles.Single(c => c.GetProperty("Name").GetString() == Adrian.Name);
            return Task.FromResult(Reply(game.GetProperty("index").GetInt32(), "The playable slice needs game controls and collision work."));
        };
        await f.DeliverAsync();
        await f.DeliverAsync();
        Assert.Equal(Adrian.AgentReference, f.Selected);
        Assert.Equal((1, 0, 1), (f.Calls, f.Messages, f.Hires));
    }

    [Fact]
    public async Task Decision_changing_question_goes_once_to_author_then_reply_changes_hire()
    {
        var f = new Journey("Staff the new client prototype. The initial deliverable is undecided.", false);
        const string question = "Which deliverable did the client agree to demonstrate first?";
        f.Model = (request, _) => Task.FromResult(f.Calls == 1
            ? Reply(0, "Daniel fits a reporting prototype.", question, 1, "A playable vertical slice favors Adrian.")
            : Evidence(request).GetProperty("authorAnswer").GetString() == "A playable vertical slice."
                ? Reply(1, "Adrian owns the interaction and rendering work the author confirmed.")
                : throw new InvalidOperationException("Missing the authenticated author's answer."));
        await f.DeliverAsync();
        await f.DeliverAsync();
        Assert.Equal((1, 1, 0), (f.Calls, f.Messages, f.Hires));
        Assert.Contains(question, f.SentContent);
        f.Answer("A playable vertical slice.");
        await f.DeliverAsync();
        await f.DeliverAsync();
        Assert.Equal(Adrian.AgentReference, f.Selected);
        Assert.Equal((2, 1, 1), (f.Calls, f.Messages, f.Hires));
    }

    [Fact]
    public async Task Same_candidate_under_both_answers_does_not_send_a_question()
    {
        var f = new Journey(Game, false);
        f.Model = (_, _) => Task.FromResult(f.Calls == 1
            ? Reply(1, "Gameplay work.", "Which color is the paddle?", 1, "Adrian in both cases.")
            : Reply(1, "The color has no bearing on the hire."));
        await f.DeliverAsync();
        Assert.Equal((2, 0, 1), (f.Calls, f.Messages, f.Hires));
        Assert.Equal(Adrian.AgentReference, f.Selected);
    }

    [LocalHiringInference]
    public async Task Local_model_selects_by_work_with_candidate_order_reversed()
    {
        var endpoint = Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_ENDPOINT")!.TrimEnd('/') + "/chat/completions";
        var model = Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_MODEL")!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var (brief, expected) in new[] { (Game, Adrian), (Business, Daniel) })
        foreach (var reverse in new[] { false, true })
        {
            var f = new Journey(brief, reverse);
            f.Model = async (request, token) =>
            {
                var messages = request.GetProperty("messages").EnumerateArray().Select(m => new {
                    role = m.GetProperty("role").GetString(), content = m.GetProperty("text").GetString() }).ToArray();
                using var response = await http.PostAsJsonAsync(endpoint, new { model, messages, temperature = 0,
                    max_tokens = 4096, response_format = new { type = "json_schema",
                        json_schema = new { name = "hiring_fit", schema = request.GetProperty("responseSchema") } } }, token);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                return content.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            };
            await f.DeliverAsync();
            await f.DeliverAsync();
            output.WriteLine($"{model} | {brief} | {(reverse ? "Adrian, Daniel" : "Daniel, Adrian")} | {f.Selected} | {f.Reason}");
            Assert.Equal(expected.AgentReference, f.Selected);
            Assert.Equal((1, 0, 1), (f.Calls, f.Messages, f.Hires));
        }
    }

    private sealed class LocalHiringInferenceAttribute : FactAttribute
    {
        public LocalHiringInferenceAttribute()
        {
            if (new[] { "CSWEET_HIRING_TEST_ENDPOINT", "CSWEET_HIRING_TEST_MODEL" }.Any(k => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(k))))
                Skip = "Set the local inference URL and model to run the real-provider check.";
        }
    }

    private static JsonElement Evidence(JsonElement request) =>
        JsonSerializer.Deserialize<JsonElement>(request.GetProperty("messages")[1].GetProperty("text").GetString()!);

    private static string Reply(int candidate, string reason, string? question = null, int? alternative = null,
        string? alternativeCondition = null) => JsonSerializer.Serialize(new { candidate, reason, question, alternative, alternativeCondition });

    private sealed class Journey
    {
        private readonly Guid id = Guid.NewGuid(), author = Guid.NewGuid(), delivery = Guid.NewGuid();
        private readonly AgentTestRuntime platform = new();
        private HiringCandidateBrief brief;
        private AvailableAgent? chosen;
        public Func<JsonElement, CancellationToken, Task<string>> Model { get; set; } = null!;
        public int Calls { get; private set; }
        public int Hires { get; private set; }
        public int Messages { get; private set; }
        public string? SentContent { get; private set; }
        public string? Selected => chosen?.AgentReference;
        public string? Reason { get; private set; }

        public Journey(string project, bool reverse)
        {
            brief = new("current", "Development engineer", "Own first-sprint implementation.", project, null, [],
                ["work.execution.run"], [], HiringPublisherPreference.BestFit, reverse ? [Adrian, Daniel] : [Daniel, Adrian],
                author, null, null, null);
            platform.RegisterCapability<SelectHiringCandidateRequest, HiringCandidateSelectionResponse>(HiringAutonomyCapabilities.SelectCandidate,
                (r, _) =>
                {
                    Assert.Equal(id, r.RecommendationId);
                    if (r.Decision is { } d)
                    {
                        Assert.Equal(brief.Evidence, d.Evidence);
                        if (d.Question is null)
                        {
                            chosen = brief.Candidates.Single(c => c.AgentReference == d.CandidateReference);
                            Reason = d.Rationale;
                        }
                        else
                        {
                            Assert.Null(brief.Question);
                            Assert.NotEqual(d.CandidateReference, d.AlternativeReference);
                            brief = brief with { Question = d.Question, DispatchKey = "author-question" };
                        }
                    }
                    return Task.FromResult(new HiringCandidateSelectionResponse(id, HiringSelectionMode.Automatic,
                        Hires == 0 ? chosen : null, Reason ?? "Pending") { Brief = chosen is null ? brief : null });
                })
                .RegisterCapability<SubmitDelegatedHireRequest, DelegatedHireResponse>(HiringAutonomyCapabilities.Submit, (r, _) =>
                {
                    Assert.Equal(id, r.RecommendationId);
                    Hires++;
                    return Task.FromResult(new DelegatedHireResponse(id, null, "Succeeded", null));
                })
                .RegisterCapability<CreateCommunicationChat, JsonElement>(CommunicationCapabilities.ChatCreate, (r, _) =>
                {
                    Assert.Equal(author, Assert.Single(r.ParticipantOrganizationUserIds));
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { chat = new { id, title = "Direct",
                        participants = new[] { new { organizationUserId = author, employeeType = "Agent", displayName = "Plan author", role = "Author" } } } }));
                })
                .RegisterCapability<JsonElement, JsonElement>(CommunicationCapabilities.MessageSend, (r, _) =>
                {
                    Messages++;
                    Assert.Equal("author-question", r.GetProperty("idempotencyKey").GetString());
                    SentContent = r.GetProperty("content").GetString();
                    brief = brief with { DispatchKey = null };
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), chatId = id, chatTurnId = Guid.NewGuid() }));
                })
                .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, async (r, ct) =>
                {
                    Calls++;
                    return JsonSerializer.SerializeToElement(new { role = "assistant", text = await Model(r, ct), finishReason = "stop" });
                });
        }

        public void Answer(string answer) => brief = brief with { Answer = answer, Evidence = "with-answer" };

        public async Task DeliverAsync()
        {
            var agent = new ChiefOfStaffAgent(NullLogger<ChiefOfStaffAgent>.Instance,
                new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
            var host = new AgentTestHost(agent, platform);
            var settings = new Dictionary<string, JsonElement> {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["llmModel"] = JsonSerializer.SerializeToElement("test") };
            await host.ApplyConfigurationAsync(new(Guid.NewGuid(), "1.0", settings, settings.Keys.ToArray(), 1, "fixture"));
            var result = await host.DeliverEventAsync(HiringCandidateReviewWake.EventType, new HiringCandidateReviewWake(id), delivery);
            Assert.True(result.Succeeded, result.Error);
        }
    }
}
