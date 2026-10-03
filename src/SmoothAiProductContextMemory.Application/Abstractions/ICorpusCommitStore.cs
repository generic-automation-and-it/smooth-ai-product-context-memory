namespace SmoothAiProductContextMemory.Application.Abstractions;

public sealed record CorpusState(Guid Epoch, long Revision);

public sealed record OperationReceipt(
    string OperationKey,
    string PayloadHash,
    string OperationType,
    string ResultJson,
    DateTimeOffset CommittedAt);

public interface ICorpusCommitStore
{
    Task<CorpusState> GetStateAsync(CancellationToken cancellationToken);
    Task<CorpusState> LockAsync(CancellationToken cancellationToken);
    Task<OperationReceipt?> FindAsync(string operationKey, CancellationToken cancellationToken);
    Task RecordAsync(OperationReceipt receipt, CancellationToken cancellationToken);
}
