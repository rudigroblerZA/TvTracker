using System.Security.Claims;
using Microsoft.Extensions.Hosting;

namespace Catalog.UnitTests;

/// <summary>
/// Who may call the sync worker's internal APIs: a service signed in as the internal client. A person's token
/// is refused however valid, and so is another service account, such as a reporting job's.
/// </summary>
public class InternalCallerTests
{
    private static readonly string[] Internal = [IdentitySettings.InternalClient];

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void The_internal_client_is_an_internal_caller() =>
        Assert.True(AuthenticationExtensions.IsInternalCaller(Token(("sub", "1"), ("azp", "tvtracker-internal"),
            ("client_id", "tvtracker-internal"), ("preferred_username", "service-account-tvtracker-internal")), Internal));

    [Fact]
    public void A_person_signed_in_through_any_client_is_not() =>
        Assert.False(AuthenticationExtensions.IsInternalCaller(Token(("sub", "demo"), ("azp", "tvtracker-internal"),
            ("preferred_username", "demo")), Internal));

    [Fact]
    public void Another_service_account_is_not() =>
        Assert.False(AuthenticationExtensions.IsInternalCaller(Token(("sub", "2"), ("azp", "tvtracker-reports"), ("client_id", "tvtracker-reports"),
            ("preferred_username", "service-account-tvtracker-reports")), Internal));
}
