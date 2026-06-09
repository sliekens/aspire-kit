// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provisions a GlitchTip organization, team, and project and sets <see cref="GlitchTipResource.DsnKey"/>
/// and <see cref="GlitchTipResource.ProjectId"/> on the resource for use in connection strings.
/// </summary>
internal static class GlitchTipProvisioner
{
    /// <summary>
    /// Runs the provisioning flow in run mode: resolves the endpoint and parameters, then sets
    /// <see cref="GlitchTipResource.DsnKey"/> and <see cref="GlitchTipResource.ProjectId"/>.
    /// Throws <see cref="DistributedApplicationException"/> on any failure so the dashboard surfaces the error.
    /// </summary>
    internal static async Task ProvisionAsync(
        GlitchTipResource resource,
        ILogger logger,
        CancellationToken ct)
    {
        var baseUrl = await resource.PrimaryEndpoint.GetValueAsync(ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': primary endpoint URL is not available.");

        var email = await resource.AdminEmail.GetValueAsync(ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': admin email parameter is not set.");

        var password = await resource.AdminPassword.GetValueAsync(ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': admin password parameter is not set.");

        var orgName = await resource.OrgName.GetValueAsync(ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': org name parameter is not set.");

        var projectName = await resource.ProjectName.GetValueAsync(ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resource.Name}': project name parameter is not set.");

        var (dsnKey, projectId) = await ProvisionAsync(
            baseUrl, email, password, orgName, projectName, resource.Name, logger, ct);

        resource.DsnKey = dsnKey;
        resource.ProjectId = projectId;
    }

    /// <summary>
    /// Core provisioning flow shared by run mode and the deploy pipeline step: authenticates,
    /// ensures org/team/project exist, and fetches the DSN over HTTP.
    /// Throws <see cref="DistributedApplicationException"/> on any failure.
    /// </summary>
    /// <returns>The DSN key (user-info segment) and project ID (path segment).</returns>
    internal static async Task<(string DsnKey, string ProjectId)> ProvisionAsync(
        string baseUrl,
        string email,
        string password,
        string orgName,
        string projectName,
        string resourceName,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("GlitchTip provisioner: starting for resource '{Name}' at {BaseUrl}",
            resourceName, baseUrl);

        using var auth = new GlitchTipAuthClient(baseUrl, logger);
        var token = await auth.GetApiTokenAsync(email, password, ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resourceName}': authentication failed — check admin email and password parameters.");

        var api = new GlitchTipApiClient(baseUrl, token, logger);
        var orgSlug = ToSlug(orgName);
        var projSlug = ToSlug(projectName);

        var org = await api.EnsureOrganizationAsync(orgName, orgSlug, ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resourceName}': failed to ensure organization '{orgSlug}'.");

        logger.LogInformation("GlitchTip provisioner: organization '{Org}' ready", org);

        var team = await api.EnsureTeamAsync(org, orgName, orgSlug, ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resourceName}': failed to ensure team '{orgSlug}' in org '{org}'.");

        logger.LogInformation("GlitchTip provisioner: team '{Team}' ready", team);

        var project = await api.EnsureProjectAsync(org, team, projectName, projSlug, ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resourceName}': failed to ensure project '{projSlug}' in org '{org}'.");

        logger.LogInformation("GlitchTip provisioner: project '{Project}' ready", project);

        var dsn = await api.GetDsnAsync(org, project, ct)
            ?? throw new DistributedApplicationException(
                $"GlitchTip '{resourceName}': failed to retrieve DSN for project '{project}' in org '{org}'.");

        var dsnUri = new Uri(dsn);

        logger.LogInformation("GlitchTip provisioner: DSN ready for project '{Project}'", project);

        // UserInfo contains the DSN key (e.g. "1234567890abcdef1234567890abcdef").
        // AbsolutePath contains the project ID prefixed with "/" (e.g. "/1").
        return (dsnUri.UserInfo, dsnUri.AbsolutePath.TrimStart('/'));
    }

    private static string ToSlug(string name) =>
        name.ToLowerInvariant().Replace(' ', '-');
}
