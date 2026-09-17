namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Content-addressed object storage. The address is derived from the content's hash, so writing the
/// same bytes twice yields the same address and a single stored object (idempotent, safe to retry).
/// The abstraction never leaks storage-engine concepts (buckets, keys, endpoints).
/// <para>
/// Deliberately no delete: addresses are shared by identical bytes, so no application code can prove
/// an object is unreferenced across all versions. Object deletion is excluded until a separately
/// approved garbage-collection design exists, informed by HLD-006 orphan accounting (which reports
/// counts but never deletes). Test cleanup uses the concrete adapter directly.
/// </para>
/// </summary>
public interface IBlobStorage
{
    /// <summary>Stores content and returns its content address.</summary>
    Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default);

    /// <summary>Retrieves content by address, or <see langword="null"/> when no object exists.</summary>
    Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Returns whether an object exists at the given address.</summary>
    Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default);
}

public sealed record BlobContent(Stream Content, string? ContentType) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
