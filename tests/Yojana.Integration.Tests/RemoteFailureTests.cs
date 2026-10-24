using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Tests;

public sealed class RemoteFailureTests
{
    public static IEnumerable<object[]> Causes()
    {
        yield return [new HttpRequestException("network", new SocketException()), RemoteFailureCode.Unreachable];
        yield return [new HttpRequestException("TLS", new AuthenticationException()), RemoteFailureCode.TlsHandshake];
        yield return [new CertificateRejectedException(new string('A', 64), new string('B', 64)), RemoteFailureCode.CertificateRejected];
        yield return [new TestRemoteException(HttpStatusCode.Unauthorized, "login refused"), RemoteFailureCode.AuthenticationRefused];
        yield return [new TestRemoteException(HttpStatusCode.Forbidden, "permission refused"), RemoteFailureCode.AuthorisationRefused];
        yield return [new TestRemoteException(HttpStatusCode.BadRequest, "field refused"), RemoteFailureCode.RequestRefused];
        yield return [new TestRemoteException(HttpStatusCode.TooManyRequests, "slow down"), RemoteFailureCode.RateLimited];
        yield return [new TaskCanceledException("late"), RemoteFailureCode.Timeout];
        yield return [new TestRemoteException(HttpStatusCode.InternalServerError, "failed"), RemoteFailureCode.RemoteError];
        yield return [new InvalidOperationException("surprise"), RemoteFailureCode.Unknown];
    }

    [Theory]
    [MemberData(nameof(Causes))]
    public void Shared_classifier_produces_each_closed_cause(Exception exception, string expected) =>
        Assert.Equal(expected, RemoteFailureClassifier.Classify(exception).Code);

    [Fact]
    public void Detail_is_short_and_removes_secrets_and_email_addresses()
    {
        var input = "Property 'UpdateTime' is invalid; password=hunter2 token:abc123 Cookie: sessionid=private Authorization: Bearer secret user@example.test "
            + new string('x', 400);
        var detail = RemoteFailureClassifier.Sanitise(input);
        Assert.DoesNotContain("hunter2", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("private", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", detail, StringComparison.Ordinal);
        Assert.Contains("UpdateTime", detail, StringComparison.Ordinal);
        Assert.True(detail.Length <= RemoteFailureClassifier.DetailLimit);
    }

    [Theory]
    [InlineData("Set-Cookie: B1SESSION=6c1f0d2e-private; path=/", "6c1f0d2e-private")]
    [InlineData("ROUTEID=.node-private", ".node-private")]
    [InlineData("{\"UserName\":\"manager\",\"Password\":\"hunter2\"}", "hunter2")]
    [InlineData("request carried Bearer eyJhbGciOi.private.sig", "eyJhbGciOi.private.sig")]
    [InlineData("X-Api-Token: tok-private", "tok-private")]
    public void Detail_removes_prefixed_session_cookies_quoted_secrets_and_bare_bearer_tokens(string input, string secret) =>
        Assert.DoesNotContain(secret, RemoteFailureClassifier.Sanitise(input), StringComparison.Ordinal);

    [Fact]
    public void Old_relay_failure_becomes_unknown_and_new_failure_keeps_its_cause()
    {
        Assert.Equal(RemoteFailureCode.Unknown, RemoteFailureClassifier.FromWire(null, "old failure").Code);
        var current = RemoteFailureClassifier.FromWire(RemoteFailureCode.RateLimited, "wait");
        Assert.Equal(RemoteFailureCode.RateLimited, current.Code);
        Assert.Equal("wait", current.Detail);
    }

    private sealed class TestRemoteException(HttpStatusCode status, string detail)
        : Exception(detail), IRemoteFailureSource
    {
        public HttpStatusCode FailureStatusCode => status;
        public string? FailureDetail => Message;
    }
}
