using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Host.Configuration;

namespace SmoothAiProductContextMemory.Host.UnitTest;

public sealed class ApiAccessAuthorizerTests
{
    private static ApiAccessAuthorizer Create(string read = "read-token", string write = "write-token") =>
        new(Options.Create(new ApiAccessOptions { ReadToken = read, WriteToken = write }));

    [Fact]
    public void Constructor_throws_when_read_token_is_blank()
    {
        Should.Throw<InvalidOperationException>(() => Create(read: " ", write: "write-token"))
            .Message.ShouldContain("read and write tokens are required");
    }

    [Fact]
    public void Constructor_throws_when_write_token_is_blank()
    {
        Should.Throw<InvalidOperationException>(() => Create(read: "read-token", write: ""))
            .Message.ShouldContain("read and write tokens are required");
    }

    [Fact]
    public void Constructor_throws_when_tokens_are_identical()
    {
        Should.Throw<InvalidOperationException>(() => Create(read: "same", write: "same"))
            .Message.ShouldContain("must be distinct");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("bearer token")]
    [InlineData("Bearer")]        // no token after the scheme
    public void Malformed_authorization_is_denied(string? authorization)
    {
        ApiAccessAuthorizer authorizer = Create();
        authorizer.Authorize(authorization, ApiCapability.Read).ShouldBeFalse();
        authorizer.Authorize(authorization, ApiCapability.Write).ShouldBeFalse();
    }

    [Fact]
    public void Wrong_token_is_denied_for_both_capabilities()
    {
        ApiAccessAuthorizer authorizer = Create();
        authorizer.Authorize("Bearer not-a-token", ApiCapability.Read).ShouldBeFalse();
        authorizer.Authorize("Bearer not-a-token", ApiCapability.Write).ShouldBeFalse();
    }

    [Fact]
    public void Read_token_grants_read_but_not_write()
    {
        ApiAccessAuthorizer authorizer = Create(read: "read-token", write: "write-token");
        authorizer.Authorize("Bearer read-token", ApiCapability.Read).ShouldBeTrue();
        authorizer.Authorize("Bearer read-token", ApiCapability.Write).ShouldBeFalse();
    }

    [Fact]
    public void Write_token_grants_both_read_and_write()
    {
        ApiAccessAuthorizer authorizer = Create(read: "read-token", write: "write-token");
        authorizer.Authorize("Bearer write-token", ApiCapability.Read).ShouldBeTrue();
        authorizer.Authorize("Bearer write-token", ApiCapability.Write).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer not-a-token")]
    public void ResolveCapability_returns_null_for_absent_or_unknown(string? authorization)
    {
        ApiAccessAuthorizer authorizer = Create();
        authorizer.ResolveCapability(authorization).ShouldBeNull();
    }

    [Fact]
    public void ResolveCapability_returns_read_for_the_read_token()
    {
        ApiAccessAuthorizer authorizer = Create(read: "read-token", write: "write-token");
        authorizer.ResolveCapability("Bearer read-token").ShouldBe(ApiCapability.Read);
    }

    [Fact]
    public void ResolveCapability_returns_write_for_the_write_token()
    {
        ApiAccessAuthorizer authorizer = Create(read: "read-token", write: "write-token");
        authorizer.ResolveCapability("Bearer write-token").ShouldBe(ApiCapability.Write);
    }
}
