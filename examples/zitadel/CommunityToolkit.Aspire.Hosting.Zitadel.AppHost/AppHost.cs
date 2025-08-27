using CommunityToolkit.Aspire.Hosting.Zitadel.AppHost;
using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("zitadel-db", "zitadel");

var zitadel = builder.AddZitadel("zitadel", port: 8501, useHttps: true)
    .WithDeveloperCertificate()
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
    .WithLoginClientKey()
    .WithInitialization(ZitadelInitialization.Initialize);

zitadel.AddZitadelLoginClient("zitadel-login", port: 8503)
    .WithExternalHttpEndpoints();

var project = zitadel.AddProject("zitadel-project", "Aspire")
    .WithInitialization(ZitadelInitialization.InitializeProject);

var web = builder.AddProject<CommunityToolkit_Aspire_Hosting_Zitadel_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(zitadel)
    .WithEnvironment("OpenIDConnectSettings__Authority", zitadel.Resource.PrimaryEndpoint)
    .WaitFor(project);

if (builder.ExecutionContext.IsRunMode)
{
    web.WithEnvironment("OpenIDConnectSettings__ClientId", () => ZitadelInitialization.ClientId);
    web.WithEnvironment("OpenIDConnectSettings__ClientSecret", () => ZitadelInitialization.ClientSecret);
}
else
{
    var clientIdParam = builder.AddParameter("clientId");
    var clientSecretParam = builder.AddParameter("clientSecret", true);

    web.WithEnvironment("OpenIDConnectSettings__ClientId", clientIdParam);
    web.WithEnvironment("OpenIDConnectSettings__ClientSecret", clientSecretParam);
}

await builder.Build().RunAsync();