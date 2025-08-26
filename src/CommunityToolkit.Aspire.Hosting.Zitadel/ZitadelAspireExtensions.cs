using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using Zitadel.Api;
using Zitadel.Management.V1;
using Zitadel.Project.V1;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting;

/// <summary>
/// </summary>
public static class ZitadelAspireExtensions
{
    private const int DefaultContainerPort = 8080;
    private const int DefaultContainerPortLoginClient = 3000;
    private const string DatabaseToken = "Database=";

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="name"></param>
    /// <param name="adminUsername"></param>
    /// <param name="adminPassword"></param>
    /// <param name="masterKey"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> AddZitadel(this IDistributedApplicationBuilder builder, string name, IResourceBuilder<ParameterResource>? adminUsername = null,
        IResourceBuilder<ParameterResource>? adminPassword = null, IResourceBuilder<ParameterResource>? masterKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        ParameterResource passwordParameter = adminPassword?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");
        ParameterResource masterKeyParameter = masterKey?.Resource ??
                                               ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-masterkey", true,
                                                   new GenerateParameterDefault { MinLength = 32 });

        ZitadelResource zitadel = new(name, adminUsername?.Resource, passwordParameter, masterKeyParameter);

        return builder
            .AddResource(zitadel)
            .WithImage(ZitadelContainerImageTags.Image)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.Tag)
            // zitadel does not support generic otlp paramters yet, pending request to support it.
            // .WithOtlpExporter()
            // current oltp parameters only support non-authorized endpoint on port 443.
            // .WithEnvironment("ZITADEL_TRACING_TYPE", "otel")
            // .WithEnvironment("ZITADEL_TRACING_ENDPOINT", builder.Configuration.GetValue<string>("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"))
            // .WithEnvironment("ZITADEL_TRACING_SERVICENAME", name)
            .WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "false") // https://github.com/zitadel/zitadel/issues/10526
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORDCHANGEREQUIRED", "false")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["ZITADEL_FIRSTINSTANCE_ORG_HUMAN_USERNAME"] = zitadel.AdminReference;
                context.EnvironmentVariables["ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORD"] = zitadel.AdminPasswordParameter;
                context.EnvironmentVariables["ZITADEL_MASTERKEY"] = zitadel.MasterKeyParameter;
            })
            .WithArgs("start-from-init", "--masterkeyFromEnv")
            .OnResourceReady(async (zitadel, @event, ct) =>
            {
                Clients.Options? options = null;

                zitadel.TryGetAnnotationsOfType<ZitadelInitializationAnnotation>(out IEnumerable<ZitadelInitializationAnnotation>? initAnnotations);
                if (initAnnotations != null)
                {
                    foreach (ZitadelInitializationAnnotation annotation in initAnnotations)
                    {
                        if (options == null)
                        {
                            options = zitadel.CreateApiClientOptions();
                        }

                        await annotation.Initialization.Invoke(options, zitadel);
                    }
                }

                ResourceNotificationService notificationService = @event.Services.GetRequiredService<ResourceNotificationService>();

                foreach (KeyValuePair<string, string> pair in zitadel.Projects)
                {
                    if (builder.Resources.FirstOrDefault(n => string.Equals(n.Name, pair.Key, StringComparison.InvariantCultureIgnoreCase)) is ZitadelProjectResource
                        zitadelProject)
                    {
                        if (options == null)
                        {
                            options = zitadel.CreateApiClientOptions();
                        }

                        await CreateZitadelProject(options, zitadelProject, notificationService);
                    }
                }
            });
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="database"></param>
    /// <param name="userPassword"></param>
    /// <param name="sslModeEnabled"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithPostgresDatabase(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<IResourceWithConnectionString> database, IResourceBuilder<ParameterResource>? userPassword = null, bool sslModeEnabled = false)
    {
        ParameterResource userPasswordParameter = userPassword?.Resource ??
                                                  ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder.ApplicationBuilder,
                                                      $"{database.Resource.Name}-user-password");

        builder.WithEnvironment(context =>
            {
                // extract the server parameters generically from IResourceWithConnectionString to support different postgres resources
                IResourceWithConnectionString serverResource;
                if (database.Resource is IResourceWithParent parentResource)
                {
                    serverResource = (IResourceWithConnectionString)parentResource.Parent;
                }
                else
                {
                    throw new InvalidOperationException("Parameter must be a database resource created from a parent server resource.");
                }

                string? part = database.Resource.ConnectionStringExpression.ValueExpression.Split(';').FirstOrDefault(p => p.StartsWith(DatabaseToken));
                if (part == null)
                {
                    throw new InvalidOperationException("could not parse ConnectionString for database name");
                }

                string databaseName = part.Substring(DatabaseToken.Length);

                // Assumption: ConnectionStrings are using ValueProvider in the format: "Host=host;Port=port;Username=username;Password=password;Database=database"
                if (serverResource.ConnectionStringExpression.ValueProviders.Count < 4)
                {
                    throw new InvalidOperationException("server of database has not proper connection string format");
                }

                if (context.ExecutionContext.IsRunMode && serverResource is ContainerResource serverContainerResource)
                {
                    // workaround to resolve container internal host and port, default resolving only returns external host and port
                    context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_PORT"] = serverContainerResource.GetEndpoint("tcp").Property(EndpointProperty.TargetPort);
                    context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_HOST"] = serverContainerResource.Name; // use name 
                }
                else
                {
                    context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_HOST"] = serverResource.ConnectionStringExpression.ValueProviders[0];
                    context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_PORT"] = serverResource.ConnectionStringExpression.ValueProviders[1];
                }

                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_DATABASE"] = databaseName;
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_USER_USERNAME"] = "zitadel-user";
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_USER_PASSWORD"] = userPasswordParameter;
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_USER_SSL_MODE"] = sslModeEnabled ? "enable" : "disable";
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME"] = serverResource.ConnectionStringExpression.ValueProviders[2];
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD"] = serverResource.ConnectionStringExpression.ValueProviders[3];
                context.EnvironmentVariables["ZITADEL_DATABASE_POSTGRES_ADMIN_SSL_MODE"] = sslModeEnabled ? "enable" : "disable";
            })
            .WithReference(database)
            .WaitFor(database);

        return builder;
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="machineUser"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithMachineUser(this IResourceBuilder<ZitadelResource> builder, string machineUser = "admin")
    {
        string path = Path.GetFullPath($"./{builder.Resource.Name}-keys");

        builder.Resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out IEnumerable<ContainerMountAnnotation>? mountAnnotations);
        if (mountAnnotations == null || mountAnnotations.All(m => m.Target != "/keys"))
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            builder.WithBindMount(path, "/keys");
        }

        builder
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_MACHINEKEYPATH", $"/keys/{machineUser}.json")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME", machineUser)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME", "Automatically Initialized IAM_OWNER")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINEKEY_TYPE", "1");

        builder.Resource.MachineUserKeyPath = Path.Combine(path, $"{machineUser}.json");

        return builder;
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="certificateDestinationPath"></param>
    /// <param name="port"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithHttpsEndpointUsingDevCertificate(this IResourceBuilder<ZitadelResource> builder, int? port = null,
        string certificateDestinationPath = "/certificate")
    {
        var (certPath, keyPath) = ExportDevCertificate(builder.ApplicationBuilder);
        return builder.WithHttpsEndpoint(certPath, keyPath, port, certificateDestinationPath);
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="certificatePath"></param>
    /// <param name="keyPath"></param>
    /// <param name="certificateDestinationPath"></param>
    /// <param name="port"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithHttpsEndpoint(this IResourceBuilder<ZitadelResource> builder, string certificatePath, string keyPath, int? port = null,
        string certificateDestinationPath = "/certificate")
    {
        string certFileName = Path.GetFileName(certificatePath);
        string certKeyFileName = Path.GetFileName(keyPath);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.WithContainerFiles(certificateDestinationPath, [
                new ContainerFile { Name = certFileName, Contents = File.ReadAllText(certificatePath) },
                new ContainerFile { Name = certKeyFileName, Contents = File.ReadAllText(keyPath) }
            ]);
        }
        else
        {
            string sourceFullPath = Path.GetFullPath(Path.GetDirectoryName(certificatePath) ?? ".", builder.ApplicationBuilder.AppHostDirectory);
            builder.WithBindMount(sourceFullPath, certificateDestinationPath);
        }

        builder.Resource.PrimaryEndpointName = "https";

        return builder.WithHttpsEndpoint(port, DefaultContainerPort)
            .WithEnvironment("ZITADEL_TLS_CERTPATH", certificateDestinationPath + "/" + certFileName)
            .WithEnvironment("ZITADEL_TLS_KEYPATH", certificateDestinationPath + "/" + certKeyFileName)
            .WithEnvironment("ZITADEL_TLS_ENABLED", "true")
            .WithEnvironment("ZITADEL_EXTERNALSECURE", "true")
            .WithEnvironment("ZITADEL_EXTERNALPORT", builder.GetEndpoint("https").Property(EndpointProperty.Port))
            .WithEnvironment("ZITADEL_EXTERNALDOMAIN", builder.GetEndpoint("https").Property(EndpointProperty.Host))
            .WithHttpHealthCheck("debug/healthz", endpointName: "https");
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="port"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithHttpEndpoint(this IResourceBuilder<ZitadelResource> builder, int? port = null)
    {
        return builder.WithHttpEndpoint(port, DefaultContainerPort)
            .WithEnvironment("ZITADEL_TLS_ENABLED", "false")
            .WithEnvironment("ZITADEL_EXTERNALSECURE", "false")
            .WithEnvironment("ZITADEL_EXTERNALPORT", builder.GetEndpoint("http").Property(EndpointProperty.Port))
            .WithEnvironment("ZITADEL_EXTERNALDOMAIN", builder.GetEndpoint("http").Property(EndpointProperty.Host));
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="loginUser"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithLoginClientKey(this IResourceBuilder<ZitadelResource> builder, string loginUser = "login-client")
    {
        builder.Resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out IEnumerable<ContainerMountAnnotation>? mountAnnotations);
        if (mountAnnotations == null || mountAnnotations.All(m => m.Target != "/keys"))
        {
            string path = Path.GetFullPath($"./{builder.Resource.Name}-keys");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            builder.WithBindMount(path, "/keys");
        }

        builder
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_LOGINCLIENTPATPATH", $"/keys/{loginUser}.pat")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_USERNAME", loginUser)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_NAME", "Automatically Initialized IAM_LOGIN_CLIENT")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_PAT_EXPIRATIONDATE", DateTime.UtcNow.AddYears(5).ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return builder;
    }

    /// <summary>
    /// </summary>
    /// <param name="zitadel"></param>
    /// <param name="name"></param>
    /// <param name="port"></param>
    /// <param name="loginUser"></param>
    /// <param name="serviceAccessToken"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelLoginClientResource> AddZitadelLoginClient(this IResourceBuilder<ZitadelResource> zitadel, string name, int? port = null,
        string loginUser = "login-client", IResourceBuilder<ParameterResource>? serviceAccessToken = null)
    {
        string path = Path.GetFullPath($"./{zitadel.Resource.Name}-keys");

        ParameterResource serviceAccessTokenParameter = serviceAccessToken?.Resource ??
                                                        new ParameterResource(name, @default => File.ReadAllText(Path.Combine(path, $"{loginUser}.pat")), true);

        ZitadelLoginClientResource loginClientResource = new(name, serviceAccessTokenParameter);

        IResourceBuilder<ZitadelLoginClientResource> login = zitadel.ApplicationBuilder.AddResource(loginClientResource)
            .WithImage(ZitadelContainerImageTags.LoginImage)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.LoginTag)
            .WithHttpEndpoint(port, DefaultContainerPortLoginClient)
            .WithEnvironment("ZITADEL_API_URL", zitadel.Resource.PrimaryEndpoint)
            .WithEnvironment("NEXT_PUBLIC_BASE_PATH", ZitadelLoginClientResource.BasePath)
            .WithEnvironment("ZITADEL_SERVICE_USER_TOKEN", serviceAccessTokenParameter)
            .WithEnvironment("NODE_TLS_REJECT_UNAUTHORIZED", "0")
            .WithEnvironment("CUSTOM_REQUEST_HEADERS", "Host:localhost")
            .WithHttpHealthCheck(ZitadelLoginClientResource.BasePath + "/healthy", 200)
            .WaitFor(zitadel);

        zitadel.WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "true")
            .WithEnvironment(async context =>
            {
                // workaround to resolve external address of login client
                if (zitadel.ApplicationBuilder.ExecutionContext.IsRunMode)
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] =
                        await login.Resource.BaseEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = await login.Resource.OidcLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = await login.Resource.OidcLogoutEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = await login.Resource.SamlLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                }
                else
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = login.Resource.BaseEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = login.Resource.OidcLoginEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = login.Resource.OidcLogoutEndpoint;
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = login.Resource.SamlLoginEndpoint;
                }
            });

        return login;
    }


    /// <summary>
    ///     Initializes the Zitadel resource with the provided initialization function.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="initialization"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithInitialization(this IResourceBuilder<ZitadelResource> builder,
        Func<Clients.Options, IResource, Task> initialization)
    {
        return builder.WithAnnotation(new ZitadelInitializationAnnotation(initialization));
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="initialization"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelProjectResource> WithInitialization(this IResourceBuilder<ZitadelProjectResource> builder,
        Func<Clients.Options, IResource, Task> initialization)
    {
        return builder.WithAnnotation(new ZitadelInitializationAnnotation(initialization));
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="name"></param>
    /// <param name="projectName"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelProjectResource> AddProject(this IResourceBuilder<ZitadelResource> builder, string name, string projectName)
    {
        return builder.AddProject(name, new AddProjectRequest { Name = projectName });
    }

    /// <summary>
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="name"></param>
    /// <param name="projectRequest"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelProjectResource> AddProject(this IResourceBuilder<ZitadelResource> builder, string name, AddProjectRequest projectRequest)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(projectRequest);
        ArgumentNullException.ThrowIfNull(projectRequest.Name);

        ZitadelProjectResource zitadelProject = new(name, projectRequest, builder.Resource);
        builder.Resource.AddProject(name, projectRequest.Name);

        return builder.ApplicationBuilder.AddResource(zitadelProject)
            .WithInitialState(new CustomResourceSnapshot
            {
                Properties = [], ResourceType = "ZitadelProject", State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, KnownResourceStateStyles.Info)
            })
            .ExcludeFromManifest();
    }

    /// <summary>
    ///     Sets the organization name for the ZITADEL instance.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="organizationName"></param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithOrganizationName(this IResourceBuilder<ZitadelResource> builder, string organizationName)
    {
        builder.Resource.OrganizationName = organizationName;
        return builder.WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_NAME", organizationName);
    }

    private static async Task CreateZitadelProject(Clients.Options options, ZitadelProjectResource zitadelProject, ResourceNotificationService resourceNotificationService)
    {
        await resourceNotificationService.PublishUpdateAsync(zitadelProject,
            state => state with { State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info) });

        ManagementService.ManagementServiceClient managementService = Clients.ManagementService(options);

        ListProjectsResponse? projects = await managementService.ListProjectsAsync(new ListProjectsRequest());

        Project? project = projects.Result.FirstOrDefault(p => p.Name == zitadelProject.ProjectRequest.Name);

        string projectId;
        if (project == null)
        {
            AddProjectResponse? projectAddResponse = await managementService.AddProjectAsync(zitadelProject.ProjectRequest);
            projectId = projectAddResponse.Id;
        }
        else
        {
            projectId = project.Id;
        }

        zitadelProject.ProjectId = projectId;


        zitadelProject.TryGetAnnotationsOfType<ZitadelInitializationAnnotation>(out IEnumerable<ZitadelInitializationAnnotation>? initAnnotations);
        if (initAnnotations != null)
        {
            foreach (ZitadelInitializationAnnotation annotation in initAnnotations)
            {
                await annotation.Initialization.Invoke(options, zitadelProject);
            }
        }

        await resourceNotificationService.PublishUpdateAsync(zitadelProject,
            state => state with { State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success) });
    }

    private static (string, string) ExportDevCertificate(IDistributedApplicationBuilder builder)
    {
        // Exports the ASP.NET Core HTTPS development certificate & private key to PEM files using 'dotnet dev-certs https' to a temporary
        // directory and returns the path.

        byte[] appNameHashBytes = XxHash64.Hash(Encoding.Unicode.GetBytes(builder.Environment.ApplicationName).AsSpan());
        string appNameHash = BitConverter.ToString(appNameHashBytes).Replace("-", "").ToLowerInvariant();
        string tempDir = Path.Combine(Path.GetTempPath(), $"aspire.{appNameHash}");
        string certExportPath = Path.Combine(tempDir, "dev-cert.pem");
        string certKeyExportPath = Path.Combine(tempDir, "dev-cert.key");

        if (File.Exists(certExportPath) && File.Exists(certKeyExportPath))
        {
            // Certificate already exported, return the path.
            return (certExportPath, certKeyExportPath);
        }

        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }

        Directory.CreateDirectory(tempDir);

        Process exportProcess = Process.Start("dotnet", $"dev-certs https --export-path \"{certExportPath}\" --format Pem --no-password");

        bool exited = exportProcess.WaitForExit(TimeSpan.FromSeconds(5));
        if (exited && File.Exists(certExportPath) && File.Exists(certKeyExportPath))
        {
            return (certExportPath, certKeyExportPath);
        }

        if (exportProcess.HasExited && exportProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"HTTPS dev certificate export failed with exit code {exportProcess.ExitCode}");
        }

        if (!exportProcess.HasExited)
        {
            exportProcess.Kill(true);
            throw new InvalidOperationException("HTTPS dev certificate export timed out");
        }

        throw new InvalidOperationException("HTTPS dev certificate export failed for an unknown reason");
    }
}