using Aspire.Hosting;
using Aspire.Hosting.Publishing;
using Zitadel.Api;
using Zitadel.App.V1;
using Zitadel.Management.V1;
using Zitadel.Org.V2;
using Zitadel.Project.V1;
using Zitadel.V1;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
    //.WithDataVolume();

var database = postgres.AddDatabase("zitadel-db", "zitadel");

string clientId = "?";
string clientSecret = "?";

var zitadel = builder.AddZitadel("zitadel")
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

var project = zitadel.AddProject("zitadel-project", "Aspire")
   .WithInitialization(InitializeProject);

var web = builder.AddProject<Projects.CommunityToolkit_Aspire_Hosting_Zitadel_Web>("webfrontend")
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
    var clientIdParam = builder.AddParameter("clientId");
    var clientSecretParam = builder.AddParameter("clientSecret", secret: true);

    web.WithEnvironment("OpenIDConnectSettings__ClientId", clientIdParam);
    web.WithEnvironment("OpenIDConnectSettings__ClientSecret", clientSecretParam);
}

await builder.Build().RunAsync();

async Task InitializeZitadel(Clients.Options options, IResource _)
{
    var orgService = Clients.OrganizationService(options);
    var organizations = await orgService.ListOrganizationsAsync(new ListOrganizationsRequest());
    var organization = organizations.Result[0];
    Console.WriteLine($"Organization: {organization.Name} {organization.Id}");
}

async Task InitializeProject(Clients.Options options, IResource resource)
{
    var project = (ZitadelProjectResource)resource;
    var managementService = Clients.ManagementService(options);

    // add OIDC app if not exists
    var appsResult = await managementService.ListAppsAsync(new ListAppsRequest()
    {
        ProjectId = project.ProjectId, Queries = { new AppQuery() { NameQuery = new AppNameQuery() { Name = "webfrontend-oidc", Method = TextQueryMethod.Equals } } }
    });

    if (appsResult.Result.Count == 0)
    {
        var oidcAppRequest = new AddOIDCAppRequest()
        {
            AppType = OIDCAppType.Web,
            Name = "webfrontend-oidc",
            ProjectId = project.ProjectId,
            AccessTokenType = OIDCTokenType.Jwt,
            AuthMethodType = OIDCAuthMethodType.Basic
        };
        oidcAppRequest.RedirectUris.Add("https://localhost:8502/signin-zitadel");
        oidcAppRequest.PostLogoutRedirectUris.Add("https://localhost:8502/signout-callback-oidc");
        var oidcApp = await managementService.AddOIDCAppAsync(oidcAppRequest);

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
    var rolesResponse = await managementService.ListProjectRolesAsync(new ListProjectRolesRequest()
    {
        ProjectId = project.ProjectId, Queries = { new RoleQuery() { KeyQuery = new RoleKeyQuery() { Key = "AppAdmin", Method = TextQueryMethod.Equals } } }
    });

    if (rolesResponse.Result.Count == 0)
    {
        await managementService.AddProjectRoleAsync(new AddProjectRoleRequest() { ProjectId = project.ProjectId, RoleKey = "AppAdmin", DisplayName = "Application Admin" });
    }
}