using System.Text.Json;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

/// <summary>Persists and reads the last snapshot summary as a JSON file in a gitignored, unsynchronised
/// directory, so preflight can report recency and last-walk numbers without re-running a walk.</summary>
public sealed class FileSnapshotMetadataStore(
    IOptions<SnapshotMetadataOptions> options) : ISnapshotMetadataStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string FilePath => Path.Combine(options.Value.Directory, "snapshot-metadata.json");

    public async Task<SnapshotMetadata?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<SnapshotMetadata>(stream, Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task WriteAsync(SnapshotMetadata metadata, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.Value.Directory);
        await using FileStream stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, metadata, Json, cancellationToken);
    }
}

/// <summary>Where the last-snapshot summary is persisted. Default is a gitignored, unsynchronised path.</summary>
public sealed class SnapshotMetadataOptions
{
    public const string SectionName = "Snapshot";

    public string Directory { get; set; } = ".context";
}
