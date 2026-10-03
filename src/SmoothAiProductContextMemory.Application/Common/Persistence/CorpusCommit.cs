using System.Security.Cryptography;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Application.Common.Persistence;

public static class CorpusCommit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Hash<T>(T payload) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Json)));

    public static void CheckEpoch(CorpusState state, Guid? expectedEpoch)
    {
        if (expectedEpoch is { } epoch && epoch != state.Epoch)
        {
            throw new ConflictException("Corpus restore epoch changed; reconcile the service journal before retrying.");
        }
    }

    public static void CheckRevision(CorpusState state, long? expectedRevision)
    {
        if (expectedRevision is { } revision && revision != state.Revision)
        {
            throw new ConflictException("Corpus changed since comparison; read and compare again.");
        }
    }

    public static T Replay<T>(OperationReceipt receipt, string hash, string operationType)
    {
        if (receipt.PayloadHash != hash || receipt.OperationType != operationType)
        {
            throw new ConflictException("Operation key already committed with a different payload.");
        }

        return JsonSerializer.Deserialize<T>(receipt.ResultJson, Json)
            ?? throw new InvalidOperationException("Committed operation result is invalid.");
    }

    public static OperationReceipt Receipt<T>(string key, string hash, string operationType, T result) =>
        new(key, hash, operationType, JsonSerializer.Serialize(result, Json), DateTimeOffset.UtcNow);
}
