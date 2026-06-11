using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .AddDatabase("glitchtip-db");

var redis = builder.AddRedis("redis");

var adminEmail = builder.AddParameter("glitchtip-admin-email");
var adminPassword = builder.AddParameter("glitchtip-admin-password", secret: true);

var glitchtip = builder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
    .WithPostgres(postgres)
    .WithRedis(redis)
    .WithExternalHttpEndpoints()
    .WithDeployTimeProvisioning();

var apiService = builder.AddProject<CommunityToolkit_Aspire_Hosting_GlitchTip_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(glitchtip)
    .WaitFor(glitchtip);

var web = builder.AddProject<CommunityToolkit_Aspire_Hosting_GlitchTip_Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithReference(glitchtip)
    .WaitFor(glitchtip);

builder.Build().Run();
