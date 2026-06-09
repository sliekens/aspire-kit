# GlitchTip Integration — Design Space & Risk Analysis

Source spike: `../aspire-glitchtip/`  
Date: 2026-06-09

---

## Agreed Design Decisions

| #   | Decision                    | Choice                                                                                                                                                                                                     |
| --- | --------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | Package scope               | Two packages: hosting + client                                                                                                                                                                             |
| 2   | Package names               | `CommunityToolkit.Aspire.Hosting.GlitchTip` + `CommunityToolkit.Aspire.GlitchTip`                                                                                                                          |
| 3   | Resource type               | `GlitchTipResource : ContainerResource, IResourceWithConnectionString`                                                                                                                                     |
| 4   | Connection string value     | `ReferenceExpression` combining provisioned `DsnKey`/`ProjectId` fields with `EndpointReference` for host and port; provisioner sets the fields in `ResourceReadyEvent` handler — no host rewriting needed |
| 5   | Client config section       | `Aspire:GlitchTip` / `Aspire:GlitchTip:{connectionName}`                                                                                                                                                   |
| 6   | Provisioning                | Auto inside the hosting package via `ResourceReadyEvent`; deploy mode deferred — will use the deploy pipeline API, not the legacy publishing manifest                                                      |
| 7   | Dependency ownership        | User passes Postgres and Redis in via `.WithPostgres(pg)` / `.WithRedis(cache)` — this is the primary and only API; no auto-creation                                                                       |
| 8   | Connection string injection | Standard `WithReference(glitchtip)` — injects `ConnectionStrings__<name>` into the target                                                                                                                  |
| 9   | Health check                | `WithHttpHealthCheck("/api/0/")` — no custom `IHealthCheck` class needed                                                                                                                                   |

---

## Design Space

### Dimensions

| Dimension                 | Values considered                                                 | Chosen                                                                                                       |
| ------------------------- | ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| Package scope             | Hosting only / Hosting + client / Hosting + documented convention | Hosting + client                                                                                             |
| Client naming             | GlitchTip-branded / Sentry-branded                                | GlitchTip-branded (SDK is an impl detail)                                                                    |
| Connection string surface | `IResourceWithConnectionString` / raw env var / both              | `IResourceWithConnectionString` only — avoids ambiguity                                                      |
| Connection string shape   | Literal rewritten DSN / `ReferenceExpression` with endpoint refs  | `ReferenceExpression` — host/port resolved per consumer; provisioner populates `DsnKey` + `ProjectId` fields |
| Provisioning location     | Inside package / left to AppHost                                  | Inside package (zero-config)                                                                                 |
| Dependency ownership      | Package owns / user passes / package owns + With-override         | User passes via `.WithPostgres` / `.WithRedis` — matches Umami + Keycloak convention in this repo            |
| Health check              | Custom `IHealthCheck` class / `WithHttpHealthCheck(path)`         | `WithHttpHealthCheck("/api/0/")` — no custom class needed                                                    |
| Injection ergonomics      | `WithReference` only / custom `WithGlitchTip` / both              | `WithReference` (standard Aspire idiom)                                                                      |
| Deploy mode               | Run-mode only / deploy pipeline step / both                       | Run mode only for v1; deploy mode deferred — will use deploy pipeline API (not legacy manifest)              |

### Unexplored / explicitly ruled-out regions

| Region                                                      | Why not chosen                                                                                                                                |
| ----------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| Sentry-branded client (`CommunityToolkit.Aspire.Sentry`)    | SDK is an impl detail; naming by server is the established pattern in this repo                                                               |
| Separate `Dsn` property alongside connection string         | Ambiguity — single surface (connection string) is cleaner                                                                                     |
| `WithGlitchTip` convenience method for injection            | Standard `WithReference` is sufficient; no custom env var key mapping needed                                                                  |
| Pure env var injection (no `IResourceWithConnectionString`) | Loses dashboard visibility and deploy pipeline integration                                                                                    |
| Literal rewritten DSN as connection string                  | Requires `RewriteDsnHost`; wrong for container consumers; not manifest-compatible; replaced by `ReferenceExpression` with endpoint references |
| Package-owned Postgres + Redis (auto-creation)              | Breaks the established convention in this repo (Umami, Keycloak both require user to pass the DB in)                                          |
| Custom `IHealthCheck` class with URL factory                | `WithHttpHealthCheck("/api/0/")` handles endpoint resolution natively                                                                         |
| Legacy publishing manifest for deploy mode                  | Deploy mode will target the deploy pipeline API; manifest generation not the integration point                                                |

---

## Package API sketch

### Hosting package

```csharp
// Wire up dependencies first, then add GlitchTip
var postgres = builder.AddPostgres("postgres").AddDatabase("glitchtip-db");
var redis    = builder.AddRedis("redis");

var glitchtip = builder.AddGlitchTip("glitchtip")
    .WithPostgres(postgres)
    .WithRedis(redis);

// Consuming service
builder.AddProject<Projects.Api>("api")
    .WithReference(glitchtip)
    .WaitFor(glitchtip);
```

`GlitchTipResource` shape:

```csharp
public class GlitchTipResource : ContainerResource, IResourceWithConnectionString
{
    // Populated by the provisioner in ResourceReadyEvent
    public string? DsnKey     { get; internal set; }
    public string? ProjectId  { get; internal set; }

    public EndpointReference PrimaryEndpoint { get; }

    // ReferenceExpression: Aspire resolves host/port per consumer and per mode.
    // DsnKey/ProjectId are set before any consumer evaluates this (WaitFor guarantees it).
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create(
            $"https://{DsnKey}@{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}/{ProjectId}");
}
```

`AddGlitchTip` internally:

1. Creates the `GlitchTipResource` container with all required env vars
2. Calls `.WithHttpHealthCheck("/api/0/")` — no custom health check class
3. Subscribes to `ResourceReadyEvent` to run `GlitchTipProvisioner`, which sets `DsnKey` and `ProjectId` on the resource

Parameters exposed (all overridable via `Parameters:*` config):

| Parameter               | Default             | Secret | Purpose                                    |
| ----------------------- | ------------------- | ------ | ------------------------------------------ |
| `<name>-secret-key`     | generated           | yes    | `SECRET_KEY` container env var             |
| `<name>-admin-email`    | `admin@dev.local`   | no     | Bootstrap admin account + provisioner auth |
| `<name>-admin-password` | `Admin1234!`        | yes    | Bootstrap admin account + provisioner auth |
| `<name>-org-name`       | `<name>`            | no     | Org created/ensured by provisioner         |
| `<name>-project-name`   | `<name>`            | no     | Project created/ensured by provisioner     |
| `<name>-from-email`     | `email@example.com` | no     | `DEFAULT_FROM_EMAIL` container env var     |

### Client package

```csharp
// Program.cs in a service
builder.AddGlitchTipClient();                          // uses connectionName "glitchtip"
builder.AddGlitchTipClient("monitoring");              // named connection
builder.AddKeyedGlitchTipClient("secondary");          // keyed DI
```

Reads `ConnectionStrings:<connectionName>` (DSN), configures the Sentry .NET SDK.
Config section `Aspire:GlitchTip:{connectionName}` for future settings (sample rate, etc.).

---

## FMEA — Runtime Failure Modes

Scope: provisioning flow + DSN injection. Scoring: Severity (S), Occurrence (O), Detection (D), RPN = S×O×D.

| #   | Component                                                 | Failure mode                                                                  | Effect                                                                                             | S   | O   | D   | RPN          | Current control (spike)                                            | Recommended mitigation                                                                                                                                            |
| --- | --------------------------------------------------------- | ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- | --- | --- | --- | ------------ | ------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | Health check                                              | Wrong endpoint (`/api/0/_health/` does not exist in v6)                       | Health check never passes; dependent services never start                                          | 9   | 6   | 3   | 162          | Spike uses `/api/0/`                                               | `WithHttpHealthCheck("/api/0/")`; add integration test                                                                                                            |
| 2   | Auth — CSRF rotation                                      | Pre-rotation CSRF token sent to `/api/0/api-tokens/`                          | 403; no API token; provisioning fails                                                              | 8   | 5   | 4   | 160          | Spike reads CSRF after login                                       | Always call `GetCsrfToken()` after session is established, not before                                                                                             |
| 3   | ~~DSN host rewriting~~                                    | ~~DSN contains internal container address; host services can't reach it~~     | ~~Sentry SDK sends events to unreachable URL; silently dropped~~                                   | —   | —   | —   | **resolved** | —                                                                  | **Eliminated**: `ConnectionStringExpression` uses `EndpointReference` for host/port; Aspire resolves per consumer. `RewriteDsnHost` not needed.                   |
| 4   | API idempotency — org                                     | Duplicate org creation returns 403                                            | Provisioner fails on re-run; `DsnKey`/`ProjectId` not set                                          | 7   | 6   | 3   | 126          | Spike does GET-before-POST                                         | Check-before-create pattern; treat 200 on GET as success                                                                                                          |
| 5   | API idempotency — team                                    | Duplicate team creation returns 500                                           | Provisioner fails on re-run                                                                        | 7   | 6   | 3   | 126          | Spike lists teams and checks slug                                  | List + slug match; treat existing as success                                                                                                                      |
| 6   | API idempotency — project                                 | Duplicate project silently appends numeric suffix to slug                     | `ProjectId` set to wrong slug; DSN fetched for wrong project                                       | 8   | 6   | 8   | 384          | Spike lists projects and matches org+slug                          | List + (org slug, project slug) match; never POST if found                                                                                                        |
| 7   | `ResourceReadyEvent` handler failure                      | Exception in provisioner; event handler swallows it                           | `DsnKey`/`ProjectId` stay null; `WithReference` injects empty string; Sentry SDK disabled silently | 7   | 4   | 7   | 196          | None in spike                                                      | Catch and log; throw `DistributedApplicationException` to fail fast and surface in dashboard                                                                      |
| 8   | Provisioner called without allocated endpoint             | `GlitchTipResource.PrimaryEndpoint` URL null when provisioner runs            | NullReferenceException; provisioner cannot call GlitchTip API                                      | 8   | 2   | 4   | 64           | Spike uses `ResourceEndpointsAllocatedEvent` first                 | Aspire guarantees endpoints are allocated before `ResourceReadyEvent` fires; read the allocated URL from `PrimaryEndpoint` at handler start; guard with assertion |
| 9   | Wrong `GLITCHTIP_DOMAIN` value                            | Container env var set to external URL instead of internal URL                 | Outgoing requests from container use wrong host                                                    | 5   | 4   | 6   | 120          | Spike sets `GLITCHTIP_DOMAIN` to `glitchhttp` (endpoint reference) | Set `GLITCHTIP_DOMAIN` to the internal endpoint reference, not the external URL                                                                                   |
| 10  | GlitchTip v6 auth endpoint change                         | Future GlitchTip version changes allauth URL or drops browser mode            | Auth flow broken; no API token                                                                     | 7   | 3   | 5   | 105          | None                                                               | Pin container image tag (not `latest`); integration test against pinned version; document tested version in README                                                |
| 11  | Admin password strength                                   | Weak default password rejected by GlitchTip's validator                       | Signup fails; provisioning fails                                                                   | 5   | 3   | 4   | 60           | Default is `Admin1234!` (passes)                                   | Document password policy; validate at parameter resolution time                                                                                                   |
| 12  | Postgres not ready when GlitchTip starts                  | GlitchTip container starts, migrations fail, container exits or loops         | Health check never passes                                                                          | 7   | 4   | 3   | 84           | Spike uses `WaitFor(postgres)`                                     | `.WithPostgres` must call `.WaitFor(database)` on the GlitchTip resource builder                                                                                  |
| 13  | API token label collision                                 | Multiple Aspire runs create multiple `aspire-provisioner` tokens              | Token list grows; functionally harmless but messy                                                  | 2   | 7   | 9   | 126          | None                                                               | `GET /api/0/api-tokens/` before creating; re-use existing token with matching label if found                                                                      |
| 14  | `DsnKey`/`ProjectId` null when consumer env vars resolved | `WithEnvironment` callback captures null fields if provisioner hasn't run yet | Sentry SDK disabled in service                                                                     | 9   | 2   | 5   | 90           | Spike: `WaitFor` blocks service start                              | `WaitFor(glitchtip)` ensures service doesn't start until `ResourceReadyEvent` handlers complete; document this requirement                                        |
| 15  | Project list pagination                                   | `GET /api/0/projects/` returns only first page; duplicate project not found   | Idempotency check misses existing project; auto-increment suffix fires; `ProjectId` wrong          | 8   | 3   | 7   | 168          | None in spike                                                      | Confirm GlitchTip returns all projects in one response; if paginated, follow `next` links until exhausted                                                         |

### High-priority items (RPN > 150)

| #     | Item                            | RPN      | Action                                                                           |
| ----- | ------------------------------- | -------- | -------------------------------------------------------------------------------- |
| ~~3~~ | ~~DSN host rewriting~~          | resolved | Eliminated by `ReferenceExpression` with endpoint references                     |
| 6     | Project slug auto-increment     | 384      | List+match check before any project creation; guard against pagination (see #15) |
| 7     | Provisioner exception swallowed | 196      | Fail fast with `DistributedApplicationException`                                 |
| 15    | Project list pagination         | 168      | Confirm single-page response or implement `next`-link following                  |
| 1     | Wrong health endpoint           | 162      | `WithHttpHealthCheck("/api/0/")`; integration test                               |
| 2     | CSRF rotation                   | 160      | Read CSRF after session establishment                                            |

---

## Open questions

1. ~~**`WithPostgres` / `WithRedis` complexity**~~ — resolved: user passes dependencies in; no auto-creation.
2. **Deploy pipeline API scope** — deploy mode targets the deploy pipeline API (not the legacy manifest). The key open sub-questions are: (a) how does the deploy step obtain and persist the `DsnKey`/`ProjectId` provisioned values, and (b) in deploy mode `DsnKey`/`ProjectId` are null at manifest build time — does the deploy step substitute them as parameter values, or inject the full DSN literal? Decide before implementing deploy support.
3. **Image tag to pin** — spike uses `glitchtip/glitchtip:6`. Confirm exact digest or `major.minor` tag before shipping. Check https://glitchtip.com/documentation/install for the current recommended tag.
4. **`SERVER_ROLE=all_in_one` vs split roles** — spike uses all-in-one. Multi-worker deployment (web + worker + beat) is out of scope for v1 but should be noted in README.
5. **Project list pagination** — does `GET /api/0/projects/` return all projects in a single response? Verify against the OpenAPI spec at https://app.glitchtip.com/api/openapi.json and GlitchTip source before shipping `EnsureProjectAsync`.

---

## References

| Resource                  | URL                                                           |
| ------------------------- | ------------------------------------------------------------- |
| GlitchTip documentation   | https://glitchtip.com/documentation                           |
| GlitchTip install guide   | https://glitchtip.com/documentation/install                   |
| GlitchTip getting started | https://glitchtip.com/documentation/getting-started           |
| GlitchTip OpenAPI spec    | https://app.glitchtip.com/api/openapi.json                    |
| Spike implementation      | `../aspire-glitchtip/GlitchTipDemo.AppHost/GlitchTipSetup.cs` |
| Aspire source reference   | `../aspire/src/Aspire.Hosting/`                               |
