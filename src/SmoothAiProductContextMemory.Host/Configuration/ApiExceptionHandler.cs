using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Host.Configuration;

/// <summary>
/// The single error contract: RFC 7807 for every failure.
/// </summary>
/// <remarks>
/// Database failures are classified by <see cref="IDbErrorMapper"/>, not by inspecting message text
/// here — the Host has no business knowing SQLSTATEs, and substring matching mis-classifies. Details
/// are fixed strings for anything database-originated, so no SQL, column name or value can leak.
/// </remarks>
internal sealed class ApiExceptionHandler(IDbErrorMapper errorMapper) : IExceptionHandler
{
    private const string ProblemContentType = "application/problem+json";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ValidationException validation)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            Dictionary<string, string[]> errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            await httpContext.Response.WriteAsJsonAsync(
                new HttpValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation failed",
                },
                options: null,
                contentType: ProblemContentType,
                cancellationToken);
            return true;
        }

        Exception effective = errorMapper.TryMap(exception, out Exception mapped) ? mapped : exception;

        // A request body that fails to deserialize is a caller mistake, not a server failure. An
        // unknown property — e.g. a misspelled `initiative` where the contract says
        // `initiativeName` — is rejected here as a 400, never absorbed and silently defaulted.
        // BadHttpRequestException carries its own status (413 payload too large, 411 length
        // required, …) — honor it rather than flattening every request defect to 400.
        if (effective is JsonException || effective is BadHttpRequestException)
        {
            int badRequestStatus = effective is BadHttpRequestException bad
                ? bad.StatusCode
                : StatusCodes.Status400BadRequest;
            JsonException? json = FindJsonFailure(effective);
            var problem = new ProblemDetails
            {
                Status = badRequestStatus,
                Title = "Invalid request body",
                Detail = "The request body could not be read: " + (json?.Message ?? effective.Message),
            };

            if (json is { Path.Length: > 0 } located)
            {
                problem.Extensions["path"] = located.Path;
            }

            httpContext.Response.StatusCode = badRequestStatus;
            await httpContext.Response.WriteAsJsonAsync(
                problem,
                options: null,
                contentType: ProblemContentType,
                cancellationToken);
            return true;
        }

        (int status, string title, string detail) = effective switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", effective.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", effective.Message),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", effective.Message),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred."),
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
            },
            options: null,
            contentType: ProblemContentType,
            cancellationToken);
        return true;
    }

    // BadHttpRequestException's own message names no property ("Failed to read parameter ... as
    // JSON"), so a caller cannot tell which field was rejected. The inner JsonException names the
    // offending member, and carries the JSON path structurally — reported as a problem extension
    // rather than spliced into the detail, so nothing here reads exception text.
    private static JsonException? FindJsonFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is JsonException json)
            {
                return json;
            }
        }

        return null;
    }
}
