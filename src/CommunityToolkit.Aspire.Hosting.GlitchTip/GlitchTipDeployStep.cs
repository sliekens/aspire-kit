// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // IDeploymentStateManager is experimental
#pragma warning disable ASPIRECONTAINERRUNTIME001 // IContainerRuntime APIs are experimental

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Pipeline step that provisions GlitchTip from the deploy machine during <c>aspire deploy</c>:
/// waits for the deployed instance to become reachable, ensures org/team/project exist over HTTP,
/// and saves the DSN values to deployment state under <c>Parameters:{name}-dsn-key</c> /
/// <c>Parameters:{name}-project-id</c> so they resolve into consuming services on the next deploy.
/// </summary>
internal static class GlitchTipDeployStep
{
    private static readonly TimeSpan s_readinessTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_readinessPollInterval = TimeSpan.FromSeconds(5);

    internal static async Task ExecuteAsync(
        PipelineStepContext context,
        GlitchTipResource resource,
        ParameterResource provisionUrl)
    {
        var ct = context.CancellationToken;

        var email = await resource.AdminEmail.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': admin email parameter is not set.");

        var password = await resource.AdminPassword.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': admin password parameter is not set.");

        var orgName = await resource.OrgName.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': org name parameter is not set.");

        var projectName = await resource.ProjectName.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': project name parameter is not set.");

        var baseUrl = await ResolveProvisionUrlAsync(context, resource, provisionUrl).ConfigureAwait(false);

        await WaitForReadyAsync(baseUrl, resource.Name, context).ConfigureAwait(false);

        var (dsnKey, projectId) = await GlitchTipProvisioner.ProvisionAsync(
            baseUrl, email, password, orgName, projectName, resource.Name, context.Logger, ct).ConfigureAwait(false);

        var stateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var changed = await SaveIfChangedAsync(stateManager, $"Parameters:{resource.Name}-dsn-key", dsnKey, ct).ConfigureAwait(false);
        changed |= await SaveIfChangedAsync(stateManager, $"Parameters:{resource.Name}-project-id", projectId, ct).ConfigureAwait(false);

        if (changed)
        {
            context.Logger.LogInformation(
                "GlitchTip '{Name}': DSN provisioned and saved to deployment state. " +
                "Run 'aspire deploy' again (or restart consuming services) to apply it.",
                resource.Name);
            context.Summary.Add(
                $"GlitchTip ({resource.Name})",
                $"Provisioned project '{projectName}' in org '{orgName}'. Deploy again to inject the DSN into consuming services.");
        }
        else
        {
            context.Summary.Add(
                $"GlitchTip ({resource.Name})",
                $"Project '{projectName}' already provisioned; DSN unchanged.");
        }
    }

    /// <summary>
    /// Resolves the URL the deploy machine uses to reach GlitchTip. Precedence: the
    /// <c>{name}-provision-url</c> parameter, then (for Docker Compose deployments) the published
    /// host port of the GlitchTip service queried from the running compose project.
    /// </summary>
    private static async Task<string> ResolveProvisionUrlAsync(
        PipelineStepContext context,
        GlitchTipResource resource,
        ParameterResource provisionUrl)
    {
        var ct = context.CancellationToken;

        var overrideUrl = await provisionUrl.GetValueAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.TrimEnd('/');
        }

        var environment = resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment
            ?? throw NoProvisionUrl(resource, "the resource is not bound to a compute environment");

        // The Docker Compose up step persists the compose project identity to deployment state.
        var stateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var composeState = await stateManager.AcquireSectionAsync($"DockerCompose:{environment.Name}", ct).ConfigureAwait(false);
        var projectName = composeState.Data["ProjectName"]?.ToString();
        var outputPath = composeState.Data["OutputPath"]?.ToString();

        if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(outputPath))
        {
            throw NoProvisionUrl(resource,
                $"no Docker Compose deployment state was found for environment '{environment.Name}', " +
                "and automatic endpoint resolution is only supported for Docker Compose");
        }

        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(ct).ConfigureAwait(false);
        var services = await runtime.ComposeListServicesAsync(
            new ComposeOperationContext { ProjectName = projectName, WorkingDirectory = outputPath },
            ct).ConfigureAwait(false);

        var targetPort = resource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == GlitchTipResource.PrimaryEndpointName)?.TargetPort;

        var publishedPort = services
            ?.Where(s => string.Equals(s.Service, resource.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Publishers ?? [])
            .FirstOrDefault(p => p.PublishedPort > 0 && (targetPort is null || p.TargetPort == targetPort))
            ?.PublishedPort
            ?? throw NoProvisionUrl(resource,
                $"no published host port was found for service '{resource.Name}' in compose project '{projectName}'");

        // The compose deploy runs against the local container runtime, so the published port is on this host.
        return $"http://localhost:{publishedPort}";
    }

    private static DistributedApplicationException NoProvisionUrl(GlitchTipResource resource, string reason) =>
        new($"GlitchTip '{resource.Name}': cannot determine the provisioning URL — {reason}. " +
            $"Set the '{resource.Name}-provision-url' parameter to the externally reachable GlitchTip URL " +
            $"(e.g. Parameters__{resource.Name}-provision-url=http://my-host:8080).");

    private static async Task WaitForReadyAsync(string baseUrl, string resourceName, PipelineStepContext context)
    {
        var ct = context.CancellationToken;
        using var http = new HttpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_readinessTimeout);

        context.Logger.LogInformation(
            "GlitchTip '{Name}': waiting for {BaseUrl} to become ready (first start runs database migrations)...",
            resourceName, baseUrl);

        while (true)
        {
            try
            {
                using var response = await http.GetAsync($"{baseUrl}/api/0/", timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                context.Logger.LogDebug(
                    "GlitchTip '{Name}': readiness probe returned {StatusCode}; retrying...",
                    resourceName, (int)response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                context.Logger.LogDebug(
                    "GlitchTip '{Name}': readiness probe failed ({Message}); retrying...",
                    resourceName, ex.Message);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DistributedApplicationException(
                    $"GlitchTip '{resourceName}': {baseUrl} did not become ready within {s_readinessTimeout.TotalMinutes:0} minutes.");
            }

            try
            {
                await Task.Delay(s_readinessPollInterval, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DistributedApplicationException(
                    $"GlitchTip '{resourceName}': {baseUrl} did not become ready within {s_readinessTimeout.TotalMinutes:0} minutes.");
            }
        }
    }

    private static async Task<bool> SaveIfChangedAsync(
        IDeploymentStateManager stateManager,
        string sectionName,
        string value,
        CancellationToken ct)
    {
        var section = await stateManager.AcquireSectionAsync(sectionName, ct).ConfigureAwait(false);

        // SetValue stores the scalar under the section's root key; compare against that same slot.
        if (section.Data.TryGetPropertyValue(string.Empty, out var existing) && existing?.GetValue<string>() == value)
        {
            return false;
        }

        section.SetValue(value);
        await stateManager.SaveSectionAsync(section, ct).ConfigureAwait(false);
        return true;
    }
}

#pragma warning restore ASPIRECONTAINERRUNTIME001
#pragma warning restore ASPIREPIPELINES002
#pragma warning restore ASPIREPIPELINES001
