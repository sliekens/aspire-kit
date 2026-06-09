// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Authenticates with GlitchTip using the allauth headless browser flow and returns a scoped API token.
/// </summary>
/// <remarks>
/// GlitchTip v6 uses django-allauth headless (browser mode) instead of the legacy /api/0/auth/* endpoints.
/// Auth flow: GET session (→ csrftoken cookie) → POST signup or login (→ session + rotated CSRF) → POST api-tokens.
/// CSRF rotation: Django rotates csrftoken after successful signup or login.
/// The <see cref="CookieContainer"/> auto-updates, so <c>GetCsrfToken()</c> always returns the current value.
/// </remarks>
internal sealed class GlitchTipAuthClient : IDisposable
{
    private static readonly string[] s_apiTokenScopes =
    [
        "org:admin", "org:read", "org:write",
        "team:read", "team:write",
        "project:read", "project:write",
        "event:read", "event:write",
        "member:read"
    ];

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly Uri _baseUri;
    private readonly ILogger _logger;

    internal GlitchTipAuthClient(string baseUrl, ILogger logger)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _cookies = new CookieContainer();
        // IHttpClientFactory is not used here: the allauth browser flow requires a stateful
        // CookieContainer that spans multiple requests (CSRF init → login → token creation).
        // The factory's pooled handler lifecycle would either share cookies across concurrent
        // provisioning calls or require bypassing pooling entirely, which negates its purpose.
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, UseCookies = true })
        {
            BaseAddress = _baseUri
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _logger = logger;
    }

    /// <summary>
    /// Obtains a GlitchTip API bearer token, creating the admin account on first run.
    /// </summary>
    /// <returns>The bearer token, or <see langword="null"/> on failure.</returns>
    internal async Task<string?> GetApiTokenAsync(string email, string password, CancellationToken ct)
    {
        _logger.LogDebug("GlitchTip provisioner: initializing CSRF...");
        if (!await InitCsrfAsync(ct))
        {
            _logger.LogError("GlitchTip provisioner: failed to obtain CSRF token");
            return null;
        }

        _logger.LogDebug("GlitchTip provisioner: establishing session for {Email}...", email);
        if (!await EnsureSessionAsync(email, password, ct))
        {
            _logger.LogError("GlitchTip provisioner: failed to establish session");
            return null;
        }

        _logger.LogDebug("GlitchTip provisioner: creating API token...");
        var token = await CreateApiTokenAsync(ct);
        if (token is null)
            _logger.LogError("GlitchTip provisioner: failed to create API token");
        else
            _logger.LogDebug("GlitchTip provisioner: API token acquired");

        return token;
    }

    // GETs the allauth session endpoint to set the initial csrftoken cookie.
    private async Task<bool> InitCsrfAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("_allauth/browser/v1/auth/session", ct);
            _logger.LogDebug("GlitchTip provisioner: GET session → {StatusCode}", (int)resp.StatusCode);
            return GetCsrfToken() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GlitchTip provisioner: GET session exception");
            return false;
        }
    }

    // Attempts signup (first run), falling back to login when the account already exists.
    // Django rotates csrftoken after successful signup or login; CookieContainer auto-updates.
    private async Task<bool> EnsureSessionAsync(string email, string password, CancellationToken ct)
    {
        var csrf = GetCsrfToken();
        if (csrf is null) return false;

        using var signupResp = await PostAllauthAsync(
            "_allauth/browser/v1/auth/signup", new { email, password }, csrf, ct);
        _logger.LogDebug("GlitchTip provisioner: POST signup → {StatusCode}", (int)signupResp.StatusCode);
        if (signupResp.IsSuccessStatusCode) return true;

        // After failed signup, CSRF is NOT rotated. Use the same CSRF for login.
        using var loginResp = await PostAllauthAsync(
            "_allauth/browser/v1/auth/login", new { email, password }, csrf, ct);
        _logger.LogDebug("GlitchTip provisioner: POST login → {StatusCode}", (int)loginResp.StatusCode);
        if (!loginResp.IsSuccessStatusCode)
        {
            var body = await loginResp.Content.ReadAsStringAsync(ct);
            _logger.LogError("GlitchTip provisioner: login failed: {Body}", body[..Math.Min(200, body.Length)]);
        }

        return loginResp.IsSuccessStatusCode;
    }

    // Creates a scoped API token via the session-authenticated cookie endpoint.
    private async Task<string?> CreateApiTokenAsync(CancellationToken ct)
    {
        // Read CSRF after session establishment — Django rotates it on successful login/signup.
        var csrf = GetCsrfToken();
        if (csrf is null) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/0/api-tokens/");
        req.Headers.Add("X-CSRFToken", csrf);
        req.Content = JsonContent.Create(new { label = "aspire-provisioner", scopes = s_apiTokenScopes });

        using var resp = await _http.SendAsync(req, ct);
        _logger.LogDebug("GlitchTip provisioner: POST api-tokens → {StatusCode}", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("GlitchTip provisioner: api-tokens failed: {Body}", body[..Math.Min(200, body.Length)]);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("token").GetString();
    }

    private async Task<HttpResponseMessage> PostAllauthAsync(
        string path, object body, string csrf, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Add("X-CSRFToken", csrf);
        req.Content = JsonContent.Create(body);
        return await _http.SendAsync(req, ct);
    }

    private string? GetCsrfToken() => _cookies.GetCookies(_baseUri)["csrftoken"]?.Value;

    public void Dispose() => _http.Dispose();
}
