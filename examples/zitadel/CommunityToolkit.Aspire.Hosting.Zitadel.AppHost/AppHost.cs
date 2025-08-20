using Zitadel.App.V1;
using Zitadel.Management.V1;
using Zitadel.Org.V2;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");

var database = postgres.AddDatabase("zitadel-db", "zitadel");

string clientId = "?";

var zitadel = builder.AddZitadel("zitadel")
    .WithHttpsEndpointUsingDevCertificate(8501)
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("TESTO")
    .WithMachineUser()
    .WithInitialization(async (options, _) =>
    {
        var orgService = Zitadel.Api.Clients.OrganizationService(options);
        var orgs = await orgService.ListOrganizationsAsync(new ListOrganizationsRequest());

        var org = orgs.Result[0];
        Console.WriteLine($"Org: {org.Name} {org.Id}");
    });

var project = zitadel.AddProject("test1", "test1")
   .WithInitialization(async (options, resource) =>
   {
       var project = (ZitadelProjectResource) resource;

       var managementService = Zitadel.Api.Clients.ManagementService(options);

       var oidcAppRequest = new AddOIDCAppRequest() { AppType = OIDCAppType.Web, Name = "WebApp", ProjectId = project.ProjectId, AccessTokenType = OIDCTokenType.Jwt};
       oidcAppRequest.RedirectUris.Add("https://localhost:8502/signin-oidc");
       var oidcApp = await managementService.AddOIDCAppAsync(oidcAppRequest);
       
       clientId = oidcApp.ClientId;
   });

// test container
builder.AddContainer("webapp", "whalesalad/docker-debug", "latest")
    .WithHttpEndpoint(8502, 8080)
    .WithReference(zitadel)
    .WithEnvironment("OIDC_AUTHORITY", () => zitadel.Resource.PrimaryEndpoint.Url)
    .WithEnvironment("OIDC_CLIENT_ID", () => clientId)
    .WithEnvironment("PROJECT_ID", () => project.Resource.ProjectId ?? "?")
    .WaitFor(project);

await builder.Build().RunAsync();
