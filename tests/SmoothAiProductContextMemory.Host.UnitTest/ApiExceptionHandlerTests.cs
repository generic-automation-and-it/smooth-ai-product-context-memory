using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Host.Configuration;

namespace SmoothAiProductContextMemory.Host.UnitTest;

public class ApiExceptionHandlerTests
{
    [Fact]
    public async Task Validation_maps_to_400()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new PassthroughMapper());
        var exception = new ValidationException([new ValidationFailure("Name", "required")]);

        bool handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.ContentType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task NotFound_maps_to_404()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new PassthroughMapper());

        bool handled = await handler.TryHandleAsync(
            context,
            new NotFoundException("missing"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Conflict_maps_to_409()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new PassthroughMapper());

        bool handled = await handler.TryHandleAsync(
            context,
            new ConflictException("dup"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Scope_violation_maps_to_403()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new PassthroughMapper());

        bool handled = await handler.TryHandleAsync(
            context,
            new ForbiddenException("programme scoped"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    /// <summary>A database failure is classified by the mapper, and its own text never reaches the body.</summary>
    [Fact]
    public async Task Mapped_database_failure_maps_to_409_without_provider_text()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new StubMapper(new ConflictException("History is append-only.")));

        bool handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("Append-only history: UPDATE on memory_version is not permitted"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        (await ReadDetail(context)).ShouldBe("History is append-only.");
    }

    [Fact]
    public async Task Unmapped_failure_maps_to_500_without_leaking_the_message()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler(new PassthroughMapper());

        bool handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("relation \"memory\" does not exist"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        (await ReadDetail(context)).ShouldBe("An unexpected error occurred.");
    }

    private static async Task<string?> ReadDetail(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using JsonDocument doc = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        return doc.RootElement.GetProperty("detail").GetString();
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class PassthroughMapper : IDbErrorMapper
    {
        public bool TryMap(Exception exception, out Exception mapped)
        {
            mapped = exception;
            return false;
        }
    }

    private sealed class StubMapper(Exception result) : IDbErrorMapper
    {
        public bool TryMap(Exception exception, out Exception mapped)
        {
            mapped = result;
            return true;
        }
    }
}
