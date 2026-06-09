// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CommunityToolkit.Aspire.GlitchTip;

/// <summary>
/// Provides the client configuration settings for the GlitchTip integration.
/// </summary>
public sealed class GlitchTipSettings
{
    /// <summary>
    /// Gets or sets the Sentry-compatible DSN for the GlitchTip project.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, the DSN is read from the <c>ConnectionStrings:{connectionName}</c>
    /// configuration key injected by Aspire via <c>WithReference(glitchtip)</c>.
    /// </remarks>
    public string? Dsn { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the GlitchTip health check is disabled.
    /// </summary>
    /// <value>The default value is <see langword="false"/>.</value>
    public bool DisableHealthChecks { get; set; }
}
