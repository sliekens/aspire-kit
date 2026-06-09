// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREATS001 // AspireExport is experimental
#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding GlitchTip resources to the application model.
/// </summary>
public static class GlitchTipBuilderExtensions
{
    private const int GlitchTipTargetPort = 8000;

    /// <summary>
    /// Adds a GlitchTip resource to the application model. A container is used for local development.
    /// The default image is <inheritdoc cref="GlitchTipContainerImageTags.Image"/> and the tag is <inheritdoc cref="GlitchTipContainerImageTags.Tag"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="adminEmail">The parameter used as the bootstrap admin email and provisioner credentials.</param>
    /// <param name="adminPassword">The parameter used as the bootstrap admin password and provisioner credentials.</param>
    /// <param name="secretKey">The parameter used as the <c>SECRET_KEY</c> environment variable. A random value is generated when not specified.</param>
    /// <param name="orgName">The parameter used as the organization name provisioned by the package. Defaults to the resource name.</param>
    /// <param name="projectName">The parameter used as the project name provisioned by the package. Defaults to the resource name.</param>
    /// <param name="fromEmail">The parameter used as the <c>DEFAULT_FROM_EMAIL</c> environment variable. Defaults to <c>info@{host}</c> where <c>{host}</c> is the container hostname.</param>
    /// <param name="port">The host port for the GlitchTip container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Call <see cref="WithPostgres"/> and <see cref="WithRedis"/> to configure the required database and cache backends.
    /// </para>
    /// <para>
    /// The provisioner automatically creates an organization, team, and project on first run and sets the Sentry-compatible DSN
    /// on the resource. Use <c>WaitFor(glitchtip)</c> on consuming services to ensure the DSN is available before they start.
    /// </para>
    /// <example>
    /// Add a GlitchTip container to the application model and reference it in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var postgres = builder.AddPostgres("postgres").AddDatabase("glitchtip-db");
    /// var redis = builder.AddRedis("redis");
    ///
    /// var glitchtip = builder.AddGlitchTip("glitchtip")
    ///     .WithPostgres(postgres)
    ///     .WithRedis(redis);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(glitchtip)
    ///     .WaitFor(glitchtip);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<GlitchTipResource> AddGlitchTip(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> adminEmail,
        IResourceBuilder<ParameterResource> adminPassword,
        IResourceBuilder<ParameterResource>? secretKey = null,
        IResourceBuilder<ParameterResource>? orgName = null,
        IResourceBuilder<ParameterResource>? projectName = null,
        IResourceBuilder<ParameterResource>? fromEmail = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(adminEmail);
        ArgumentNullException.ThrowIfNull(adminPassword);

        var adminEmailParam = adminEmail.Resource;
        var adminPasswordParam = adminPassword.Resource;

        var secretKeyParam = secretKey?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-secret-key");

        var orgNameParam = orgName?.Resource
            ?? builder.AddParameter($"{name}-org-name", name).Resource;

        var projectNameParam = projectName?.Resource
            ?? builder.AddParameter($"{name}-project-name", name).Resource;

        var resource = new GlitchTipResource(
            name, secretKeyParam, adminEmailParam, adminPasswordParam,
            orgNameParam, projectNameParam);

        var resourceBuilder = builder.AddResource(resource)
            .WithHttpEndpoint(port: port, targetPort: GlitchTipTargetPort, name: GlitchTipResource.PrimaryEndpointName)
            .WithImage(GlitchTipContainerImageTags.Image, GlitchTipContainerImageTags.Tag)
            .WithImageRegistry(GlitchTipContainerImageTags.Registry)
            .WithEnvironment("SECRET_KEY", secretKeyParam)
            .WithEnvironment("EMAIL_URL", "consolemail://")
            .WithEnvironment("ENABLE_ADMIN", "False")
            .WithEnvironment("ENABLE_OPENAPI", "False")
            .WithEnvironment("GLITCHTIP_ENABLE_MCP", "False")
            .WithEnvironment("GLITCHTIP_ENABLE_DUCKDB", "False")
            .WithEnvironment("SERVER_ROLE", "all_in_one")
            .WithEnvironment("GLITCHTIP_DOMAIN", resource.PrimaryEndpoint)
            .WithHttpHealthCheck("/api/0/");

        if (fromEmail is not null)
            resourceBuilder.WithEnvironment("DEFAULT_FROM_EMAIL", fromEmail.Resource);
        else
            resourceBuilder.WithEnvironment("DEFAULT_FROM_EMAIL", ReferenceExpression.Create(
                $"info@{resource.PrimaryEndpoint.Property(EndpointProperty.Host)}"));

        if (builder.ExecutionContext.IsRunMode)
        {
            resourceBuilder.WithContainerCertificatePaths(
                defaultCertificateDirectoryPaths: ["/etc/ssl/certs", "/usr/local/share/ca-certificates"]);
        }

        if (builder.ExecutionContext.IsPublishMode)
        {
            // Deployment state is loaded into configuration at AppHost startup, so these resolve to
            // the provisioned values on every deploy after the first. The empty fallback means they
            // never prompt (headless CI safe); until values exist, consumers receive an incomplete
            // DSN, which the CommunityToolkit.Aspire.GlitchTip client treats as "Sentry disabled".
            resource.DsnKeyParameter = builder.AddParameter($"{name}-dsn-key",
                () => builder.Configuration[$"Parameters:{name}-dsn-key"] ?? "", secret: true).Resource;
            resource.ProjectIdParameter = builder.AddParameter($"{name}-project-id",
                () => builder.Configuration[$"Parameters:{name}-project-id"] ?? "").Resource;
        }

        builder.Eventing.Subscribe<ResourceReadyEvent>(resource, async (@event, ct) =>
        {
            var logger = @event.Services.GetRequiredService<ILogger<GlitchTipResource>>();
            await GlitchTipProvisioner.ProvisionAsync(resource, logger, ct);
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Enables automatic provisioning during <c>aspire deploy</c>: a pipeline step runs after the
    /// deployment, reaches the published GlitchTip endpoint over HTTP from the deploy machine,
    /// ensures the organization, team, and project exist, and saves the resulting DSN values to
    /// deployment state so they flow into consuming services on the next deploy.
    /// </summary>
    /// <param name="builder">The GlitchTip resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The provisioning endpoint is resolved automatically for Docker Compose deployments by querying
    /// the published port of the GlitchTip service (the HTTP endpoint is marked external for this
    /// purpose). For other targets, or when the deploy machine is not the container host, set the
    /// <c>{name}-provision-url</c> parameter (e.g. <c>Parameters__glitchtip-provision-url=http://my-host:8080</c>).
    /// </para>
    /// <para>
    /// On the first deploy, consuming services start before the DSN exists and run with error
    /// reporting disabled; the values are applied on the next <c>aspire deploy</c>. This method has
    /// no effect in run mode, where provisioning happens automatically when the container is ready.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<GlitchTipResource> WithDeployTimeProvisioning(
        this IResourceBuilder<GlitchTipResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var appBuilder = builder.ApplicationBuilder;
        var resource = builder.Resource;
        var name = resource.Name;

        // The deploy machine provisions over HTTP, and operators need the UI; both require a
        // published (external) endpoint.
        builder.WithExternalHttpEndpoints();

        var provisionUrlParam = appBuilder.AddParameter($"{name}-provision-url",
            () => appBuilder.Configuration[$"Parameters:{name}-provision-url"] ?? "").Resource;

        var stepName = $"provision-glitchtip-{name}";
        builder.WithPipelineStepFactory(
            stepName,
            context => GlitchTipDeployStep.ExecuteAsync(context, resource, provisionUrlParam),
            dependsOn: [WellKnownPipelineSteps.DeployPrereq],
            requiredBy: [WellKnownPipelineSteps.Deploy]);

        // Order after the compute deploy step(s) when their names are known (Docker Compose).
        // For other targets the step's readiness polling provides the ordering.
        builder.WithPipelineConfiguration(context =>
        {
            var step = context.Steps.FirstOrDefault(s => s.Name == stepName);
            if (step is null)
            {
                return;
            }

            foreach (var environment in context.Model.Resources.OfType<IComputeEnvironmentResource>())
            {
                var composeUpStep = $"docker-compose-up-{environment.Name}";
                if (context.Steps.Any(s => s.Name == composeUpStep))
                {
                    step.DependsOn(composeUpStep);
                }
            }
        });

        return builder;
    }

    /// <summary>
    /// Configures GlitchTip to use the specified PostgreSQL database as its storage backend.
    /// </summary>
    /// <param name="builder">The GlitchTip resource builder.</param>
    /// <param name="database">The PostgreSQL database resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<GlitchTipResource> WithPostgres(
        this IResourceBuilder<GlitchTipResource> builder,
        IResourceBuilder<PostgresDatabaseResource> database)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);

        builder.WaitFor(database);
        builder.WithEnvironment("DATABASE_URL", ReferenceExpression.Create(
            $"postgres://{database.Resource.Parent.UserNameReference}:{database.Resource.Parent.PasswordParameter}@{database.Resource.Parent.PrimaryEndpoint.Property(EndpointProperty.Host)}:{database.Resource.Parent.PrimaryEndpoint.Property(EndpointProperty.Port)}/{database.Resource.DatabaseName}"));

        return builder;
    }

    /// <summary>
    /// Configures GlitchTip to use the specified Redis (or Valkey) instance as its cache and task queue backend.
    /// </summary>
    /// <param name="builder">The GlitchTip resource builder.</param>
    /// <param name="redis">The Redis resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<GlitchTipResource> WithRedis(
        this IResourceBuilder<GlitchTipResource> builder,
        IResourceBuilder<RedisResource> redis)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(redis);

        builder.WaitFor(redis);
        builder.WithEnvironment("VALKEY_URL", redis.Resource.UriExpression);

        return builder;
    }
}

#pragma warning restore ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning restore ASPIREATS001 // AspireExport is experimental
