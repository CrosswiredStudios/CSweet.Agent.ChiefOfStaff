using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.Contracts.Grpc;
using CSweet.Agent.SDK;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.ChiefOfStaff.Tests;

public sealed class ChiefOfStaffOnboardingTests
{
    [Fact]
    public async Task OnboardedEvent_SendsAgentOwnedMessageThenAcknowledges()
    {
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var hiringUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var broker = new RecordingBrokerClient();
        var agent = new ChiefOfStaffAgent(
            NullLogger<ChiefOfStaffAgent>.Instance,
            new ChiefOfStaffOrchestrator(NullLogger<ChiefOfStaffOrchestrator>.Instance));
        var message = new DeliveredEvent
        {
            EventId = eventId.ToString("N"),
            EventType = ChiefOfStaffProfile.OnboardedEvent,
            Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new AgentOnboardedEvent(
                organizationId, agentId, hiringUserId, conversationId, DateTimeOffset.UtcNow),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        await agent.HandleEventAsync(message, new AgentRuntimeContext(
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), broker), CancellationToken.None);

        Assert.Equal(2, broker.Requests.Count);
        var send = broker.Requests[0];
        Assert.Equal(ChiefOfStaffProfile.SendCommunicationMessageCapability, send.Capability);
        var sendPayload = JsonDocument.Parse(send.Payload.ToByteArray()).RootElement;
        Assert.Equal(conversationId, sendPayload.GetProperty("chatId").GetGuid());
        Assert.Contains("determine which role we should hire next", sendPayload.GetProperty("content").GetString());
        Assert.Equal($"agent-onboarded:{eventId:N}", sendPayload.GetProperty("idempotencyKey").GetString());

        var acknowledgement = broker.Requests[1];
        Assert.Equal(ChiefOfStaffProfile.CompleteOnboardingCapability, acknowledgement.Capability);
        Assert.Equal(eventId, JsonDocument.Parse(acknowledgement.Payload.ToByteArray()).RootElement
            .GetProperty("eventId").GetGuid());
    }

    private sealed class RecordingBrokerClient : IAgentBrokerClient
    {
        public List<RequestCapability> Requests { get; } = [];

        public Task<CapabilityResult> InvokeCapabilityAsync(
            RequestCapability request,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
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
}
