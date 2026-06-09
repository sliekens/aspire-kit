// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.Utils;
using CommunityToolkit.Aspire.Testing;
using System.Net.Sockets;

namespace CommunityToolkit.Aspire.Hosting.GlitchTip.Tests;

public class AddGlitchTipTests
{
    [Fact]
    public void AddGlitchTipAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        Assert.Equal(
            "Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault",
            glitchtip.Resource.SecretKey.Default?.GetType().FullName);
    }

    [Fact]
    public void AddGlitchTipDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        Assert.NotEqual(
            "Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault",
            glitchtip.Resource.SecretKey.Default?.GetType().FullName);
    }

    [Fact]
    public void AddGlitchTipContainerWithDefaultsAddsAnnotationMetadata()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        var containerResource = Assert.Single(appBuilder.Resources.OfType<GlitchTipResource>());
        Assert.Equal("glitchtip", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8000, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(GlitchTipContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(GlitchTipContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(GlitchTipContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task AddGlitchTipSetsStaticEnvironmentVariables()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);
        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)));

        Assert.True(glitchtip.Resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var annotations));
        foreach (var annotation in annotations)
            await annotation.Callback(context);

        var env = context.EnvironmentVariables;

        Assert.Contains("EMAIL_URL", env.Keys);
        Assert.Contains("ENABLE_ADMIN", env.Keys);
        Assert.Contains("ENABLE_OPENAPI", env.Keys);
        Assert.Contains("SERVER_ROLE", env.Keys);
        Assert.Contains("GLITCHTIP_ENABLE_MCP", env.Keys);
        Assert.Contains("GLITCHTIP_ENABLE_DUCKDB", env.Keys);
    }

    [Fact]
    public async Task WithPostgresAddsDatabaseUrlEnvironmentVariable()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);
        var postgres = appBuilder.AddPostgres("postgres").AddDatabase("glitchtip-db");
        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword).WithPostgres(postgres);

        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)));

        Assert.True(glitchtip.Resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var annotations));
        foreach (var annotation in annotations)
            await annotation.Callback(context);

        Assert.Contains("DATABASE_URL", context.EnvironmentVariables.Keys);
        var dbUrl = (ReferenceExpression)context.EnvironmentVariables["DATABASE_URL"];
        Assert.StartsWith("postgres://", dbUrl.Format);
        Assert.EndsWith("/glitchtip-db", dbUrl.Format);
    }

    [Fact]
    public async Task WithRedisAddsValkeyUrlEnvironmentVariable()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);
        var redis = appBuilder.AddRedis("redis");
        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword).WithRedis(redis);

        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)));

        Assert.True(glitchtip.Resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var annotations));
        foreach (var annotation in annotations)
            await annotation.Callback(context);

        Assert.Contains("VALKEY_URL", context.EnvironmentVariables.Keys);
    }

    [Fact]
    public void AddGlitchTipInPublishModeBuildsConnectionStringFromDsnParameters()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        var dsnKey = Assert.Single(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-dsn-key");
        Assert.True(dsnKey.Secret);
        Assert.Single(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-project-id");

        var expression = glitchtip.Resource.ConnectionStringExpression.ValueExpression;
        Assert.Contains("{glitchtip-dsn-key.value}", expression);
        Assert.Contains("{glitchtip-project-id.value}", expression);
    }

    [Fact]
    public void AddGlitchTipInRunModeDoesNotAddDsnParameters()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);

        Assert.DoesNotContain(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-dsn-key");
        Assert.DoesNotContain(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-project-id");
        Assert.DoesNotContain("{glitchtip-dsn-key.value}", glitchtip.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void WithDeployTimeProvisioningInPublishModeAddsStepAndProvisionUrlParameter()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
            .WithDeployTimeProvisioning();

        Assert.Single(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-provision-url");

        var endpoint = Assert.Single(glitchtip.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.True(endpoint.IsExternal);

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
        Assert.Single(glitchtip.Resource.Annotations.OfType<global::Aspire.Hosting.Pipelines.PipelineStepAnnotation>());
#pragma warning restore ASPIREPIPELINES001
    }

    [Fact]
    public void WithDeployTimeProvisioningInRunModeIsNoOp()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
            .WithDeployTimeProvisioning();

        Assert.DoesNotContain(appBuilder.Resources.OfType<ParameterResource>(), p => p.Name == "glitchtip-provision-url");

        var endpoint = Assert.Single(glitchtip.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.False(endpoint.IsExternal);

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
        Assert.Empty(glitchtip.Resource.Annotations.OfType<global::Aspire.Hosting.Pipelines.PipelineStepAnnotation>());
#pragma warning restore ASPIREPIPELINES001
    }

    [Fact]
    public void AddGlitchTipUsesDefaultOrgAndProjectNameFromResourceName()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);

        var glitchtip = appBuilder.AddGlitchTip("myapp", adminEmail, adminPassword);

        Assert.Equal("myapp-org-name", glitchtip.Resource.OrgName.Name);
        Assert.Equal("myapp-project-name", glitchtip.Resource.ProjectName.Name);
    }

    private static (IResourceBuilder<ParameterResource> adminEmail, IResourceBuilder<ParameterResource> adminPassword) CreateTestAdminParams(IDistributedApplicationBuilder builder)
    {
        var email = builder.AddParameter("admin-email");
        var password = builder.AddParameter("admin-password", secret: true);
        return (email, password);
    }
}
