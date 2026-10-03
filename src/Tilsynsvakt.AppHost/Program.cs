var builder = DistributedApplication.CreateBuilder(args);

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

var adminApiKey = builder.Configuration["Admin:ApiKey"];
if (!string.IsNullOrWhiteSpace(adminApiKey))
{
    api.WithEnvironment("Admin__ApiKey", adminApiKey);
}

builder.Build().Run();
