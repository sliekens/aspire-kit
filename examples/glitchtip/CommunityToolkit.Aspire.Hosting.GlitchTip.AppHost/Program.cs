var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("glitchtip-db");

var redis = builder.AddRedis("redis");

var adminEmail = builder.AddParameter("glitchtip-admin-email");
var adminPassword = builder.AddParameter("glitchtip-admin-password", secret: true);

var glitchtip = builder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
    .WithPostgres(postgres)
    .WithRedis(redis)
    .WithExternalHttpEndpoints()
    .WithDeployTimeProvisioning();

builder.Build().Run();
