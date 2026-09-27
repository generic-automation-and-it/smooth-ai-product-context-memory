namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Read-only enumeration of every object held by the blob store. Deliberately separate from
/// <see cref="IBlobStorage"/> (whose surface the model-shape guard pins to Store/Get/Exists) because
/// corpus membership is derived from the captured database state, never from listing the store
/// (LADR-03). This catalog exists solely so snapshot orphan accounting can report unreferenced
/// objects; it holds no delete and no membership authority.
/// </summary>
public interface IBlobCatalog
{
    /// <summary>Returns the content address of every stored object.</summary>
    Task<string[]> ListAsync(CancellationToken cancellationToken = default);
}
