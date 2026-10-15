namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// The product-name registry (HLD-003 LADR-10). Graph-only: a <c>Product</c> vertex links many-to-many
/// to identity-only <c>Initiative</c> vertices and optionally one-to-many to identity-only <c>Repo</c>
/// vertices. Unrelated to the <c>product</c> scope dimension; no relational table and no group column.
/// </summary>
public interface IProductGraph
{
    /// <summary>True when a product with this exact name exists.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates a product, atomically under the advisory lock. Refuses (throws
    /// <see cref="SmoothAiProductContextMemory.Application.Common.Exceptions.ConflictException"/>) a
    /// name whose case-folded form equals another product's. Null <paramref name="description"/> or
    /// <paramref name="status"/> leaves an existing field unchanged. Returns <see langword="true"/>
    /// when the product was created, <see langword="false"/> when it was updated.
    /// </summary>
    Task<bool> UpsertAsync(
        string name,
        string? description,
        string? status,
        CancellationToken cancellationToken);

    /// <summary>Every product with its initiative and repo counts (one hop each).</summary>
    Task<IReadOnlyList<ProductRegistryRow>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The product with this exact name, or null. Counts are one hop each.</summary>
    Task<ProductRegistryRow?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Declares <c>(Product)-[:HAS_INITIATIVE]->(Initiative)</c>. The Initiative vertex is MERGEd on
    /// demand; the callers validate it exists relationally first. Returns <see langword="false"/> on an
    /// already-present pair.
    /// </summary>
    Task<bool> LinkInitiativeAsync(
        string productName,
        string initiativeName,
        string reason,
        string source,
        CancellationToken cancellationToken);

    /// <summary>Removes exactly one <c>HAS_INITIATIVE</c> edge. Returns <see langword="false"/> when absent.</summary>
    Task<bool> UnlinkInitiativeAsync(
        string productName,
        string initiativeName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Declares <c>(Product)-[:HAS_REPO]->(Repo)</c>. A repo may have at most one incoming
    /// <c>HAS_REPO</c>; an already-linked-to-another-product repo is refused.
    /// </summary>
    Task<RepoLinkResult> LinkRepoAsync(
        string productName,
        string repoName,
        string reason,
        string source,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetInitiativeNamesAsync(string productName, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRepoNamesAsync(string productName, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetProductsForInitiativeAsync(string initiativeName, CancellationToken cancellationToken);

    /// <summary>The product that owns this repo via <c>HAS_REPO</c>, or null. At most one by invariant.</summary>
    Task<string?> GetProductForRepoAsync(string repoName, CancellationToken cancellationToken);

    /// <summary>
    /// For each named initiative, the number of products linked to it (incoming <c>HAS_INITIATIVE</c>),
    /// used to derive <c>sharedInitiative</c>. Initiatives with no product are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountProductsPerInitiativeAsync(
        IReadOnlyCollection<string> initiativeNames,
        CancellationToken cancellationToken);
}

public sealed record ProductRegistryRow(string Name, string? Description, string Status, int InitiativeCount, int RepoCount);

public sealed record RepoLinkResult(bool Created, string? ExistingProductName);
