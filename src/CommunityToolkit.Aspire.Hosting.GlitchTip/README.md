# CommunityToolkit.Aspire.Hosting.GlitchTip

Provides Aspire hosting support for [GlitchTip](https://glitchtip.com/), an open-source error monitoring server compatible with the Sentry SDK.

## Getting started

### Install the package

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.GlitchTip
```

## Usage

In your AppHost, wire up Postgres and Redis first, then add GlitchTip:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").AddDatabase("glitchtip-db");
var redis = builder.AddRedis("redis");

var adminEmail = builder.AddParameter("glitchtip-admin-email");
var adminPassword = builder.AddParameter("glitchtip-admin-password", secret: true);

var glitchtip = builder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
    .WithPostgres(postgres)
    .WithRedis(redis);

builder.AddProject<Projects.Api>("api")
    .WithReference(glitchtip)
    .WaitFor(glitchtip);

builder.Build().Run();
```

`WaitFor(glitchtip)` on a consuming service ensures the connection string is available at startup — the provisioner sets it as part of the readiness signal.

## Provisioning

On first run the package automatically:

1. Creates a GlitchTip admin account using the `<name>-admin-email` and `<name>-admin-password` parameters.
2. Creates (or finds) an organization, team, and project named after the `<name>-org-name` and `<name>-project-name` parameters.
3. Retrieves the project DSN and makes it available via `WithReference(glitchtip)` as `ConnectionStrings__<name>`.

Subsequent runs are idempotent — the provisioner detects existing resources and skips creation.

## Parameters

All parameters can be overridden via `Parameters:*` configuration or user secrets:

| Parameter | Default | Secret | Purpose |
|---|---|---|---|
| `<name>-admin-email` | required | no | Bootstrap admin + provisioner auth |
| `<name>-admin-password` | required | yes | Bootstrap admin + provisioner auth |
| `<name>-secret-key` | generated | yes | `SECRET_KEY` container env var |
| `<name>-org-name` | `<name>` | no | Organization created/ensured by provisioner |
| `<name>-project-name` | `<name>` | no | Project created/ensured by provisioner |
| `<name>-from-email` | `email@example.com` | no | `DEFAULT_FROM_EMAIL` container env var |
| `<name>-dsn-key` | empty (deploy mode only) | yes | DSN key injected into consumer connection strings |
| `<name>-project-id` | empty (deploy mode only) | no | Project ID injected into consumer connection strings |
| `<name>-provision-url` | derived (deploy mode only) | no | URL the deploy machine uses to reach GlitchTip (`WithDeployTimeProvisioning`) |

## Connection string format

The connection string injected via `WithReference` is a Sentry-compatible DSN:

```
http://<key>@<host>:<port>/<project-id>
```

The companion `CommunityToolkit.Aspire.GlitchTip` package maps it to `Sentry:Dsn` automatically; it can also be read directly from `options.Dsn` or the `Sentry:Dsn` configuration key.

## Deploy mode (`aspire deploy`)

In publish/deploy mode the connection string is built from the `<name>-dsn-key` and `<name>-project-id` parameters, which never prompt and default to empty. Until they have values, consuming services receive an incomplete DSN, which `CommunityToolkit.Aspire.GlitchTip` treats as "error reporting disabled" — apps start and run normally.

Two ways to supply the values:

**Manual** — provision GlitchTip once (UI or API), then set the parameters in CI configuration and redeploy:

```
Parameters__glitchtip-dsn-key=<key>
Parameters__glitchtip-project-id=<id>
```

**Automatic** — opt in with `WithDeployTimeProvisioning()`:

```csharp
var glitchtip = builder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
    .WithPostgres(postgres)
    .WithRedis(redis)
    .WithDeployTimeProvisioning();
```

After the deployment, a pipeline step reaches GlitchTip over HTTP from the deploy machine, creates the org/team/project idempotently, and saves the DSN values to deployment state. They flow into consuming services on the **next** `aspire deploy` (a single-deploy handoff is not possible with today's Aspire pipeline; see [microsoft/aspire#18097](https://github.com/microsoft/aspire/issues/18097)).

For Docker Compose deployments the provisioning URL is resolved automatically from the published port (the HTTP endpoint is marked external for this purpose). For other targets, or when the deploy machine is not the container host, set `Parameters__<name>-provision-url` to the externally reachable GlitchTip URL.

## Notes

- This package runs GlitchTip in `SERVER_ROLE=all_in_one` mode. Multi-worker deployments (separate web, worker, and beat containers) are not supported in v1.
- The image is pinned to `glitchtip/glitchtip:6`. The provisioner was tested against GlitchTip v6.
