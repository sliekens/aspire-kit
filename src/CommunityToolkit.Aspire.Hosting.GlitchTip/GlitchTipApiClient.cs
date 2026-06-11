// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Performs idempotent CRUD against the GlitchTip REST API using a bearer token.
/// </summary>
/// <remarks>
/// Check-before-create strategy prevents duplicate resources regardless of the API's
/// inconsistent conflict handling: org → 403, team → 500, project → auto-increments slug.
/// </remarks>
internal sealed class GlitchTipApiClient(string baseUrl, string bearerToken, ILogger logger)
{
    // IHttpClientFactory is not used here: this client is constructed once per provisioning run
    // (a one-shot startup operation), so socket exhaustion and handler lifetime are non-issues.
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/'))
    };

    /// <summary>
    /// Ensures an organization with the given slug exists, creating it if necessary.
    /// </summary>
    /// <returns>The organization slug, or <see langword="null"/> on failure.</returns>
    internal async Task<string?> EnsureOrganizationAsync(string name, string slug, CancellationToken ct)
    {
        using var get = await SendAsync(HttpMethod.Get, $"/api/0/organizations/{slug}/", ct: ct);
        logger.LogDebug("GlitchTip provisioner: GET org/{Slug} → {StatusCode}", slug, (int)get.StatusCode);
        if (get.IsSuccessStatusCode) return slug;

        using var create = await SendAsync(HttpMethod.Post, "/api/0/organizations/", new { name, slug }, ct);
        logger.LogDebug("GlitchTip provisioner: POST org → {StatusCode}", (int)create.StatusCode);
        return create.IsSuccessStatusCode ? await ReadSlugAsync(create, ct) : null;
    }

    /// <summary>
    /// Ensures a team with the given slug exists within the organization, creating it if necessary.
    /// </summary>
    /// <returns>The team slug, or <see langword="null"/> on failure.</returns>
    internal async Task<string?> EnsureTeamAsync(string orgSlug, string name, string slug, CancellationToken ct)
    {
        using var list = await SendAsync(HttpMethod.Get, $"/api/0/organizations/{orgSlug}/teams/", ct: ct);
        logger.LogDebug("GlitchTip provisioner: GET org/{OrgSlug}/teams → {StatusCode}", orgSlug, (int)list.StatusCode);
        if (list.IsSuccessStatusCode && await ContainsSlugAsync(list, slug, ct))
            return slug;

        using var create = await SendAsync(
            HttpMethod.Post, $"/api/0/organizations/{orgSlug}/teams/", new { name, slug }, ct);
        logger.LogDebug("GlitchTip provisioner: POST org/{OrgSlug}/teams → {StatusCode}", orgSlug, (int)create.StatusCode);
        return create.IsSuccessStatusCode ? await ReadSlugAsync(create, ct) : null;
    }

    /// <summary>
    /// Ensures a project with the given slug exists within the org/team, creating it if necessary.
    /// </summary>
    /// <remarks>
    /// GlitchTip silently appends a numeric suffix to duplicate project slugs; this method lists
    /// all projects (following pagination) to detect existing projects before attempting creation.
    /// </remarks>
    /// <returns>The project slug, or <see langword="null"/> on failure.</returns>
    internal async Task<string?> EnsureProjectAsync(
        string orgSlug, string teamSlug, string name, string slug, CancellationToken ct)
    {
        var projects = await GetAllProjectsAsync(ct);
        foreach (var proj in projects)
        {
            if (proj.TryGetProperty("slug", out var s) && s.GetString() == slug &&
                proj.TryGetProperty("organization", out var org) &&
                org.TryGetProperty("slug", out var os) && os.GetString() == orgSlug)
            {
                logger.LogDebug("GlitchTip provisioner: project {Slug} already exists in {OrgSlug}", slug, orgSlug);
                return slug;
            }
        }

        using var create = await SendAsync(
            HttpMethod.Post, $"/api/0/teams/{orgSlug}/{teamSlug}/projects/",
            new { name, platform = "dotnet" }, ct);
        logger.LogDebug("GlitchTip provisioner: POST teams/{OrgSlug}/{TeamSlug}/projects → {StatusCode}",
            orgSlug, teamSlug, (int)create.StatusCode);
        return create.IsSuccessStatusCode ? await ReadSlugAsync(create, ct) : null;
    }

    /// <summary>
    /// Returns the public DSN for a project.
    /// </summary>
    /// <returns>The DSN string, or <see langword="null"/> on failure.</returns>
    internal async Task<string?> GetDsnAsync(string orgSlug, string projectSlug, CancellationToken ct)
    {
        using var resp = await SendAsync(
            HttpMethod.Get, $"/api/0/projects/{orgSlug}/{projectSlug}/keys/", ct: ct);
        logger.LogDebug("GlitchTip provisioner: GET project/{ProjectSlug}/keys → {StatusCode}",
            projectSlug, (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement;
        return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0].GetProperty("dsn").GetProperty("public").GetString()
            : null;
    }

    // Fetches all projects, following Link: next pagination headers.
    private async Task<List<JsonElement>> GetAllProjectsAsync(CancellationToken ct)
    {
        var results = new List<JsonElement>();
        var path = "/api/0/projects/";

        while (path is not null)
        {
            using var resp = await SendAsync(HttpMethod.Get, path, ct: ct);
            logger.LogDebug("GlitchTip provisioner: GET {Path} → {StatusCode}", path, (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode) break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                results.Add(item.Clone());
            }

            path = GetNextLink(resp);
        }

        return results;
    }

    // Parses the Link header for rel="next" to support cursor-based pagination.
    private static string? GetNextLink(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Link", out var values))
            return null;

        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                var segments = part.Trim().Split(';');
                if (segments.Length < 2) continue;

                var hasNext = segments.Skip(1).Any(s =>
                    s.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase));

                if (hasNext)
                {
                    var url = segments[0].Trim();
                    if (url.StartsWith('<') && url.EndsWith('>'))
                        return url[1..^1];
                }
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await _http.SendAsync(req, ct);
    }

    private static async Task<string?> ReadSlugAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("slug", out var v) ? v.GetString() : null;
    }

    private static async Task<bool> ContainsSlugAsync(
        HttpResponseMessage resp, string slug, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("slug", out var v) && v.GetString() == slug)
                return true;
        }
        return false;
    }
}
