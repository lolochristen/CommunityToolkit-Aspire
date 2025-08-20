using Microsoft.AspNetCore.Authentication.Cookies;
using CommunityToolkit.Aspire.Hosting.Zitadel.ServiceDefaults;
using CommunityToolkit.Aspire.Hosting.Zitadel.Web;
using CommunityToolkit.Aspire.Hosting.Zitadel.Web.Components;
using Zitadel.Authentication;
using Zitadel.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();
builder.Services.AddDistributedMemoryCache();

var oidcConfig = builder.Configuration.GetSection("OpenIDConnectSettings");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = ZitadelDefaults.AuthenticationScheme;
        options.DefaultSignOutScheme = ZitadelDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.SameSite = SameSiteMode.Strict;
    })
    .AddZitadel(options =>
    {
        options.Authority = oidcConfig["Authority"];
        options.ClientId = oidcConfig["ClientId"];
        options.ClientSecret = oidcConfig["ClientSecret"];

        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("offline_access");

        options.SaveTokens = true;
    });



builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseOutputCache();

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();
app.MapAuthenticationEndpoints();

await app.RunAsync();
