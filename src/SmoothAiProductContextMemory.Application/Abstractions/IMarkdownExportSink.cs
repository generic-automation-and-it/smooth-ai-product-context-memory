namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Writes generated Markdown export files. The sink owns disk encoding and wipe/marker policy;
/// tree shape and file contents stay in the Application export slice.
/// </summary>
public interface IMarkdownExportSink
{
    /// <summary>
    /// Prepares <paramref name="outputDirectory"/> for a wipe-and-rewrite. Creates the directory,
    /// writes the export marker, and deletes previous contents when the directory is empty, already
    /// marked, or <paramref name="force"/> is set. Refuses a filesystem root, a git repository root,
    /// and an unmarked non-empty directory unless forced.
    /// </summary>
    Task PrepareAsync(string outputDirectory, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes UTF-8 (no BOM) text at <paramref name="relativePath"/> under the directory prepared by
    /// <see cref="PrepareAsync"/>. Relative paths use <c>/</c> and must not escape the output root.
    /// </summary>
    Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default);
}
