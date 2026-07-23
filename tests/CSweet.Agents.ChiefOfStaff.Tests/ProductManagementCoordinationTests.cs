using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.Contracts.Grpc;
using CSweet.Agent.SDK;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ProductManagementCoordinationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RoleBrief_ValidatesDirectReport_AndReturnsExecutiveGap()
    {
        var fixture = Fixture();
        var broker = new CoordinationBroker(fixture.Snapshot, fixture.Profile, finance: null);
        var agent = Agent();
        var request = new ProductRoleBriefRequest(
            fixture.ProductManagerId,
            fixture.ProductInstallationId,
            Guid.NewGuid(),
            "brief-1");

        var result = await agent.ExecuteCapabilityAsync(
            Capability(ProductManagementCapabilities.RoleBrief, ProductManagerProfileId, request),
            Runtime(fixture, broker),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var brief = JsonSerializer.Deserialize<ProductRoleBriefResponse>(result.Payload, JsonOptions)!;
        Assert.Equal("AwaitingExecutiveInput", brief.Status);
        Assert.Contains(brief.MissingInformation, gap => gap.Key == "workforce-budget");
        Assert.Equal(fixture.ChiefId, brief.ChiefOrganizationUserId);
    }

    [Fact]
    public async Task PlanReview_MaintainsChiefOwnedHiringBacklog()
    {
        var fixture = Fixture();
        var broker = new CoordinationBroker(fixture.Snapshot, fixture.Profile, Finance(fixture.OrganizationId));
        var plan = new ProductPlanResponse(
            "Use a lean product pod.",
            ["Validate demand"],
            ["Discovery", "Delivery"],
            [
                new ProductTeamRole("Product Designer", "Own discovery and interaction design.", "Product Manager", "Now", 1),
                new ProductTeamRole("Quality Engineer", "Own independent quality evidence.", "Product Manager", "Next", 2)
            ],
            ["Product Designer", "Quality Engineer"],
            [],
            [],
            [],
            [],
            3,
            DateTimeOffset.UtcNow);
        var request = new ProductPlanReviewRequest(
            fixture.ProductManagerId,
            fixture.ProductInstallationId,
            plan,
            Guid.NewGuid(),
            "review-1");

        var result = await Agent().ExecuteCapabilityAsync(
            Capability(ProductManagementCapabilities.PlanReview, ProductManagerProfileId, request),
            Runtime(fixture, broker),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, broker.Requests.Count(x => x.Capability == PlatformCapabilities.HiringRecommendationUpsert));
        var priorities = broker.Requests
            .Where(x => x.Capability == PlatformCapabilities.HiringRecommendationUpsert)
            .Select(x => JsonSerializer.Deserialize<UpsertHiringRecommendationRequest>(x.Payload.Span, JsonOptions)!.Priority)
            .ToArray();
        Assert.Equal([1, 2], priorities);
    }

    [Fact]
    public async Task Escalation_IsSentByChiefToProtectedHumanConversation()
    {
        var fixture = Fixture();
        var broker = new CoordinationBroker(fixture.Snapshot, fixture.Profile, Finance(fixture.OrganizationId))
        {
            CommunicationHub = new CommunicationHubResponse(
                fixture.ChiefId,
                true,
                [
                    new CommunicationChatResponse(
                        fixture.OwnerChatId,
                        "CEO",
                        null,
                        true,
                        true,
                        true,
                        false,
                        DateTimeOffset.UtcNow,
                        [
                            new CommunicationParticipantResponse(fixture.ChiefId, "Chief", "Agent", "Member"),
                            new CommunicationParticipantResponse(Guid.NewGuid(), "CEO", "Human", "Coordinator")
                        ],
                        null,
                        null,
                        0)
                ],
                [],
                [])
        };
        var escalation = new ProductEscalationRequest(
            fixture.ProductManagerId,
            fixture.ProductInstallationId,
            "target-customer",
            "Which customer segment should we serve first?",
            "It changes product priorities.",
            [],
            null,
            Guid.NewGuid(),
            "gap-1");

        var result = await Agent().ExecuteCapabilityAsync(
            Capability(ProductManagementCapabilities.Escalation, ProductManagerProfileId, escalation),
            Runtime(fixture, broker),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var send = Assert.Single(broker.Requests, x => x.Capability == ChiefOfStaffProfile.SendCommunicationMessageCapability);
        var payload = JsonSerializer.Deserialize<SendCommunicationMessageRequest>(send.Payload.Span, JsonOptions)!;
        Assert.Equal(fixture.OwnerChatId, payload.ChatId);
        Assert.Contains("Product Manager needs one executive answer", payload.Content);
        Assert.Equal("gap-1", payload.IdempotencyKey);
    }

    [Fact]
    public async Task Coordination_RejectsProductManagerThatDoesNotReportToChief()
    {
        var fixture = Fixture();
        var broker = new CoordinationBroker(fixture.Snapshot, fixture.Profile, Finance(fixture.OrganizationId));
        var request = new ProductRoleBriefRequest(
            Guid.NewGuid(),
            fixture.ProductInstallationId,
            Guid.NewGuid(),
            "brief-invalid");

        var result = await Agent().ExecuteCapabilityAsync(
            Capability(ProductManagementCapabilities.RoleBrief, ProductManagerProfileId, request),
            Runtime(fixture, broker),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(broker.Requests, x => x.Capability == ChiefOfStaffProfile.SendCommunicationMessageCapability);
    }

    [Fact]
    public async Task Consultation_TargetsExactProductManagerInstallation()
    {
        var fixture = Fixture();
        var expected = new ProductPlanResponse(
            "Use a lean product pod.", [], [], [], [], [], [], [], [], 3, DateTimeOffset.UtcNow);
        var broker = new CoordinationBroker(fixture.Snapshot, fixture.Profile, Finance(fixture.OrganizationId))
        {
            ProductPlan = expected
        };
        var operatingContext = new ChiefOperatingContext(
            fixture.Profile,
            Finance(fixture.OrganizationId),
            fixture.Snapshot,
            null,
            null,
            null,
            []);
        var input = new AssistantCapabilityInput(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            "How should we structure the product team?",
            null,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid());

        var actual = await ChiefOfStaffAgent.ConsultProductManagerAsync(
            "Recommend the initial product team.",
            input,
            operatingContext,
            Runtime(fixture, broker),
            CancellationToken.None);

        Assert.Equal(expected.Recommendation, actual.Recommendation);
        var consultation = Assert.Single(broker.Requests, x => x.Capability == ProductManagementCapabilities.Plan);
        Assert.Equal($"installation:{fixture.ProductInstallationId:D}", consultation.TargetAgentId);
    }

    private const string ProductManagerProfileId = "com.csweet.product-manager";

    private static ChiefOfStaffAgent Agent() => new(
        NullLogger<ChiefOfStaffAgent>.Instance,
        new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));

    private static CapabilityRequest Capability<T>(string capability, string requestingAgentId, T payload) => new()
    {
        Capability = capability,
        RequestingAgentId = requestingAgentId,
        ContentType = "application/json",
        Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
    };

    private static AgentRuntimeContext Runtime(FixtureData fixture, IAgentBrokerClient broker) =>
        new(
            fixture.OrganizationId.ToString("D"),
            fixture.ChiefInstallationId.ToString("D"),
            broker)
        {
            Identity = new AgentIdentity(
                fixture.ChiefId.ToString("D"),
                "Chief of Staff",
                null,
                "Chief of Staff",
                null,
                [],
                "Manager",
                null,
                null)
        };

    private static FixtureData Fixture()
    {
        var organizationId = Guid.NewGuid();
        var chiefId = Guid.NewGuid();
        var chiefInstallationId = Guid.NewGuid();
        var productManagerId = Guid.NewGuid();
        var productInstallationId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var snapshot = new OrganizationSnapshotResponse(
            organizationId,
            "Active",
            [
                new OrganizationPerson(chiefId, "Chief of Staff", "Agent", null, null, chiefInstallationId, true),
                new OrganizationPerson(productManagerId, "Product Manager", "Agent", roleId, chiefId, productInstallationId, true)
            ],
            [new OrganizationRole(roleId, "Product Manager", "Own product outcomes.", "[]")],
            [],
            [],
            [],
            DateTimeOffset.UtcNow);
        var profile = new BusinessProfileResponse(
            organizationId,
            "Trailwise",
            "Marketplace",
            "Outdoor recreation",
            "A marketplace for guided trips.",
            "Make outdoor expertise accessible.",
            "Validation",
            ["New outdoor enthusiasts"],
            ["Guided trip bookings"],
            "Commission",
            ["United States"],
            null,
            [],
            [],
            null,
            "UTC",
            3,
            0.9m,
            new Dictionary<string, ProfileFieldProvenance>());
        return new FixtureData(
            organizationId,
            chiefId,
            chiefInstallationId,
            productManagerId,
            productInstallationId,
            Guid.NewGuid(),
            snapshot,
            profile);
    }

    private static FinancialOperatingProfileResponse Finance(Guid organizationId) => new(
        organizationId,
        "USD",
        100_000,
        null,
        null,
        6,
        20_000,
        5_000,
        2,
        "DigitalFirst",
        1);

    private sealed record FixtureData(
        Guid OrganizationId,
        Guid ChiefId,
        Guid ChiefInstallationId,
        Guid ProductManagerId,
        Guid ProductInstallationId,
        Guid OwnerChatId,
        OrganizationSnapshotResponse Snapshot,
        BusinessProfileResponse Profile);

    private sealed class CoordinationBroker(
        OrganizationSnapshotResponse snapshot,
        BusinessProfileResponse profile,
        FinancialOperatingProfileResponse? finance) : IAgentBrokerClient
    {
        public List<RequestCapability> Requests { get; } = [];
        public CommunicationHubResponse? CommunicationHub { get; init; }
        public ProductPlanResponse? ProductPlan { get; init; }

        public Task<CapabilityResult> InvokeCapabilityAsync(
            RequestCapability request,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            object? response = request.Capability switch
            {
                PlatformCapabilities.OrganizationSnapshotRead => snapshot,
                PlatformCapabilities.BusinessProfileRead => profile,
                PlatformCapabilities.FinanceProfileRead when finance is not null => finance,
                PlatformCapabilities.HiringRecommendationList => new HiringBacklogResponse([]),
                PlatformCapabilities.HiringRecommendationUpsert => new MutationResponse(true, 1, null, "Saved"),
                ChiefOfStaffProfile.ReadCommunicationCapability when CommunicationHub is not null => CommunicationHub,
                ChiefOfStaffProfile.SendCommunicationMessageCapability => new { },
                ProductManagementCapabilities.Plan when ProductPlan is not null => ProductPlan,
                _ => null
            };
            return Task.FromResult(response is null
                ? new CapabilityResult
                {
                    RequestId = request.RequestId,
                    Succeeded = false,
                    Error = "Capability unavailable in test."
                }
                : new CapabilityResult
                {
                    RequestId = request.RequestId,
                    Succeeded = true,
                    ContentType = "application/json",
                    Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(response, response.GetType(), JsonOptions))
                });
        }

        public Task StartAsync(RegisterAgent registration, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<BrokerToAgentMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task PublishEventAsync(PublishEvent message, string? correlationId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendCapabilityResultAsync(CapabilityResult result, string? correlationId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
