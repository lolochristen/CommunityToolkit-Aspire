# CommunityToolkit.Aspire.Hosting.Zitadel library

The integration provides ...

## Getting Started

### Install the package

In your AppHost project, install the package using the following command:

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.Zitadel
```

### Example usage

Then, in the _Program.cs_ file of `AppHost`, define an Zitadel resource, then call `AddZitadel`:

```csharp
var zitadel = builder.AddZitadel("zitadel")
    .WithHttpsEndpointUsingDevCertificate(8443)
    .WithExternalHttpEndpoints()
    .WithPostgresDatabase(database)
    .WithOrganizationName("ASPIRE")
    .WithMachineUser()
   
```

## Additional Information

https://learn.microsoft.com/dotnet/aspire/community-toolkit/

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire