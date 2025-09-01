# CommunityToolkit.Aspire.Hosting.Zitadel library

Provides extension methods and resource definitions for a .NET Aspire AppHost to configure a Zitadel Idendtity Provider project. 
Extensions allows to attach a generic postgres database (Container, Azure Postgres) and to initialize a Zitadel projet to consume directly in webapp / api projects.

https://zitadel.com/docs/guides/overview

## Getting Started

### Install the package

In your AppHost project, install the package using the following command:

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.Zitadel
```

### Example usage

Then, in the _Program.cs_ file of `AppHost`, define an Zitadel resource, then call `AddZitadel`:

```csharp
var zitadel = builder.AddZitadel("zitadel", useHttps: true)
    .WithDeveloperCertificate()
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
```

In case new login v2 shall be used, add the following. Without this, v1 login will be used.

```csharp
var zitadel_login = _zitadel.AddZitadelLoginClient("zitadel-login")
    .WithExternalHttpEndpoints();    
```

## Additional Information

https://learn.microsoft.com/dotnet/aspire/community-toolkit/

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire