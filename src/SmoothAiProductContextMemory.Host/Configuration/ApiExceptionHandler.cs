using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Host.Configuration;

internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ValidationException validation)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            var errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            await httpContext.Response.WriteAsJsonAsync(
                new HttpValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation failed",
                },
                cancellationToken);
            return true;
        }

        (int status, string title, string detail) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", exception.Message),
            DbUpdateException db => MapDatabase(db),
            _ => MapUnhandled(exception),
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
            },
            cancellationToken);
        return true;
    }

    private static (int Status, string Title, string Detail) MapDatabase(DbUpdateException exception)
    {
        Exception mapped = DbExceptionMapping.Map(exception);
        if (mapped is ConflictException conflict)
        {
            return (StatusCodes.Status409Conflict, "Conflict", conflict.Message);
        }

        return (StatusCodes.Status409Conflict, "Conflict", "The request conflicted with stored data.");
    }

    private static (int Status, string Title, string Detail) MapUnhandled(Exception exception)
    {
        string text = exception.Message;
        if (text.Contains("Append-only history", StringComparison.Ordinal)
            || text.Contains("23505", StringComparison.Ordinal))
        {
            return (StatusCodes.Status409Conflict, "Conflict", "The request conflicted with stored data.");
        }

        return (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred.");
    }
}
