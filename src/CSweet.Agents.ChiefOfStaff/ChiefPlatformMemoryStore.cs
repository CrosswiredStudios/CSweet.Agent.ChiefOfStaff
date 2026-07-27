using CSweet.Agent.SDK;
using CSweet.Memory;

namespace CSweet.Agents.ChiefOfStaff;

/// <summary>
/// Keeps the agent compatible with the published memory abstractions while all
/// platform transport remains behind the SDK 1.0 platform client.
/// </summary>
internal sealed class ChiefPlatformMemoryStore(PlatformCapabilityClient platform)
    : IMemoryStore, IKnowledgeTransferStore
{
    public MemoryStoreCapabilities Capabilities =>
        MemoryStoreCapabilities.Transactions |
        MemoryStoreCapabilities.FullText |
        MemoryStoreCapabilities.NativeVectors |
        MemoryStoreCapabilities.RecursiveTraversal |
        MemoryStoreCapabilities.TemporalQueries |
        MemoryStoreCapabilities.BulkOperations |
        MemoryStoreCapabilities.ChangeHistory;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("append-episode", episode, cancellationToken);

    public Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("upsert-entity", entity, cancellationToken);

    public Task<MemoryEntity?> FindEntityByApplicationKeyAsync(
        MemoryPartition partition,
        string applicationKey,
        CancellationToken cancellationToken = default) =>
        QueryAsync<MemoryEntity?>("find-entity-by-application-key", new { partition, applicationKey }, cancellationToken);

    public Task<MemoryEntity?> FindEntityAsync(
        MemoryPartition partition,
        string canonicalName,
        CancellationToken cancellationToken = default) =>
        QueryAsync<MemoryEntity?>("find-entity", new { partition, canonicalName }, cancellationToken);

    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("write-claim", claim, cancellationToken);

    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("write-edge", edge, cancellationToken);

    public Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("write-block", block, cancellationToken);

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("write-procedure", procedure, cancellationToken);

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        WriteAsync<MemoryWriteResult>("write-embedding", embedding, cancellationToken);

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default) =>
        await WriteAsync<MemoryWriteResult>("record-use", use, cancellationToken);

    public Task<IReadOnlyList<MemoryCandidate>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default) =>
        QueryAsync<IReadOnlyList<MemoryCandidate>>("search", request, cancellationToken);

    public async Task SupersedeClaimAsync(
        Guid claimId,
        Guid supersededByClaimId,
        DateTimeOffset validTo,
        CancellationToken cancellationToken = default) =>
        await ManageAsync<MemoryWriteResult>(
            "supersede-claim",
            new { claimId, supersededByClaimId, validTo },
            cancellationToken);

    public Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default) =>
        QueryAsync<MemoryClaim?>("get-claim", new { claimId }, cancellationToken);

    public async Task SetClaimConfirmationAsync(
        Guid claimId,
        MemoryConfirmationState confirmation,
        CancellationToken cancellationToken = default) =>
        await ManageAsync<MemoryWriteResult>("set-confirmation", new { claimId, confirmation }, cancellationToken);

    public Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(
        MemoryPartition partition,
        CancellationToken cancellationToken = default) =>
        QueryAsync<IReadOnlyList<MemoryClaim>>("list-claims", partition, cancellationToken);

    public async Task WriteKnowledgeTransferAsync(
        KnowledgeTransferPackage package,
        CancellationToken cancellationToken = default) =>
        await ManageAsync<MemoryWriteResult>("write-knowledge-transfer", package, cancellationToken);

    public Task<KnowledgeTransferPackage?> GetKnowledgeTransferAsync(
        Guid packageId,
        CancellationToken cancellationToken = default) =>
        QueryAsync<KnowledgeTransferPackage?>("get-knowledge-transfer", new { packageId }, cancellationToken);

    public Task<MemoryExport> ExportAsync(
        MemoryPartition partition,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<MemoryExport>("platform.memory.export.v1", "export", partition, cancellationToken);

    public async Task DeleteScopeAsync(
        MemoryPartition partition,
        CancellationToken cancellationToken = default) =>
        await ManageAsync<MemoryWriteResult>("delete-scope", partition, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<T> WriteAsync<T>(string operation, object payload, CancellationToken cancellationToken) =>
        InvokeAsync<T>("platform.memory.write.v1", operation, payload, cancellationToken);

    private Task<T> QueryAsync<T>(string operation, object payload, CancellationToken cancellationToken) =>
        InvokeAsync<T>("platform.memory.query.v1", operation, payload, cancellationToken);

    private Task<T> ManageAsync<T>(string operation, object payload, CancellationToken cancellationToken) =>
        InvokeAsync<T>("platform.memory.manage.v1", operation, payload, cancellationToken);

    private Task<T> InvokeAsync<T>(
        string capability,
        string operation,
        object payload,
        CancellationToken cancellationToken)
    {
        var access = capability switch
        {
            "platform.memory.query.v1" => "query",
            "platform.memory.write.v1" => "write",
            "platform.memory.manage.v1" => "manage",
            "platform.memory.export.v1" => "export",
            _ => throw new InvalidOperationException($"Unsupported memory capability '{capability}'.")
        };
        return platform.Memory.ExecuteAsync<T>(access, operation, payload, cancellationToken);
    }
}
