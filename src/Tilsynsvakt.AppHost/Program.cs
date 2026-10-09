var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("aca");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume());
var tables = storage.AddTables("tables");

var api = builder.AddProject<Projects.Tilsynsvakt_Api>("api")
    .WithReference(tables)
    .WaitFor(tables)
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health");

// Secret parameters publish as ACA secrets referenced via secretRef, not plain env values.
foreach (var (configKey, envName, parameterName) in new[]
{
    ("Admin:ApiKey", "Admin__ApiKey", "admin-api-key"),
    ("Admin:Username", "Admin__Username", "admin-username"),
    ("Admin:Password", "Admin__Password", "admin-password"),
})
{
    if (!string.IsNullOrWhiteSpace(builder.Configuration[configKey]))
    {
        api.WithEnvironment(envName, builder.AddParameterFromConfiguration(parameterName, configKey, secret: true));
    }
}

if (builder.ExecutionContext.IsPublishMode)
{
    // Aspire grants the API's managed identity Table Data Contributor, which is enough to create the table.
    api.WithEnvironment("Storage__CreateTable", "true")
       .WithExternalHttpEndpoints()
       .PublishAsAzureContainerApp((_, app) =>
       {
           var container = app.Template.Containers[0].Value!;
           container.Resources.Cpu = 0.25;
           container.Resources.Memory = "0.5Gi";
       });

    var frontendOrigin = builder.Configuration["Frontend:Origin"];
    if (!string.IsNullOrWhiteSpace(frontendOrigin))
    {
        api.WithEnvironment("Frontend__Origin", frontendOrigin);
    }
}

builder.Build().Run();
