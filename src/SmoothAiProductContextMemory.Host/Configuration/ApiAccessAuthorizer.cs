using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal enum ApiCapability
{
    Read,
    Write,
}

internal sealed record RequiredApiCapability(ApiCapability Value);

internal sealed class ApiAccessOptions
{
    public const string SectionName = "ApiAccess";

    public string ReadToken { get; set; } = string.Empty;

    public string WriteToken { get; set; } = string.Empty;
}

internal sealed class ApiAccessAuthorizer
{
    private readonly byte[] _readTokenHash;
    private readonly byte[] _writeTokenHash;

    public ApiAccessAuthorizer(IOptions<ApiAccessOptions> options)
    {
        ApiAccessOptions value = options.Value;
        if (string.IsNullOrWhiteSpace(value.ReadToken) || string.IsNullOrWhiteSpace(value.WriteToken))
        {
            throw new InvalidOperationException("ApiAccess read and write tokens are required.");
        }

        if (string.Equals(value.ReadToken, value.WriteToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ApiAccess read and write tokens must be distinct.");
        }

        _readTokenHash = Hash(value.ReadToken);
        _writeTokenHash = Hash(value.WriteToken);
    }

    public bool Authorize(string? authorization, ApiCapability required)
    {
        const string Prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] supplied = Hash(authorization[Prefix.Length..]);
        bool write = CryptographicOperations.FixedTimeEquals(supplied, _writeTokenHash);
        bool read = required == ApiCapability.Read
            && CryptographicOperations.FixedTimeEquals(supplied, _readTokenHash);
        return write || read;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

internal static class ApiAccessEndpointExtensions
{
    public static RouteHandlerBuilder RequireCapability(
        this RouteHandlerBuilder builder,
        ApiCapability capability) =>
        builder.WithMetadata(new RequiredApiCapability(capability));

    public static IApplicationBuilder UseApiAccessAuthorization(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            RequiredApiCapability? required = context.GetEndpoint()?.Metadata
                .GetMetadata<RequiredApiCapability>();
            if (required is null)
            {
                if (context.Request.Path.StartsWithSegments("/api/context"))
                {
                    await WriteForbiddenAsync(context);
                    return;
                }

                await next(context);
                return;
            }

            var authorizer = context.RequestServices.GetRequiredService<ApiAccessAuthorizer>();
            if (authorizer.Authorize(context.Request.Headers.Authorization.FirstOrDefault(), required.Value))
            {
                await next(context);
                return;
            }

            await WriteForbiddenAsync(context);
        });

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "The supplied API credential does not grant this capability.")
                .ExecuteAsync(context);
    }
}
