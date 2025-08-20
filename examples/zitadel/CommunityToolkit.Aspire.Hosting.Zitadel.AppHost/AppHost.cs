using Zitadel.Api;
using Zitadel.App.V1;
using Zitadel.Management.V1;
using Zitadel.Org.V2;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");

var database = postgres.AddDatabase("zitadel-db", "zitadel");

string clientId = "?";
string clientSecret = "?";

var zitadel = builder.AddZitadel("zitadel")
    .WithHttpsEndpointUsingDevCertificate(8501)
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
    .WithInitialization(InitializeZitadel);

var project = zitadel.AddProject("zitadel-project", "Aspire")
   .WithInitialization(InitializeProject);

builder.AddProject<Projects.CommunityToolkit_Aspire_Hosting_Zitadel_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(zitadel)
    .WithEnvironment("OpenIDConnectSettings__Authority", zitadel.Resource.PrimaryEndpoint)
    .WithEnvironment("OpenIDConnectSettings__ClientId", () => clientId)
    .WithEnvironment("OpenIDConnectSettings__ClientSecret", () => clientSecret)
    .WaitFor(project);

await builder.Build().RunAsync();

async Task InitializeZitadel(Clients.Options options, IResource _)
{
    var orgService = Clients.OrganizationService(options);
    var organizations = await orgService.ListOrganizationsAsync(new ListOrganizationsRequest());
    var organization = organizations.Result[0];
    Console.WriteLine($"Org: {organization.Name} {organization.Id}");
}

async Task InitializeProject(Clients.Options options, IResource resource)
{
    var project = (ZitadelProjectResource)resource;
    var managementService = Clients.ManagementService(options);

    var oidcAppRequest = new AddOIDCAppRequest()
    {
        AppType = OIDCAppType.Web,
        Name = "webfrontend-oidc",
        ProjectId = project.ProjectId,
        AccessTokenType = OIDCTokenType.Jwt,
        AuthMethodType = OIDCAuthMethodType.Basic
    };
    oidcAppRequest.RedirectUris.Add("https://localhost:8502/signin-zitadel");
    var oidcApp = await managementService.AddOIDCAppAsync(oidcAppRequest);

    clientId = oidcApp.ClientId;
    clientSecret = oidcApp.ClientSecret;
}
