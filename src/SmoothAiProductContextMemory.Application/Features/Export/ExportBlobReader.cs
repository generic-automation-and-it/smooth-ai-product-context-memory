using System.Text;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.Export;

internal static class ExportBlobReader
{
    public static async Task<(BlobRenderState State, string? Text)> ReadAsync(
        BlobContent blob,
        CancellationToken cancellationToken)
    {
        if (!IsTextContentType(blob.ContentType))
        {
            return (BlobRenderState.NonText, null);
        }

        try
        {
            using var reader = new StreamReader(
                blob.Content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            string text = await reader.ReadToEndAsync(cancellationToken);
            return (BlobRenderState.Inlined, text);
        }
        catch (DecoderFallbackException)
        {
            return (BlobRenderState.NonText, null);
        }
    }

    internal static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        ReadOnlySpan<char> type = contentType.AsSpan();
        int separator = type.IndexOf(';');
        if (separator >= 0)
        {
            type = type[..separator];
        }

        type = type.Trim();
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
               || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
               || type.Equals("application/yaml", StringComparison.OrdinalIgnoreCase)
               || type.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase);
    }
}
