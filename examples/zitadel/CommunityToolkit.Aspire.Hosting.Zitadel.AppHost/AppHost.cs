using CommunityToolkit.Aspire.Hosting.Zitadel.AppHost;
using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres");

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("zitadel-db", "zitadel");

var zitadel = builder.AddZitadel("zitadel", port: 8501, useHttps: true)
    .WithDeveloperCertificate()
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
    .WithLoginClientKey()
    .WithInitialization(CustomZitadelInitialization.Initialize);

zitadel.AddZitadelLoginClient("zitadel-login", port: 8503)
    .WithExternalHttpEndpoints();

var project = zitadel.AddProject("zitadel-project", "Aspire")
    .WithInitialization(CustomZitadelInitialization.InitializeProject);

builder.AddProject<CommunityToolkit_Aspire_Hosting_Zitadel_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(zitadel)
    .WithEnvironment("OpenIDConnectSettings__Authority", zitadel.Resource.PrimaryEndpoint)
    .WithEnvironment("OpenIDConnectSettings__ClientId", () => CustomZitadelInitialization.ClientId)
    .WithEnvironment("OpenIDConnectSettings__ClientSecret", () => CustomZitadelInitialization.ClientSecret)
    .WaitFor(project);

await builder.Build().RunAsync();