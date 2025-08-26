using Projects;
using Zitadel.Api;
using Zitadel.App.V1;
using Zitadel.Management.V1;
using Zitadel.Org.V2;
using Zitadel.Project.V1;
using Zitadel.V1;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres");
//.WithDataVolume();

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("zitadel-db", "zitadel");

string clientId = "?";
string clientSecret = "?";

IResourceBuilder<ZitadelResource> zitadel = builder.AddZitadel("zitadel")
    .WithHttpsEndpointUsingDevCertificate(8501)
    //.WithHttpEndpoint(8501) // if http only
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
    .WithLoginClientKey()
    .WithInitialization(InitializeZitadel);

zitadel.AddZitadelLoginClient("zitadel-login", 8503) // no support for https
    .WithExternalHttpEndpoints();

IResourceBuilder<ZitadelProjectResource> project = zitadel.AddProject("zitadel-project", "Aspire")
    .WithInitialization(InitializeProject);

IResourceBuilder<ProjectResource> web = builder.AddProject<CommunityToolkit_Aspire_Hosting_Zitadel_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(zitadel)
    .WithEnvironment("OpenIDConnectSettings__Authority", zitadel.Resource.PrimaryEndpoint)
    .WaitFor(project);

if (builder.ExecutionContext.IsRunMode)
{
    web.WithEnvironment("OpenIDConnectSettings__ClientId", () => clientId);
    web.WithEnvironment("OpenIDConnectSettings__ClientSecret", () => clientSecret);
}
else
{
    IResourceBuilder<ParameterResource> clientIdParam = builder.AddParameter("clientId");
    IResourceBuilder<ParameterResource> clientSecretParam = builder.AddParameter("clientSecret", true);

    web.WithEnvironment("OpenIDConnectSettings__ClientId", clientIdParam);
    web.WithEnvironment("OpenIDConnectSettings__ClientSecret", clientSecretParam);
}

await builder.Build().RunAsync();

async Task InitializeZitadel(Clients.Options options, IResource _)
{
    OrganizationService.OrganizationServiceClient orgService = Clients.OrganizationService(options);
    ListOrganizationsResponse? organizations = await orgService.ListOrganizationsAsync(new ListOrganizationsRequest());
    Organization? organization = organizations.Result[0];
    Console.WriteLine($"Organization: {organization.Name} {organization.Id}");
}

async Task InitializeProject(Clients.Options options, IResource resource)
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

        clientId = oidcApp.ClientId;
        clientSecret = oidcApp.ClientSecret;
        // TODO Store clientSecret
    }
    else
    {
        clientId = appsResult.Result[0].OidcConfig.ClientId;
        // TODO read and set clientSecret
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