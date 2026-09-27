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

    [Fact]
    public void Token_matching_read_capability_does_not_leak_write_when_single_write_token_present()
    {
        // The read cap path requires the read hash; a token that is only the read value never
        // satisfies write even though write is computed first on every call.
        ApiAccessAuthorizer authorizer = Create(read: "read-token", write: "write-token");
        authorizer.Authorize("Bearer read-token", ApiCapability.Write).ShouldBeFalse();
    }
}
