var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword      = builder.AddParameter("sql-password",      secret: true);
var openRouterApiKey = builder.AddParameter("openrouter-apikey", secret: true);

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1433)
    .WithImageTag("2025-latest")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("VectorDb", "ragdb");

var apiService = builder.AddProject<Projects.RAG_API_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(sql)
    .WaitFor(sql)
    .WithEnvironment("OpenRouter__ApiKey", openRouterApiKey);

builder.AddProject<Projects.RAG_API_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
