using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// Authenticates a request when it carries <see cref="HeaderName"/>, and reports no result
/// otherwise so the authorization middleware issues a real challenge. A stub that always
/// succeeded would make the tests that matter — the unauthenticated ones — vacuous.
/// </summary>
internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    internal const string SchemeName = "Test";
    internal const string HeaderName = "X-Test-User";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var user) || string.IsNullOrEmpty(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user!)], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
