// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using CommunityToolkit.Aspire.GlitchTip;
using HealthChecks.Uris;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for configuring GlitchTip (Sentry-compatible) error monitoring in a <see cref="WebApplicationBuilder"/>.
/// </summary>
public static class AspireGlitchTipExtensions
{
    private const string DefaultConfigSectionName = "Aspire:GlitchTip";

    /// <summary>
    /// Registers GlitchTip error monitoring by reading the Aspire-injected DSN and initializing the Sentry SDK.
    /// </summary>
    /// <param name="builder">The <see cref="WebApplicationBuilder"/> to configure.</param>
    /// <param name="connectionName">The connection string name used to find the DSN.</param>
    /// <param name="configureSettings">An optional method for customizing the <see cref="GlitchTipSettings"/>.</param>
    /// <remarks>
    /// <para>
    /// The DSN is read from <c>ConnectionStrings:{connectionName}</c> (set by <c>WithReference(glitchtip)</c> in the AppHost)
    /// and passed directly to the Sentry SDK via <c>builder.WebHost.UseSentry()</c>.
    /// </para>
    /// <para>
    /// Settings are read from the <c>Aspire:GlitchTip</c> configuration section.
    /// </para>
    /// </remarks>
    public static void AddGlitchTipClient(
        this WebApplicationBuilder builder,
        string connectionName,
        Action<GlitchTipSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        AddGlitchTipClientCore(builder, DefaultConfigSectionName, configureSettings, connectionName);
    }

    private static void AddGlitchTipClientCore(
        WebApplicationBuilder builder,
        string configSectionName,
        Action<GlitchTipSettings>? configureSettings,
        string connectionName)
    {
        var settings = new GlitchTipSettings();
        var config = (IConfiguration)builder.Configuration;
        config.GetSection(configSectionName).Bind(settings);

        if (config.GetConnectionString(connectionName) is string connectionString)
        {
            settings.Dsn = connectionString;
        }

        // In deploy mode the DSN is parameterized and may be incomplete until provisioning has run
        // (e.g. "http://@glitchtip:8000/"). Treat a DSN without a key as absent so the Sentry SDK
        // is disabled instead of throwing on a malformed DSN at startup.
        if (settings.Dsn is { Length: > 0 } candidateDsn &&
            (!Uri.TryCreate(candidateDsn, UriKind.Absolute, out var candidateUri) ||
             candidateUri.UserInfo.Length == 0 ||
             candidateUri.AbsolutePath.TrimStart('/').Length == 0))
        {
            settings.Dsn = null;
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddSingleton(settings);

        if (settings.Dsn is { Length: > 0 } dsn)
        {
            builder.WebHost.UseSentry(options => options.Dsn = dsn);
        }

        if (!settings.DisableHealthChecks && settings.Dsn is { Length: > 0 } healthDsn)
        {
            if (Uri.TryCreate(healthDsn, UriKind.Absolute, out var dsnUri))
            {
                var healthEndpoint = new Uri($"{dsnUri.Scheme}://{dsnUri.Host}:{dsnUri.Port}/api/0/");
                var uriHealthCheck = new UriHealthCheck(
                    new UriHealthCheckOptions().AddUri(healthEndpoint),
                    () => new HttpClient());

                builder.TryAddHealthCheck(new HealthCheckRegistration(
                    "GlitchTip",
                    _ => uriHealthCheck,
                    failureStatus: null,
                    tags: null,
                    timeout: null));
            }
        }
    }
}
