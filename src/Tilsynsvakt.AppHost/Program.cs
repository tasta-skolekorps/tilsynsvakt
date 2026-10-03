var builder = DistributedApplication.CreateBuilder(args);

var tables = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .AddTables("tables");

builder.AddProject<Projects.Tilsynsvakt_Api>("api")
    .WithReference(tables)
    .WaitFor(tables)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
