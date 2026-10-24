using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff.Contracts;

namespace CSweet.Agents.ChiefOfStaff.Agent;

public sealed partial class ChiefOfStaffAgent
{
    private sealed record ProductDelivery(string Fingerprint, ProductContextUpdateRequest Request);

    private static async Task DeliverProductContextAsync(Guid recipient, Guid installation,
        ProductRoleBriefResponse brief, ChiefOperatingContext operating, AgentRuntimeContext context, CancellationToken token)
    {
        var key = $"chief.product-context:{installation:N}:{recipient:N}";
        var stored = await context.Platform.ReadOperatingStateAsync(new(key), token);
        // Finish an interrupted delivery with exactly the original request and source identity.
        if (stored?.Status == "Pending")
            stored = await AcknowledgeProductDeliveryAsync(stored, context, token);
        var fingerprint = ProductContextFingerprint(brief, operating);
        if (stored?.DecisionFingerprint == fingerprint) return;

        var operation = $"product-context:{installation:N}:{fingerprint}:{stored?.Revision ?? 0}";
        var source = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(operation)).AsSpan(0, 16));
        var delivery = new ProductDelivery(fingerprint, new(brief, source, operation));
        stored = await context.Platform.WriteOperatingStateAsync(new(key, "com.csweet.chief.product-context", 1, "Pending",
            new Dictionary<string, string>(), [], fingerprint, [], Guid.Empty,
            SerializePayload(delivery), stored?.Revision, operation + ":intent"), token);
        await AcknowledgeProductDeliveryAsync(stored, context, token);
    }

    private static async Task<AgentOperatingStateResponse> AcknowledgeProductDeliveryAsync(
        AgentOperatingStateResponse stored, AgentRuntimeContext context, CancellationToken token)
    {
        var delivery = DeserializePayload<ProductDelivery>(stored.Payload)
            ?? throw new InvalidOperationException("The pending Product Manager context delivery is invalid.");
        var acknowledgment = await context.Platform.InvokeAsync<ProductContextUpdateRequest, ProductContextUpdateResponse>(
            ProductManagementCapabilities.ContextUpdate, delivery.Request, token);
        if (!acknowledgment.Acknowledged)
            throw new InvalidOperationException("The Product Manager has not acknowledged the context delivery.");
        // Context-update owns PM review/follow-through. A separate Plan call repeats that workflow.
        return await context.Platform.WriteOperatingStateAsync(new(stored.StateKey, stored.SchemaId, stored.SchemaVersion,
            "Acknowledged", stored.SourceRevisions, [], delivery.Fingerprint, [], Guid.Empty,
            stored.Payload, stored.Revision, delivery.Request.IdempotencyKey + ":ack"), token);
    }

    internal static string ProductContextFingerprint(ProductRoleBriefResponse brief, ChiefOperatingContext operating)
    {
        var data = JsonSerializer.SerializeToNode(new { brief, operating.BusinessProfile, operating.FinancialProfile, operating.Organization });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(data)))).ToLowerInvariant();

        static string Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => "{" + string.Join(",", obj
                .Where(x => x.Key is not ("PreparedAt" or "GeneratedAt" or "CreatedAt" or "UpdatedAt" or "OccurredAt" or "AsOf" or "SnapshotAt"))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(Canonical).Order(StringComparer.Ordinal)) + "]",
            _ => node?.ToJsonString() ?? "null"
        };
    }
}
