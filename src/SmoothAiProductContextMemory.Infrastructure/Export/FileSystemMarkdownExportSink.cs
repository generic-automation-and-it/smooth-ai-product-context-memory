using System.Text;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Export;

namespace SmoothAiProductContextMemory.Infrastructure.Export;

public sealed class FileSystemMarkdownExportSink(ILogger<FileSystemMarkdownExportSink> logger) : IMarkdownExportSink
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private string? _root;

    public async Task PrepareAsync(string outputDirectory, bool force, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(outputDirectory);
        if (IsFilesystemRoot(root))
        {
            throw new InvalidOperationException("Refusing to export to a filesystem root.");
        }

        if (IsGitRoot(root))
        {
            throw new InvalidOperationException("Refusing to export to a git repository root.");
        }

        Directory.CreateDirectory(root);

        string markerPath = Path.Combine(root, ExportPaths.MarkerFileName);
        bool hasMarker = File.Exists(markerPath);
        bool empty = !Directory.EnumerateFileSystemEntries(root).Any();

        if (!empty && !hasMarker && !force)
        {
            throw new InvalidOperationException(
                $"Refusing to wipe '{root}' because it is not an export directory. Pass --force to override.");
        }

        if (!empty)
        {
            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        await File.WriteAllTextAsync(markerPath, "generated-never-maintained\n", Utf8NoBom, cancellationToken);
        _root = root;
        logger.LogInformation("Export directory prepared");
    }

    public async Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("Export directory has not been prepared.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string combined = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(combined, _root))
        {
            throw new InvalidOperationException("Export path escaped the output directory.");
        }

        string? directory = Path.GetDirectoryName(combined);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(combined, content, Utf8NoBom, cancellationToken);
        logger.LogDebug("Wrote export file {RelativePath}", relativePath);
    }

    private static bool IsFilesystemRoot(string path)
    {
        string full = Path.GetFullPath(path);
        string? volumeRoot = Path.GetPathRoot(full);
        if (volumeRoot is null)
        {
            return false;
        }

        return string.Equals(
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitRoot(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static bool IsUnderRoot(string candidate, string root)
    {
        // Case-insensitive only where the filesystem is (Windows/macOS); Linux is case-sensitive.
        StringComparison comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison)
               || string.Equals(candidate, root, comparison);
    }
}
