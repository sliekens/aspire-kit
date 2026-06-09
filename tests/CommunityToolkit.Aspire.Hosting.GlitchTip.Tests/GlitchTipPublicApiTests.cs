// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.Utils;

namespace CommunityToolkit.Aspire.Hosting.GlitchTip.Tests;

public class GlitchTipPublicApiTests
{
    [Fact]
    public void AddGlitchTipShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddGlitchTip("glitchtip", null!, null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddGlitchTipShouldThrowWhenNameIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        string name = null!;

        var action = () => builder.AddGlitchTip(name, null!, null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddGlitchTipShouldThrowWhenNameIsEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddGlitchTip(string.Empty, null!, null!);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void AddGlitchTipShouldThrowWhenAdminEmailIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var adminPassword = builder.AddParameter("admin-password", secret: true);

        var action = () => builder.AddGlitchTip("glitchtip", null!, adminPassword);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("adminEmail", exception.ParamName);
    }

    [Fact]
    public void AddGlitchTipShouldThrowWhenAdminPasswordIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var adminEmail = builder.AddParameter("admin-email");

        var action = () => builder.AddGlitchTip("glitchtip", adminEmail, null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("adminPassword", exception.ParamName);
    }

    [Fact]
    public void WithPostgresShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GlitchTipResource> builder = null!;
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var db = appBuilder.AddPostgres("pg").AddDatabase("db");

        var action = () => builder.WithPostgres(db);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPostgresShouldThrowWhenDatabaseIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);
        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);
        IResourceBuilder<PostgresDatabaseResource> database = null!;

        var action = () => glitchtip.WithPostgres(database);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(database), exception.ParamName);
    }

    [Fact]
    public void WithRedisShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GlitchTipResource> builder = null!;
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var redis = appBuilder.AddRedis("redis");

        var action = () => builder.WithRedis(redis);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDeployTimeProvisioningShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GlitchTipResource> builder = null!;

        var action = () => builder.WithDeployTimeProvisioning();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithRedisShouldThrowWhenRedisIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var (adminEmail, adminPassword) = CreateTestAdminParams(appBuilder);
        var glitchtip = appBuilder.AddGlitchTip("glitchtip", adminEmail, adminPassword);
        IResourceBuilder<RedisResource> redis = null!;

        var action = () => glitchtip.WithRedis(redis);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(redis), exception.ParamName);
    }

    private static (IResourceBuilder<ParameterResource> adminEmail, IResourceBuilder<ParameterResource> adminPassword) CreateTestAdminParams(IDistributedApplicationBuilder builder)
    {
        var email = builder.AddParameter("admin-email");
        var password = builder.AddParameter("admin-password", secret: true);
        return (email, password);
    }
}
