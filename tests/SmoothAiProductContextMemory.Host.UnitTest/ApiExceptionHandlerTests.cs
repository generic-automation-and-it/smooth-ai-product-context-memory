using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Host.Configuration;

namespace SmoothAiProductContextMemory.Host.UnitTest;

public class ApiExceptionHandlerTests
{
    [Fact]
    public async Task Validation_maps_to_400()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler();
        var exception = new ValidationException([new ValidationFailure("Name", "required")]);

        bool handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task NotFound_maps_to_404()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler();

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
        var handler = new ApiExceptionHandler();

        bool handled = await handler.TryHandleAsync(
            context,
            new ConflictException("dup"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Append_only_message_maps_to_409()
    {
        DefaultHttpContext context = CreateContext();
        var handler = new ApiExceptionHandler();

        bool handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("Append-only history: UPDATE on memory_version is not permitted"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        doc.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain("UPDATE on");
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }
}
