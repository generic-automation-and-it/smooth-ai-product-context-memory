using System.Text.RegularExpressions;

namespace SmoothAiProductContextMemory.AppHost;

internal readonly partial record struct ContainerImageReference(string Image, string? Tag)
{
    internal static ContainerImageReference Parse(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (reference.Contains('@', StringComparison.Ordinal))
        {
            return new ContainerImageReference(reference, null);
        }

        int slash = reference.LastIndexOf('/');
        int colon = reference.LastIndexOf(':');
        return colon > slash
            ? new ContainerImageReference(reference[..colon], reference[(colon + 1)..])
            : new ContainerImageReference(reference, null);
    }

    internal static bool IsDigestPinned(string reference) => DigestPattern().IsMatch(reference);

    [GeneratedRegex("^[^\\s@]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
