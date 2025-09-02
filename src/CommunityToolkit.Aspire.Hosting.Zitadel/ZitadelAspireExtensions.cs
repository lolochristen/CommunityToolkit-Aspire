using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Asn1.X509;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using Zitadel.Api;
using Zitadel.Management.V1;
using Zitadel.Project.V1;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring Zitadel identity and access management resources in an Aspire application.
/// </summary>
public static class ZitadelAspireExtensions
{
    private const int DefaultContainerPort = 8080;
    private const int DefaultContainerPortLoginClient = 3000;
    private const string DatabaseToken = "Database=";

    /// <summary>
    /// Adds a Zitadel identity and access management server container to the application builder with configurable authentication settings.
    /// Zitadel provides OAuth 2.0, OIDC, and SAML authentication services for modern applications.
    /// </summary>
    /// <param name="builder">The distributed application builder to add the Zitadel resource to.</param>
    /// <param name="name">The name of the Zitadel resource for service discovery and configuration.</param>
    /// <param name="port">The optional host port to bind the Zitadel instance to. If not specified, a random port will be assigned.</param>
    /// <param name="useHttps">Whether to enable HTTPS for the Zitadel instance. Default is false.</param>
    /// <param name="adminUsername">Optional parameter resource for the admin username. If not provided, a default will be used.</param>
    /// <param name="adminPassword">Optional parameter resource for the admin password. If not provided, a secure password will be generated.</param>
    /// <param name="masterKey">Optional parameter resource for the master encryption key. If not provided, a secure key will be generated.</param>
    /// <returns>A resource builder for the Zitadel resource that can be used for further configuration.</returns>
    public static IResourceBuilder<ZitadelResource> AddZitadel(this IDistributedApplicationBuilder builder, string name, int? port = null, bool useHttps = false, IResourceBuilder<ParameterResource>? adminUsername = null,
        IResourceBuilder<ParameterResource>? adminPassword = null, IResourceBuilder<ParameterResource>? masterKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        ParameterResource passwordParameter = adminPassword?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");
        ParameterResource masterKeyParameter = masterKey?.Resource ??
                                               ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-masterkey", true,
                                                   new GenerateParameterDefault { MinLength = 32 });

        var zitadel = new ZitadelResource(name, adminUsername?.Resource, passwordParameter, masterKeyParameter, useHttps ? "https" : "http");

        var zitadelBuilder = builder
            .AddResource(zitadel)
            .WithImage(ZitadelContainerImageTags.Image)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.Tag)
            .WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "false") // when login client is used, parameters will be true
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

        if (useHttps)
        {
            zitadelBuilder.WithHttpsEndpoint(port, DefaultContainerPort)
                .WithEnvironment("ZITADEL_TLS_ENABLED", "true")
                .WithHttpHealthCheck("debug/healthz", endpointName: "https")
                .WithExternalDomain("https");
        }
        else
        {
            zitadelBuilder.WithHttpEndpoint(port, DefaultContainerPort)
                .WithEnvironment("ZITADEL_TLS_ENABLED", "false")
                .WithHttpHealthCheck("debug/healthz", endpointName: "http")
                .WithExternalDomain("http");
        }

        return zitadelBuilder;
    }

    /// <summary>
    /// Configures the Zitadel resource to use a PostgreSQL database for persistent storage of identity and access management data.
    /// This method extracts connection information from the provided database resource and configures Zitadel environment variables.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with database settings.</param>
    /// <param name="database">The PostgreSQL database resource with connection string information.</param>
    /// <param name="userPassword">Optional parameter resource for the database user password. If not provided, a secure password will be generated.</param>
    /// <param name="sslModeEnabled">Whether to enable SSL mode for database connections. Default is false.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithPostgresDatabase(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<IResourceWithConnectionString> database, IResourceBuilder<ParameterResource>? userPassword = null, bool sslModeEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        
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
    /// Configures a machine user for programmatic access to the Zitadel instance with API key authentication.
    /// Creates a service account that can be used for server-to-server communication and API automation.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with machine user settings.</param>
    /// <param name="machineUser">The machine user name for the service account. Default is "admin".</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithMachineUser(this IResourceBuilder<ZitadelResource> builder, string machineUser = "admin")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(machineUser);
        
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
    /// Configures the Zitadel resource to use the ASP.NET Core development certificate for HTTPS connections.
    /// This method exports the development certificate and configures Zitadel to use it for TLS encryption.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with the development certificate.</param>
    /// <param name="certificateDestinationPath">The container path where certificate files will be mounted. Default is "/certificate".</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithDeveloperCertificate(this IResourceBuilder<ZitadelResource> builder, string certificateDestinationPath = "/certificate")
    {
        var (certPath, keyPath) = ExportDevCertificate(builder.ApplicationBuilder);
        return builder.WithCertificate(certPath, keyPath, certificateDestinationPath);
    }

    /// <summary>
    /// Configures the Zitadel resource to use a custom SSL certificate for HTTPS connections.
    /// Allows specification of custom certificate and private key files for production scenarios.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with certificate settings.</param>
    /// <param name="certificatePath">The file system path to the SSL certificate file.</param>
    /// <param name="keyPath">The file system path to the private key file corresponding to the certificate.</param>
    /// <param name="certificateDestinationPath">The container path where certificate files will be mounted. Default is "/certificate".</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithCertificate(this IResourceBuilder<ZitadelResource> builder, string certificatePath, string keyPath, string certificateDestinationPath = "/certificate")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(certificatePath);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

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

        return builder.WithEnvironment("ZITADEL_TLS_CERTPATH", certificateDestinationPath + "/" + certFileName)
            .WithEnvironment("ZITADEL_TLS_KEYPATH", certificateDestinationPath + "/" + certKeyFileName);
    }

    /// <summary>
    /// Configures the external domain settings for the Zitadel instance using an endpoint name.
    /// This method retrieves the endpoint reference by name and configures external access settings.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with external domain settings.</param>
    /// <param name="endpointName">The name of the endpoint to use for external domain configuration.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithExternalDomain(this IResourceBuilder<ZitadelResource> builder, string endpointName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(endpointName);

        var endpoint = builder.GetEndpoint(endpointName);
        return builder.WithExternalDomain(endpoint);
    }

    /// <summary>
    /// Configures the external domain settings for the Zitadel instance using an endpoint reference.
    /// Sets up how Zitadel identifies itself to external clients and handles redirects and callbacks.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with external domain settings.</param>
    /// <param name="endpoint">The endpoint reference containing host, port, and scheme information for external access.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithExternalDomain(this IResourceBuilder<ZitadelResource> builder, EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);

        return builder.WithEnvironment("ZITADEL_EXTERNALSECURE", endpoint.Scheme == "https" ? "true" : "false")
            .WithEnvironment("ZITADEL_EXTERNALPORT", endpoint.Property(EndpointProperty.Port))
            .WithEnvironment("ZITADEL_EXTERNALDOMAIN", endpoint.Property(EndpointProperty.Host));
    }

    /// <summary>
    /// Configures the external domain settings for the Zitadel instance with explicit parameters.
    /// Allows direct specification of security, host, and port settings for external access configuration.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with external domain settings.</param>
    /// <param name="isSecure">Whether the external domain uses HTTPS (true) or HTTP (false).</param>
    /// <param name="host">The external domain host name or IP address that clients will use to access Zitadel.</param>
    /// <param name="port">The port number that clients will use to access Zitadel externally.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithExternalDomain(this IResourceBuilder<ZitadelResource> builder, bool isSecure, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(host);

        return builder.WithEnvironment("ZITADEL_EXTERNALSECURE", isSecure.ToString)
            .WithEnvironment("ZITADEL_EXTERNALPORT", port.ToString())
            .WithEnvironment("ZITADEL_EXTERNALDOMAIN", host);
    }

    /// <summary>
    /// Configures a login client with personal access token for custom login UI integration.
    /// Creates the necessary configuration for implementing a custom login interface that integrates with Zitadel's authentication flows.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with login client settings.</param>
    /// <param name="loginUser">The login client user name for the service account. Default is "login-client".</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithLoginClientKey(this IResourceBuilder<ZitadelResource> builder, string loginUser = "login-client")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(loginUser);

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
    /// Adds a Zitadel login client container for custom login UI functionality.
    /// Deploys a Next.js-based login interface that provides a customizable authentication experience for end users.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to add the login client to.</param>
    /// <param name="name">The name of the login client resource for service discovery and configuration.</param>
    /// <param name="port">Optional host port for the login client. If not specified, a random port will be assigned.</param>
    /// <param name="loginUser">The login user name for authentication with the Zitadel API. Default is "login-client".</param>
    /// <param name="serviceAccessToken">Optional parameter resource for the service access token. If not provided, it will be read from the generated PAT file.</param>
    /// <returns>A resource builder for the Zitadel login client resource that can be used for further configuration.</returns>
    public static IResourceBuilder<ZitadelLoginClientResource> AddZitadelLoginClient(this IResourceBuilder<ZitadelResource> builder, string name, int? port = null,
        string loginUser = "login-client", IResourceBuilder<ParameterResource>? serviceAccessToken = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        
        string path = Path.GetFullPath($"./{builder.Resource.Name}-keys");

        ParameterResource serviceAccessTokenParameter = serviceAccessToken?.Resource ??
                                                        new ParameterResource(name, @default => File.ReadAllText(Path.Combine(path, $"{loginUser}.pat")), true);

        ZitadelLoginClientResource loginClientResource = new(name, serviceAccessTokenParameter);

        IResourceBuilder<ZitadelLoginClientResource> loginBuilder = builder.ApplicationBuilder.AddResource(loginClientResource)
            .WithImage(ZitadelContainerImageTags.LoginImage)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.LoginTag)
            .WithHttpEndpoint(port, DefaultContainerPortLoginClient)
            .WithEnvironment("ZITADEL_API_URL", builder.Resource.PrimaryEndpoint)
            .WithEnvironment("NEXT_PUBLIC_BASE_PATH", ZitadelLoginClientResource.BasePath)
            .WithEnvironment("ZITADEL_SERVICE_USER_TOKEN", serviceAccessTokenParameter)
            .WithEnvironment("NODE_TLS_REJECT_UNAUTHORIZED", "0")
            .WithEnvironment("CUSTOM_REQUEST_HEADERS", "Host:localhost")
            .WithHttpHealthCheck(ZitadelLoginClientResource.BasePath + "/healthy", 200)
            .WaitFor(builder);

        builder.WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "true")
            .WithEnvironment(async context =>
            {
                // workaround to resolve external address of login client
                if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] =
                        await loginBuilder.Resource.BaseEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = await loginBuilder.Resource.OidcLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = await loginBuilder.Resource.OidcLogoutEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = await loginBuilder.Resource.SamlLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                }
                else
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = loginBuilder.Resource.BaseEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = loginBuilder.Resource.OidcLoginEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = loginBuilder.Resource.OidcLogoutEndpoint;
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = loginBuilder.Resource.SamlLoginEndpoint;
                }
            });

        return loginBuilder;
    }

    /// <summary>
    /// Configures a specific host port for the Zitadel resource instead of using a randomly assigned port.
    /// This is useful for development scenarios where you need predictable port assignments.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with a specific port.</param>
    /// <param name="port">The port to bind on the host. If null, a random port will be assigned.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithHostPort(this IResourceBuilder<ZitadelResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(builder.Resource.PrimaryEndpoint.EndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Configures a specific host port for the Zitadel login client resource instead of using a randomly assigned port.
    /// This is useful for development scenarios where you need predictable port assignments for the login UI.
    /// </summary>
    /// <param name="builder">The Zitadel login client resource builder to configure with a specific port.</param>
    /// <param name="port">The port to bind on the host. If null, a random port will be assigned.</param>
    /// <returns>The Zitadel login client resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelLoginClientResource> WithHostPort(this IResourceBuilder<ZitadelLoginClientResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(ZitadelLoginClientResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Configures an initialization function that executes when the Zitadel resource becomes ready.
    /// This allows for custom setup logic such as creating organizations, projects, or configuring initial settings.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with initialization logic.</param>
    /// <param name="initialization">A function that takes Zitadel API client options and the resource, and performs initialization tasks.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithInitialization(this IResourceBuilder<ZitadelResource> builder,
        Func<Clients.Options, IResource, Task> initialization)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(initialization);
        return builder.WithAnnotation(new ZitadelInitializationAnnotation(initialization));
    }

    /// <summary>
    /// Configures an initialization function that executes when the Zitadel project resource becomes ready.
    /// This allows for custom project setup logic such as creating applications, roles, or configuring project-specific settings.
    /// </summary>
    /// <param name="builder">The Zitadel project resource builder to configure with initialization logic.</param>
    /// <param name="initialization">A function that takes Zitadel API client options and the project resource, and performs initialization tasks.</param>
    /// <returns>The Zitadel project resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelProjectResource> WithInitialization(this IResourceBuilder<ZitadelProjectResource> builder,
        Func<Clients.Options, IResource, Task> initialization)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(initialization);
        return builder.WithAnnotation(new ZitadelInitializationAnnotation(initialization));
    }

    /// <summary>
    /// Adds a Zitadel project resource for organizing applications and configurations within the Zitadel instance.
    /// Projects in Zitadel are containers for applications, roles, and other identity management resources.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to add the project to.</param>
    /// <param name="name">The resource name for service discovery and configuration.</param>
    /// <param name="projectName">The display name of the project within Zitadel.</param>
    /// <returns>A resource builder for the Zitadel project resource that can be used for further configuration.</returns>
    public static IResourceBuilder<ZitadelProjectResource> AddProject(this IResourceBuilder<ZitadelResource> builder, string name, string projectName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return builder.AddProject(name, new AddProjectRequest { Name = projectName });
    }

    /// <summary>
    /// Adds a Zitadel project resource using a detailed project creation request.
    /// This overload allows for more advanced project configuration options through the AddProjectRequest parameter.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to add the project to.</param>
    /// <param name="name">The resource name for service discovery and configuration.</param>
    /// <param name="projectRequest">A detailed project creation request containing all project configuration options.</param>
    /// <returns>A resource builder for the Zitadel project resource that can be used for further configuration.</returns>
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
    /// Sets the organization name for the Zitadel instance.
    /// The organization is the top-level container for all identity and access management resources in Zitadel.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with the organization name.</param>
    /// <param name="organizationName">The name of the organization to create or use in the Zitadel instance.</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithOrganizationName(this IResourceBuilder<ZitadelResource> builder, string organizationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(organizationName);
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