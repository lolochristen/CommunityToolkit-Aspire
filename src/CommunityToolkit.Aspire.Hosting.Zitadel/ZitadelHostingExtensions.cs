using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Zitadel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Zitadel to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class ZitadelHostingExtensions
{
    /// <summary>
    /// Adds a Zitadel container resource to the <see cref="IDistributedApplicationBuilder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder" /> to add the Zitadel container to.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port used when launching the container. If <c>null</c> a random port will be assigned</param>
    /// <param name="username">An optional parameter to set a username for the admin account, if <c>null</c> will auto generate one.</param>
    /// <param name="password">An optional parameter to set a password for the admin account, if <c>null</c> will auto generate one.</param>
    /// <param name="masterKey">An optional parameter to set the masterkey, if <c>null</c> will auto generate one.</param>
    public static IResourceBuilder<ZitadelResource> AddZitadel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? username = null,
        IResourceBuilder<ParameterResource>? password = null,
        IResourceBuilder<ParameterResource>? masterKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var usernameParameter = username?.Resource ?? new ParameterResource($"{name}-username", _ => "admin", false);
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", minSpecial: 1);
        var masterKeyParameter = masterKey?.Resource ?? ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-masterKey", true, new GenerateParameterDefault
        {
            MinLength = 32, // Zitadel requires 32, CreateDefaultPasswordParameter generates 22
            Lower = true,
            Upper = true,
            Numeric = true,
            Special = true,
            MinLower = 1,
            MinUpper = 1,
            MinNumeric = 1,
            MinSpecial = 1
        });

        var resource = new ZitadelResource(name)
        {
            AdminUsernameParameter = usernameParameter,
            AdminPasswordParameter = passwordParameter
        };

        var zitadelBuilder = builder.AddResource(resource)
            .WithImage(ZitadelContainerImageTags.Image)
            .WithImageTag(ZitadelContainerImageTags.Tag)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithArgs("start-from-init", "--masterkeyFromEnv")
            .WithHttpEndpoint(
                targetPort: 8080,
                port: port,
                name: ZitadelResource.HttpEndpointName
            )
            .WithHttpHealthCheck("/healthz")
            .WithEnvironment("ZITADEL_MASTERKEY", masterKeyParameter)
            .WithUrlForEndpoint(ZitadelResource.HttpEndpointName, e => e.DisplayText = "Zitadel Dashboard");

#pragma warning disable ASPIRECERTIFICATES001
        zitadelBuilder.WithHttpsCertificateConfiguration(ctx =>
        {
            ctx.EnvironmentVariables["ZITADEL_EXTERNALSECURE"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_ENABLED"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_CERTPATH"] = ctx.CertificatePath;
            ctx.EnvironmentVariables["ZITADEL_TLS_KEYPATH"] = ctx.KeyPath;
            return Task.CompletedTask;
        });
#pragma warning restore ASPIRECERTIFICATES001

        if (builder.ExecutionContext.IsRunMode)
        {
#pragma warning disable ASPIRECERTIFICATES001
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            {
                var developerCertificateService = @event.Services.GetRequiredService<IDeveloperCertificateService>();

                bool addHttps = false;
                if (!zitadelBuilder.Resource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var annotation))
                {
                    if (developerCertificateService.UseForHttps)
                    {
                        // If no certificate is configured, and the developer certificate service supports container trust,
                        // configure the resource to use the developer certificate for its key pair.
                        addHttps = true;
                    }
                }
                else if (annotation.UseDeveloperCertificate.GetValueOrDefault(developerCertificateService.UseForHttps) || annotation.Certificate is not null)
                {
                    addHttps = true;
                }

                if (addHttps)
                {
                    // If a TLS certificate is configured, override the endpoint to use HTTPS instead of HTTP
                    // Zitadel only binds to a single port
                    zitadelBuilder
                        .WithEndpoint(ZitadelResource.HttpEndpointName, ep => ep.UriScheme = "https");
                }

                return Task.CompletedTask;
            });
#pragma warning restore ASPIRECERTIFICATES001
            zitadelBuilder.OnResourceReady(async (zitadelResource, @event, cancellationToken) =>
            {
                // store keys
#pragma warning disable ASPIREUSERSECRETS001
                if (zitadelResource.MachineServiceAccountKeyParameter != null && zitadelResource.MachineServiceAccountNameParameter != null)
                {
                    var serviceAccountName = await zitadelResource.MachineServiceAccountNameParameter.GetValueAsync(cancellationToken);
                    var keyPath = Path.Combine(GetLocalKeysPath(zitadelBuilder.Resource.Name), $"{serviceAccountName}.json");
                    if (File.Exists(keyPath))
                    {
                        var secretName = $"Parameters:{zitadelResource.MachineServiceAccountKeyParameter.Name}";
                        zitadelBuilder.ApplicationBuilder.UserSecretsManager.TrySetSecret(secretName, await File.ReadAllTextAsync(keyPath, cancellationToken));
                    }
                }

                if (zitadelResource.LoginServiceAccountTokenParameter != null && zitadelResource.LoginServiceAccountNameParameter != null)
                {
                    var serviceAccountName = await zitadelResource.LoginServiceAccountNameParameter.GetValueAsync(cancellationToken);
                    var keyPath = Path.Combine(GetLocalKeysPath(zitadelBuilder.Resource.Name), $"{serviceAccountName}.pat");
                    if (File.Exists(keyPath))
                    {
                        var secretName = $"Parameters:{zitadelResource.LoginServiceAccountTokenParameter.Name}";
                        var pat = await File.ReadAllTextAsync(keyPath, cancellationToken);
                        zitadelBuilder.ApplicationBuilder.UserSecretsManager.TrySetSecret(secretName, pat.Trim(' ', '\n'));
                    }
                }
#pragma warning restore ASPIREUSERSECRETS001
            });
        }

        return zitadelBuilder
            .WithEnvironment(ctx =>
            {
                var resource = ctx.Resource;
                if (resource.TryGetEndpoints(out var endpoints))
                {
                    var endpoint = endpoints.Single(e => e.Name == ZitadelResource.HttpEndpointName);
                    ctx.EnvironmentVariables.TryAdd("ZITADEL_EXTERNALDOMAIN", endpoint.TargetHost);
                    var port = endpoint.Port;
                    if (port is not null)
                    {
                        ctx.EnvironmentVariables.TryAdd("ZITADEL_EXTERNALPORT", port);
                    }
                }
            })
            // Disable Login V2 for simpler setup (no separate login container needed)
            .WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "false")
            // Configure admin user
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_USERNAME", usernameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORD", passwordParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORDCHANGEREQUIRED", "false");
    }

    /// <summary>
    /// Adds database support to the Zitadel resource.
    /// </summary>
    /// <param name="builder">The Zitadel resource to add database support to.</param>
    /// <param name="server">The Postgres server resource to use for the database.</param>
    /// <param name="databaseName">An optional name for the database Zitadel will use, if left empty will default to <c>"zitadel-db"</c>.</param>
    public static IResourceBuilder<ZitadelResource> WithDatabase(
        this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<PostgresServerResource> server,
        [ResourceName] string? databaseName = null
    )
    {
        databaseName = string.IsNullOrWhiteSpace(databaseName) ? "zitadel-db" : databaseName;
        var database = server.AddDatabase(databaseName);

        return WithDatabase(builder, database);
    }

    /// <summary>
    /// Adds database support to the Zitadel resource.
    /// </summary>
    /// <param name="builder">The Zitadel resource to add database support to.</param>
    /// <param name="database">The Postgres database resource to use for the database.</param>
    public static IResourceBuilder<ZitadelResource> WithDatabase(this IResourceBuilder<ZitadelResource> builder, IResourceBuilder<PostgresDatabaseResource> database)
    {
        ArgumentNullException.ThrowIfNull(database);

        builder
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_USERNAME", database.Resource.Parent.UserNameReference)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_PASSWORD", database.Resource.Parent.PasswordParameter)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME", database.Resource.Parent.UserNameReference)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD", database.Resource.Parent.PasswordParameter)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_HOST", database.Resource.Parent.Host)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_PORT", database.Resource.Parent.Port)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_DATABASE", database.Resource.DatabaseName)
            .WithReference(database)
            .WaitFor(database);

        return builder;
    }

    /// <summary>
    /// Configures the external domain for the Zitadel resource. This overrides the default domain set in <see cref="AddZitadel"/>.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder.</param>
    /// <param name="externalDomain">The external domain to use (e.g., "auth.example.com"). Cannot be null or empty.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="externalDomain"/> is null or whitespace.</exception>
    public static IResourceBuilder<ZitadelResource> WithExternalDomain(
        this IResourceBuilder<ZitadelResource> builder,
        string externalDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalDomain);

        return builder.WithEnvironment("ZITADEL_EXTERNALDOMAIN", externalDomain);
    }

    /// <summary>
    /// Configures the resource builder to use the specified organization name for the Zitadel instance.
    /// </summary>
    /// <param name="builder">The resource builder to configure.</param>
    /// <param name="orgName">The name of the organization to set. This value must not be null, empty, or consist only of white-space
    /// characters.</param>
    /// <returns>The resource builder instance with the organization name configured.</returns>
    public static IResourceBuilder<ZitadelResource> WithOrganizationName(
        this IResourceBuilder<ZitadelResource> builder,
        string orgName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgName);

        return builder.WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_NAME", orgName);
    }


    /// <summary>
    /// Configures the resource builder to use the specified instance name for the Zitadel resource.
    /// </summary>
    /// <param name="builder">The resource builder to configure with the instance name.</param>
    /// <param name="instance">The name of the instance to associate with the resource. This value cannot be null or whitespace.</param>
    /// <returns>The updated resource builder configured with the specified instance name.</returns>
    public static IResourceBuilder<ZitadelResource> WithInstanceName(
        this IResourceBuilder<ZitadelResource> builder,
        string instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);

        return builder.WithEnvironment("ZITADEL_FIRSTINSTANCE_INSTANCENAME", instance);
    }

    /// <summary>
    /// Configures the resource builder to set the default language for the Zitadel instance.
    /// </summary>
    /// <param name="builder">The resource builder to configure with the default language setting.</param>
    /// <param name="languageCode">The language code to set as the default language. This value must not be null or consist only of white-space
    /// characters.</param>
    /// <returns>The resource builder instance configured with the specified default language.</returns>
    public static IResourceBuilder<ZitadelResource> WithDefaultLanguage(
        this IResourceBuilder<ZitadelResource> builder,
        string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        return builder.WithEnvironment("ZITADEL_FIRSTINSTANCE_DEFAULTLANGUAGE", languageCode);
    }


    /// <summary>
    /// Configures a service account user for programmatic access to the Zitadel instance with API key authentication.
    /// Creates a service account that can be used for server-to-server communication and API automation.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with machine user settings.</param>
    /// <param name="serviceAccountName">The user name for the service account. Default is "admin".</param>
    /// <param name="serviceAccountKey">The parameter to receive/store the created key</param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithMachineServiceAccount(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<ParameterResource>? serviceAccountName = null,
        IResourceBuilder<ParameterResource>? serviceAccountKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string path = GetLocalKeysPath(builder.Resource.Name);
        var serviceAccountNameParameter = serviceAccountName?.Resource ?? 
                                          new ParameterResource($"{builder.Resource.Name}-service-account", _ => "admin", false);

        var keyParamName = $"{builder.Resource.Name}-service-account-key";
        var serviceAccountKeyParameter = serviceAccountKey?.Resource ??
                                         new ParameterResource(keyParamName,
                                             _ =>
                                             {
                                                 var value = builder.ApplicationBuilder.Configuration[$"Parameters:{keyParamName}"];

                                                 if (!string.IsNullOrWhiteSpace(value)) return value;

                                                 var filePath = Path.Combine(path, $"{serviceAccountNameParameter.GetValueAsync(CancellationToken.None).Result}.json");
                                                 if (File.Exists(filePath))
                                                 {
                                                     return File.ReadAllText(filePath);
                                                 }

                                                 return string.Empty;
                                             }, true);

        builder.Resource.MachineServiceAccountNameParameter = serviceAccountNameParameter;
        builder.Resource.MachineServiceAccountKeyParameter = serviceAccountKeyParameter;

        builder
            .WithKeysMount()
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_MACHINEKEYPATH", $"/keys/{serviceAccountNameParameter}.json")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME", serviceAccountNameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME", "Automatically Initialized IAM_OWNER")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINEKEY_TYPE", "1");

        return builder;
    }

    /// <summary>
    /// Mounts the keys directory of Zitadel to a local directory to receive created keys and pats.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure.</param>
    /// <param name="source">Source directory to mount</param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithKeysMount(this IResourceBuilder<ZitadelResource> builder, string? source = null)
    {
        source ??= GetLocalKeysPath(builder.Resource.Name);

        builder.Resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out IEnumerable<ContainerMountAnnotation>? mountAnnotations);
        if (mountAnnotations == null || mountAnnotations.All(m => m.Target != "/keys"))
        {
            builder.WithBindMount(source, "/keys");
        }

        return builder;
    }

    /// <summary>
    /// Configures a login client with personal access token for custom login UI integration.
    /// Creates the necessary configuration for implementing a custom login interface that integrates with Zitadel's authentication flows.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with login client settings.</param>
    /// <param name="serviceAccountToken"></param>
    /// <param name="expirationDate">Optional expiration date. Default today + 5y</param>
    /// <param name="serviceAccountName"></param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithLoginServiceAccount(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<ParameterResource>? serviceAccountName = null,
        IResourceBuilder<ParameterResource>? serviceAccountToken = null,
        DateTimeOffset? expirationDate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string path = GetLocalKeysPath(builder.Resource.Name);

        var serviceAccountNameParameter = serviceAccountName?.Resource ??
                                          new ParameterResource($"{builder.Resource.Name}-login-client", _ => "login-client", false);

        var tokenParamName = $"{builder.Resource.Name}-login-client-token";
        var serviceAccountTokenParameter = serviceAccountToken?.Resource ??
                                         new ParameterResource(tokenParamName,
                                             _ =>
                                             {
                                                 var value = builder.ApplicationBuilder.Configuration[$"Parameters:{tokenParamName}"];

                                                 if (!string.IsNullOrWhiteSpace(value)) return value;

                                                 var filePath = Path.Combine(path, $"{serviceAccountNameParameter.GetValueAsync(CancellationToken.None).Result}.pat");
                                                 if (File.Exists(filePath))
                                                 {
                                                     return File.ReadAllText(filePath);
                                                 }

                                                 return string.Empty;
                                             }, true);

        builder.Resource.LoginServiceAccountNameParameter = serviceAccountNameParameter;
        builder.Resource.LoginServiceAccountTokenParameter = serviceAccountTokenParameter;

        builder
            .WithKeysMount()
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_LOGINCLIENTPATPATH", $"/keys/{serviceAccountNameParameter}.pat")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_USERNAME", serviceAccountNameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_NAME", "Automatically Initialized IAM_LOGIN_CLIENT")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_PAT_EXPIRATIONDATE", (expirationDate ?? DateTime.UtcNow.AddYears(5)).ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return builder;
    }

    /// <summary>
    /// Adds a Zitadel login client container for custom login UI functionality.
    /// Deploys a Next.js-based login interface that provides a customizable authentication experience for end users.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to add the login client to.</param>
    /// <param name="name">The name of the login client resource for service discovery and configuration.</param>
    /// <param name="port">Optional host port for the login client. If not specified, a random port will be assigned.</param>
    /// <param name="serviceAccountToken">Optional parameter resource for the service access token. If not provided, it will be read from the generated PAT file.</param>
    /// <returns>A resource builder for the Zitadel login client resource that can be used for further configuration.</returns>
    public static IResourceBuilder<ZitadelLoginClientResource> AddZitadelLoginClient(this IResourceBuilder<ZitadelResource> builder, 
        string name, 
        int? port = null,
        IResourceBuilder<ParameterResource>? serviceAccountToken = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var accessTokenParameter = serviceAccountToken?.Resource ?? builder.Resource.LoginServiceAccountTokenParameter ?? throw new ArgumentException("access token missing");

        var loginClientResource = new ZitadelLoginClientResource(name, accessTokenParameter);

        var loginClientBuilder = builder.ApplicationBuilder.AddResource(loginClientResource)
            .WithImage(ZitadelContainerImageTags.LoginImage)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.LoginTag)
            .WithHttpEndpoint(port, 3000)
            .WithEnvironment("ZITADEL_API_URL", builder.Resource.GetEndpoint(ZitadelResource.HttpEndpointName))
            .WithEnvironment("NEXT_PUBLIC_BASE_PATH", ZitadelLoginClientResource.BasePath)
            .WithEnvironment("ZITADEL_SERVICE_USER_TOKEN", accessTokenParameter)
            .WithEnvironment("NODE_TLS_REJECT_UNAUTHORIZED", "0")
            .WithEnvironment("CUSTOM_REQUEST_HEADERS", "Host:localhost")
            .WithEnvironment("HOSTNAME", "0.0.0.0")
            .WithOtlpExporter()
            .WithHttpHealthCheck(ZitadelLoginClientResource.BasePath + "/healthy", 200)
            .WaitFor(builder);

        builder.WithLoginClient(loginClientBuilder);

#pragma warning disable ASPIRECERTIFICATES001
        loginClientBuilder.WithHttpsCertificateConfiguration(ctx =>
        {
            ctx.EnvironmentVariables["ZITADEL_TLS_ENABLED"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_CERTPATH"] = ctx.CertificatePath;
            ctx.EnvironmentVariables["ZITADEL_TLS_KEYPATH"] = ctx.KeyPath;
            return Task.CompletedTask;
        });

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            {
                var developerCertificateService = @event.Services.GetRequiredService<IDeveloperCertificateService>();

                bool addHttps = false;
                if (!loginClientBuilder.Resource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var annotation))
                {
                    if (developerCertificateService.UseForHttps)
                    {
                        // If no certificate is configured, and the developer certificate service supports container trust,
                        // configure the resource to use the developer certificate for its key pair.
                        addHttps = true;
                    }
                }
                else if (annotation.UseDeveloperCertificate.GetValueOrDefault(developerCertificateService.UseForHttps) || annotation.Certificate is not null)
                {
                    addHttps = true;
                }

                if (addHttps)
                {
                    // If a TLS certificate is configured, override the endpoint to use HTTPS instead of HTTP
                    // Zitadel only binds to a single port
                    loginClientBuilder
                        .WithEndpoint(ZitadelLoginClientResource.HttpEndpointName, ep => ep.UriScheme = "https");
                }

                return Task.CompletedTask;
            });
#pragma warning restore ASPIRECERTIFICATES001
        }

        return loginClientBuilder;
    }

    /// <summary>
    /// Configures Zitadel to use given Zitadel Login Client.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder.</param>
    /// <param name="loginClientBuilder">The Login Client resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithLoginClient(
        this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<ZitadelLoginClientResource> loginClientBuilder)
    {
        return builder.WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "true")
            .WithEnvironment(async context =>
            {
                if (builder.ApplicationBuilder.ExecutionContext.IsRunMode) 
                {
                    // workaround to resolve external address of login client
                    var baseUri = await loginClientBuilder.Resource.BaseEndpoint.GetValueAsync(context.CancellationToken);
                    if (string.IsNullOrWhiteSpace(baseUri))
                        context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = loginClientBuilder.Resource.BaseEndpoint;
                    else
                        context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = baseUri;
                }
                else
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = loginClientBuilder.Resource.BaseEndpoint;
                }
            });
    }

    /// <summary>
    /// Configures the Base Uri of the Zitadel Login Client.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder.</param>
    /// <param name="loginClientBaseUri">Base Uri of Login Client</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="loginClientBaseUri"/> is null or whitespace.</exception>
    public static IResourceBuilder<ZitadelResource> WithLoginClient(
        this IResourceBuilder<ZitadelResource> builder,
        string loginClientBaseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginClientBaseUri);

        return builder.WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "true")
            .WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI", loginClientBaseUri);
    }

    private static string GetLocalKeysPath(string name, bool ensureExists = true)
    {
        var path = Path.GetFullPath($"./.zitadel/{name}-keys");
        if (ensureExists && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }
}