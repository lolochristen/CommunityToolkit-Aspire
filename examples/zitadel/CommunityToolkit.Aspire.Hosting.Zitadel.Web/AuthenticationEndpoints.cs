using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Primitives;
using Zitadel.Authentication;

namespace CommunityToolkit.Aspire.Hosting.Zitadel.Web;

public static class AuthenticationEndpoints
{
    public static WebApplication MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapGet("/login", async context =>
        {
            StringValues returnUrl = context.Request.Query["returnUrl"];

            await context.ChallengeAsync(ZitadelDefaults.AuthenticationScheme,
                new AuthenticationProperties { RedirectUri = returnUrl == StringValues.Empty ? "/" : returnUrl.ToString() });
        }).AllowAnonymous();

        app.MapPost("/logout", async context =>
        {
            if (context.User.Identity?.IsAuthenticated ?? false)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignOutAsync(ZitadelDefaults.AuthenticationScheme);
            }
            else
            {
                context.Response.Redirect("/");
            }
        }).AllowAnonymous();

        return app;
    }
}