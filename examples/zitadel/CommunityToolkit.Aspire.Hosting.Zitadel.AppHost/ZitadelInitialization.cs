using Zitadel.Api;
using Zitadel.App.V1;
using Zitadel.Management.V1;
using Zitadel.Org.V2;
using Zitadel.Project.V1;
using Zitadel.V1;

namespace CommunityToolkit.Aspire.Hosting.Zitadel.AppHost;

/// <summary>
/// 
/// </summary>
public static class ZitadelInitialization
{
    public static string ClientId { get; set; } = string.Empty;
    public static string ClientSecret { get; set; } = string.Empty;

    public static async Task Initialize(Clients.Options options, IResource _)
    {
        OrganizationService.OrganizationServiceClient orgService = Clients.OrganizationService(options);
        ListOrganizationsResponse? organizations = await orgService.ListOrganizationsAsync(new ListOrganizationsRequest());
        Organization? organization = organizations.Result[0];
        Console.WriteLine($"Organization: {organization.Name} {organization.Id}");
    }

    public static async Task InitializeProject(Clients.Options options, IResource resource)
    {
        ZitadelProjectResource project = (ZitadelProjectResource)resource;
        ManagementService.ManagementServiceClient managementService = Clients.ManagementService(options);

        // add OIDC app if not exists
        ListAppsResponse? appsResult = await managementService.ListAppsAsync(new ListAppsRequest
        {
            ProjectId = project.ProjectId, Queries = { new AppQuery { NameQuery = new AppNameQuery { Name = "webfrontend-oidc", Method = TextQueryMethod.Equals } } }
        });

        if (appsResult.Result.Count == 0)
        {
            AddOIDCAppRequest oidcAppRequest = new()
            {
                AppType = OIDCAppType.Web,
                Name = "webfrontend-oidc",
                ProjectId = project.ProjectId,
                AccessTokenType = OIDCTokenType.Jwt,
                AuthMethodType = OIDCAuthMethodType.Basic
            };
            oidcAppRequest.RedirectUris.Add("https://localhost:8502/signin-zitadel");
            oidcAppRequest.PostLogoutRedirectUris.Add("https://localhost:8502/signout-callback-oidc");
            AddOIDCAppResponse? oidcApp = await managementService.AddOIDCAppAsync(oidcAppRequest);

            ClientId = oidcApp.ClientId;
            ClientSecret = oidcApp.ClientSecret;
            await File.WriteAllTextAsync("./zitadel-keys/webfrontend-oidc.key", ClientSecret);
        }
        else
        {
            ClientId = appsResult.Result[0].OidcConfig.ClientId;
            if (File.Exists("./zitadel-keys/webfrontend-oidc.key"))
            {
                ClientSecret = await File.ReadAllTextAsync("./zitadel-keys/webfrontend-oidc.key");
            }
        }

        // add AppAdmin role if not exists
        ListProjectRolesResponse? rolesResponse = await managementService.ListProjectRolesAsync(new ListProjectRolesRequest
        {
            ProjectId = project.ProjectId, Queries = { new RoleQuery { KeyQuery = new RoleKeyQuery { Key = "AppAdmin", Method = TextQueryMethod.Equals } } }
        });

        if (rolesResponse.Result.Count == 0)
        {
            await managementService.AddProjectRoleAsync(new AddProjectRoleRequest { ProjectId = project.ProjectId, RoleKey = "AppAdmin", DisplayName = "Application Admin" });
        }
    }
}