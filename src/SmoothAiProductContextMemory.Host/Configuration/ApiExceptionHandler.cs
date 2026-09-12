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
}
