using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.Contracts.Grpc;
using CSweet.Agent.SDK;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ChiefOfStaffOnboardingTests
{
    [Fact]
    public async Task OnboardedEvent_GeneratesBusinessGroundedMessageThenAcknowledges()
    {
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var hiringUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var profile = new BusinessProfileResponse(
            organizationId,
            "Trailwise",
            "Marketplace",
            "Outdoor recreation",
            "A marketplace for guided outdoor trips.",
            "Make expert-led outdoor experiences accessible to new adventurers.",
            "Validation",
            ["New outdoor enthusiasts"],
            ["Guided trip bookings"],
            "Booking commission",
            ["United States"],
            null,
            [],
            [],
            "Moderate",
            "America/Los_Angeles",
            1,
            0.8m,
            new Dictionary<string, ProfileFieldProvenance>());
        var broker = new RecordingBrokerClient(profile);
        var chatClient = new RecordingChatClient(
            "Trailwise should first hire a Marketplace Product Manager to own validation and coordinate the later engineering, supply, and QA roles.");
        var providerId = Guid.NewGuid();
        var agent = new ChiefOfStaffAgent(
            new RecordingLlmClientFactory(chatClient),
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
        var runtimeContext = new AgentRuntimeContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), broker);
        var configuration = await agent.ExecuteCapabilityAsync(
            new CapabilityRequest
            {
                Capability = AgentConfigurationCapabilities.Update,
                Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                    new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement>
                    {
                        ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId.ToString("D")),
                        ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
                    }),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            },
            runtimeContext,
            CancellationToken.None);
        Assert.True(configuration.Succeeded, configuration.Error);
        var message = new DeliveredEvent
        {
            EventId = eventId.ToString("N"),
            EventType = ChiefOfStaffProfile.OnboardedEvent,
            Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new AgentOnboardedEvent(
                organizationId, agentId, hiringUserId, conversationId, DateTimeOffset.UtcNow),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        await agent.HandleEventAsync(message, runtimeContext, CancellationToken.None);

        var send = Assert.Single(
            broker.Requests,
            request => request.Capability == ChiefOfStaffProfile.SendCommunicationMessageCapability);
        var sendPayload = JsonDocument.Parse(send.Payload.ToByteArray()).RootElement;
        Assert.Equal(conversationId, sendPayload.GetProperty("chatId").GetGuid());
        Assert.Contains("Trailwise", sendPayload.GetProperty("content").GetString());
        Assert.Contains("Marketplace Product Manager", sendPayload.GetProperty("content").GetString());
        Assert.DoesNotContain("Thank you for hiring me", sendPayload.GetProperty("content").GetString());
        Assert.Equal($"agent-onboarded:{eventId:N}", sendPayload.GetProperty("idempotencyKey").GetString());
        var groundedPrompt = string.Join("\n", chatClient.Messages.Select(message => message.Text));
        Assert.Contains("Trailwise", groundedPrompt);
        Assert.Contains("Outdoor recreation", groundedPrompt);
        Assert.Contains("Make expert-led outdoor experiences accessible", groundedPrompt);

        var acknowledgement = Assert.Single(
            broker.Requests,
            request => request.Capability == ChiefOfStaffProfile.CompleteOnboardingCapability);
        Assert.Equal(eventId, JsonDocument.Parse(acknowledgement.Payload.ToByteArray()).RootElement
            .GetProperty("eventId").GetGuid());
    }

    [Fact]
    public void FormatOnboardingMessage_TurnsDenseLabeledOutputIntoReadableMarkdown()
    {
        const string dense = """
We are an AI-first business platform in the Idea stage.
Role Map: Product Manager, Engineering Lead, Marketing Lead.
Priority 1 Hire: Product Manager. Validate the target customer first.
Who is the first specific customer you intend to serve?
""";

        var formatted = ChiefOfStaffAgent.FormatOnboardingMessage(dense);

        Assert.Contains("- **Role map:** Product Manager", formatted);
        Assert.Contains("- **Priority 1 hire:** Product Manager", formatted);
        Assert.Contains("**Question for you**\n\nWho is the first", formatted);
        Assert.DoesNotContain("Role Map:", formatted);
    }

    private sealed class RecordingBrokerClient(BusinessProfileResponse profile) : IAgentBrokerClient
    {
        public List<RequestCapability> Requests { get; } = [];

        public Task<CapabilityResult> InvokeCapabilityAsync(
            RequestCapability request,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Capability == PlatformCapabilities.BusinessProfileRead)
            {
                return Task.FromResult(new CapabilityResult
                {
                    RequestId = request.RequestId,
                    Succeeded = true,
                    ContentType = "application/json",
                    Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                        profile,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                });
            }

            if (request.Capability != ChiefOfStaffProfile.SendCommunicationMessageCapability &&
                request.Capability != ChiefOfStaffProfile.CompleteOnboardingCapability)
            {
                return Task.FromResult(new CapabilityResult
                {
                    RequestId = request.RequestId,
                    Succeeded = false,
                    Error = "Capability unavailable in test."
                });
            }

            return Task.FromResult(new CapabilityResult
            {
                RequestId = request.RequestId,
                Succeeded = true,
                ContentType = "application/json",
                Payload = ByteString.CopyFromUtf8("{}")
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

    private sealed class RecordingLlmClientFactory(RecordingChatClient client) : IAgentLlmClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(
            AgentLlmSelection selection,
            CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(client);
    }

    private sealed class RecordingChatClient(string response) : IChatClient
    {
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
