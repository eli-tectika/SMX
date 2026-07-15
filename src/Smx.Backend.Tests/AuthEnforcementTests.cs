using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Smx.Backend.Tests;

public class AuthEnforcementTests
{
    // Boot the real app WITH auth configured (dummy tenant/audience — never contacted without a token).
    //
    // Uses IWebHostBuilder.UseSetting, not ConfigureAppConfiguration(AddInMemoryCollection): Program.cs's
    // conditional service registration (mirroring the Cosmos wiring: `if (config[...] is {...})`) has to
    // read config BEFORE builder.Build() to decide whether to call AddAuthentication/AddAuthorizationBuilder
    // at all — WebApplication auto-adds UseAuthentication/UseAuthorization middleware whenever those
    // services are present, regardless of whether Program.cs explicitly calls app.UseAuthentication(), so
    // the services themselves (not just the middleware) must stay conditional. ConfigureAppConfiguration's
    // added source is only merged into the ConfigurationManager partway through the Build() call (an
    // ASP.NET Core diagnostics-event hook), which is too late for code that runs before Build() — so a
    // config-driven `if` there always sees it as absent. UseSetting writes into IWebHostBuilder's settings
    // directly and IS visible to builder.Configuration before Build() returns (verified empirically).
    static WebApplicationFactory<Program> AuthedHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ENTRA_TENANT_ID", "11111111-1111-1111-1111-111111111111");
            b.UseSetting("API_CLIENT_ID", "22222222-2222-2222-2222-222222222222");
        });

    [Fact]
    public async Task Healthz_stays_anonymous_when_auth_is_on()
    {
        using var client = AuthedHost().CreateClient();
        var res = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_a_token()
    {
        using var client = AuthedHost().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/projects/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
