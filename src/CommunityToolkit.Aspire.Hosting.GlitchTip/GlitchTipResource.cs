// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREATS001 // AspireExport is experimental

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a GlitchTip container resource.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class GlitchTipResource : ContainerResource, IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "http";

    /// <summary>
    /// Initializes a new instance of <see cref="GlitchTipResource"/>.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="secretKey">The <c>SECRET_KEY</c> parameter.</param>
    /// <param name="adminEmail">The bootstrap admin email parameter.</param>
    /// <param name="adminPassword">The bootstrap admin password parameter.</param>
    /// <param name="orgName">The organization name parameter.</param>
    /// <param name="projectName">The project name parameter.</param>
    public GlitchTipResource(
        [ResourceName] string name,
        ParameterResource secretKey,
        ParameterResource adminEmail,
        ParameterResource adminPassword,
        ParameterResource orgName,
        ParameterResource projectName)
        : base(name)
    {
        SecretKey = secretKey;
        AdminEmail = adminEmail;
        AdminPassword = adminPassword;
        OrgName = orgName;
        ProjectName = projectName;
        PrimaryEndpoint = new EndpointReference(this, PrimaryEndpointName);
    }

    /// <summary>
    /// Gets the DSN key (user-info segment) set by the provisioner after the container is ready.
    /// </summary>
    public string? DsnKey { get; internal set; }

    /// <summary>
    /// Gets the project ID (path segment) set by the provisioner after the container is ready.
    /// </summary>
    public string? ProjectId { get; internal set; }

    /// <summary>
    /// Gets the parameter that carries the DSN key in publish mode. Set by <c>AddGlitchTip</c> so the
    /// connection string publishes as a parameter reference instead of a literal value.
    /// </summary>
    internal ParameterResource? DsnKeyParameter { get; set; }

    /// <summary>
    /// Gets the parameter that carries the project ID in publish mode. Set by <c>AddGlitchTip</c> so the
    /// connection string publishes as a parameter reference instead of a literal value.
    /// </summary>
    internal ParameterResource? ProjectIdParameter { get; set; }

    /// <summary>
    /// Gets the primary HTTP endpoint reference.
    /// </summary>
    public EndpointReference PrimaryEndpoint { get; }

    /// <summary>Gets the <c>SECRET_KEY</c> parameter.</summary>
    public ParameterResource SecretKey { get; }

    /// <summary>Gets the bootstrap admin email parameter.</summary>
    public ParameterResource AdminEmail { get; }

    /// <summary>Gets the bootstrap admin password parameter.</summary>
    public ParameterResource AdminPassword { get; }

    /// <summary>Gets the organization name parameter.</summary>
    public ParameterResource OrgName { get; }

    /// <summary>Gets the project name parameter.</summary>
    public ParameterResource ProjectName { get; }

    /// <summary>
    /// Gets a <see cref="ReferenceExpression"/> representing the Sentry-compatible DSN for this GlitchTip project.
    /// </summary>
    /// <remarks>
    /// The DSN embeds host and port from the allocated endpoint, resolved per consumer by Aspire.
    /// In run mode, <see cref="DsnKey"/> and <see cref="ProjectId"/> are populated by the provisioner in the
    /// <c>ResourceReadyEvent</c> handler; consumers must call <c>WaitFor</c> on this resource to
    /// guarantee values are set before the connection string is evaluated.
    /// In publish mode, the key and project ID are parameter references so each publisher materializes
    /// them with its native mechanism (e.g. env-file placeholders for Docker Compose).
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression =>
        DsnKeyParameter is not null && ProjectIdParameter is not null
            ? ReferenceExpression.Create(
                $"{PrimaryEndpoint.Property(EndpointProperty.Scheme)}://{DsnKeyParameter}@{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}/{ProjectIdParameter}")
            : ReferenceExpression.Create(
                $"{PrimaryEndpoint.Property(EndpointProperty.Scheme)}://{DsnKey}@{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}/{ProjectId}");
}

#pragma warning restore ASPIREATS001 // AspireExport is experimental
