# GlitchTip deploy-mode provisioning — implementation plan

## Goal

The provisioner becomes a proper Aspire resource that works identically in run mode and any deployment target. Consuming services call `WaitForCompletion` on it and receive the DSN on first run with no hacks, no workarounds, and no stack-specific assumptions in the core model.

---

## High-level design

```
                  ┌──────────────────────────────────────────────────────────────────────────┐
                  │  Aspire AppHost                                                          │
                  │                                                                          │
                  │  builder.AddGlitchTip(...)                                               │
                  │         │                                                                │
                  │         ├──► GlitchTipResource (server container)                        │
                  │         │         │  WaitFor ←── GlitchTipProvisionerResource            │
                  │         │         │              (job container)                         │
                  │         │         │                │                                     │
                  │         │         │                │ stdout: ASPIRE_GLITCHTIP_DSN=key/id │
                  │         │         │                │                                     │
                  │         │         │           ResourceLoggerService.WatchAsync           │
                  │         │         │                │                                     │
                  │         │         │           DsnTcs.TrySetResult(dsn)                   │
                  │         │         │                                                      │
                  │         └──► ConnectionStringExpression                                  │
                  │                   └── awaits DsnTcs before returning DSN                 │
                  │                                                                          │
                  │  App services:                                                           │
                  │    .WithReference(glitchtip)          ← binds to DSN                     │
                  │    .WaitForCompletion(glitchtip.Resource.Provisioner)                    │
                  │       ↓                                                                  │
                  │    starts only after provisioner exits 0                                 │
                  └──────────────────────────────────────────────────────────────────────────┘

  Run mode:    DCP runs both containers; log watcher sets DsnTcs; app gets DSN.
  Deploy mode: Job container runs inside target network; app waits via platform
               dependency mechanism; DSN propagation is target-specific (see below).
```

---

## Comparison with `EFMigrationResource`

`EFMigrationResource` is the closest existing pattern and directly informs this design. The critical difference:

|                              | `EFMigrationResource`                                           | `GlitchTipProvisionerResource`                        |
| ---------------------------- | --------------------------------------------------------------- | ----------------------------------------------------- |
| Base class                   | `ContainerResource`                                             | `ContainerResource`                                   |
| Run mode                     | `BeforeStartEvent` → local process via `ResourceCommandService` | Container in DCP; log watcher on host                 |
| Deploy mode                  | Pipeline step builds + runs migration bundle container          | Job container in target platform                      |
| Output to consuming services | **None** — only signals "done"                                  | **DSN string** — must reach consuming containers' env |
| Consumers call               | `WaitFor(migration)`                                            | `WaitForCompletion(glitchtip.Resource.Provisioner)`   |

**The unsolved problem**: EF migrations do not need to pass any value to the services that wait for them — the database connection string was already known at configuration time. The GlitchTip provisioner must produce a DSN (`http://<key>@<host>:<port>/<id>`) that is unknown until the provisioner runs. This value must reach consuming containers as a connection string. That propagation path differs per deployment target and is the core design challenge this plan addresses.

---

## New resource: `GlitchTipProvisionerResource`

```csharp
public sealed class GlitchTipProvisionerResource(string name, GlitchTipResource glitchTip)
    : ContainerResource(name)
{
    public GlitchTipResource GlitchTip { get; } = glitchTip;

    // Completed by the host-side log watcher when it parses the DSN from stdout.
    internal TaskCompletionSource<string> DsnTcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

`ContainerResource` already implements `IResourceWithWaitSupport`, so `WaitForCompletion` works with no extra interfaces.

---

## New console app: `CommunityToolkit.Aspire.Hosting.GlitchTip.Provisioner`

A standalone, self-contained .NET console app. In run mode DCP runs it as a container; in deploy mode it is the job container image.

### Entry point

```csharp
// Program.cs
var url      = Env("GLITCHTIP_URL");
var email    = Env("GLITCHTIP_ADMIN_EMAIL");
var password = Env("GLITCHTIP_ADMIN_PASSWORD");
var org      = Env("GLITCHTIP_ORG");
var project  = Env("GLITCHTIP_PROJECT");

var (dsnKey, projectId) = await GlitchTipProvisioner.ProvisionAsync(url, email, password, org, project);

// Structured marker that the Aspire host log watcher looks for.
Console.WriteLine($"ASPIRE_GLITCHTIP_DSN={dsnKey}/{projectId}");
```

### Container image

The image tag constants live in `GlitchTipProvisionerContainerImageTags` (image name, tag). The registry is **not hardcoded** — it is inherited from whatever registry the user has already configured on the application builder (same convention used by all other integrations in this toolkit).

---

## Changes to `GlitchTipResource`

Add a `Provisioner` property and update `ConnectionStringExpression` to block until the DSN is ready:

```csharp
internal GlitchTipProvisionerResource? Provisioner { get; set; }

public ReferenceExpression ConnectionStringExpression =>
    ReferenceExpression.Create($"{new GlitchTipConnectionStringReference(this)}");
```

`GlitchTipConnectionStringReference.GetValueAsync`:

```csharp
public async ValueTask<string?> GetValueAsync(CancellationToken ct)
{
    if (Resource.Provisioner is { } p)
        await p.DsnTcs.Task.WaitAsync(ct);
    return $"{Resource.PrimaryEndpoint.Scheme}://{Resource.DsnKey}@"
         + $"{Resource.PrimaryEndpoint.Host}:{Resource.PrimaryEndpoint.Port}/{Resource.ProjectId}";
}
```

---

## Changes to `AddGlitchTip`

### Remove

The `ResourceReadyEvent` subscription. The provisioner container replaces it in both modes.

### Add

After building `resourceBuilder`:

```csharp
var provisionerResource = new GlitchTipProvisionerResource($"{name}-provisioner", resource);
resource.Provisioner = provisionerResource;

builder.AddResource(provisionerResource)
    .WithImage(GlitchTipProvisionerContainerImageTags.Image, GlitchTipProvisionerContainerImageTags.Tag)
    .WithEnvironment("GLITCHTIP_URL", resource.PrimaryEndpoint)
    .WithEnvironment("GLITCHTIP_ADMIN_EMAIL", adminEmailParam)
    .WithEnvironment("GLITCHTIP_ADMIN_PASSWORD", adminPasswordParam)
    .WithEnvironment("GLITCHTIP_ORG", orgNameParam)
    .WithEnvironment("GLITCHTIP_PROJECT", projectNameParam)
    .WaitFor(resourceBuilder);  // provisioner starts only after GlitchTip is healthy

// Watch provisioner stdout; complete DsnTcs when the marker line arrives.
builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((evt, ct) =>
{
    var logs = evt.Services.GetRequiredService<ResourceLoggerService>();
    var notifications = evt.Services.GetRequiredService<ResourceNotificationService>();
    _ = WatchProvisionerLogsAsync(provisionerResource, logs, notifications, ct);
    return Task.CompletedTask;
});
```

### Log watcher

```csharp
private static async Task WatchProvisionerLogsAsync(
    GlitchTipProvisionerResource provisioner,
    ResourceLoggerService loggerService,
    ResourceNotificationService notificationService,
    CancellationToken ct)
{
    const string Marker = "ASPIRE_GLITCHTIP_DSN=";

    await foreach (var batch in loggerService.WatchAsync(provisioner).WithCancellation(ct))
    {
        foreach (var line in batch.SelectMany(b => b))
        {
            if (!line.Content.StartsWith(Marker, StringComparison.Ordinal)) continue;

            var value = line.Content[Marker.Length..];   // "dsnKey/projectId"
            var slash = value.LastIndexOf('/');
            if (slash < 0) continue;

            provisioner.GlitchTip.DsnKey    = value[..slash];
            provisioner.GlitchTip.ProjectId = value[(slash + 1)..];
            provisioner.DsnTcs.TrySetResult(value);
            return;
        }
    }
}
```

---

## AppHost usage

`WaitForCompletion` on the provisioner is **explicit** — not automatic. The user opts in:

```csharp
var glitchtip = builder.AddGlitchTip("glitchtip", adminEmail, adminPassword)
    .WithPostgres(postgres)
    .WithRedis(redis);

builder.AddProject<Projects.Api>("api")
    .WithReference(glitchtip)
    .WaitForCompletion(glitchtip.Resource.Provisioner);
```

`glitchtip.Resource.Provisioner` is a property on `GlitchTipResource`. No separate builder type or extension method.

---

## Deploy-mode DSN propagation

`WaitForCompletion` translates to the target platform's job-completion dependency:

- Docker Compose → `depends_on: {provisioner}: condition: service_completed_successfully`
- Kubernetes → init container or Job dependency
- Azure Container Apps → ACA Job dependency

The dependency ordering is handled automatically by the platform translator. What is **not** automatic is making the DSN value available to consuming containers as an env var — that value is only known after the provisioner runs.

### The problem

At deploy time, connection strings are written to static configuration (env files, manifests, secrets) before containers start. The GlitchTip DSN is unknown at that point and only becomes known when the provisioner job runs inside the target network. No stack-agnostic native mechanism exists for a job container to write output values that sibling containers then receive as env vars.

### Avenues

**IDeploymentStateManager (re-deploy pattern)**: A pipeline step runs after the deployment, reads the provisioner's log output (e.g. via the platform's log API), saves the DSN to `IDeploymentStateManager`. On next deploy the DSN is in state and is written to static config. First deploy: app containers start without DSN. Documented known limitation.

**IPipelineOutputService / shared output path**: An avenue to investigate. The pipeline has a known output directory per environment. If the provisioner container can write to a bind-mounted path from that directory, and the platform publisher can wire consuming services to read from it at container-start time (e.g., Docker Compose `env_file` is evaluated at start time, after `service_completed_successfully` is met), single-deploy provisioning would be possible. Feasibility depends on whether the output path is accessible inside the target container network and whether the platform supports this pattern natively.

**Platform-native job outputs**: Kubernetes ConfigMaps written by Jobs, ACA Job environment outputs. These are target-specific but clean. Implement per publisher as follow-up.

**Recommendation for v1**: Implement the re-deploy pattern with clear documentation. Pursue the IPipelineOutputService avenue and platform-native options in follow-up work.

---

## Files affected

| File                                                     | Change                                                                            |
| -------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `GlitchTipResource.cs`                                   | Add `Provisioner` property; update `ConnectionStringExpression`                   |
| `GlitchTipBuilderExtensions.cs`                          | Remove `ResourceReadyEvent`; add provisioner resource + log watcher               |
| `GlitchTipProvisioner.cs`                                | Return `(string dsnKey, string projectId)` instead of setting properties directly |
| `GlitchTipProvisionerResource.cs`                        | New file                                                                          |
| `GlitchTipProvisionerContainerImageTags.cs`              | New file                                                                          |
| `GlitchTipConnectionStringReference.cs`                  | New file (or inline as nested class on `GlitchTipResource`)                       |
| `CommunityToolkit.Aspire.Hosting.GlitchTip.Provisioner/` | New console app project                                                           |
| `examples/.../Program.cs`                                | `WaitFor(glitchtip)` → `WaitForCompletion(glitchtip.Resource.Provisioner)`        |
| Tests                                                    | Update call sites                                                                 |
| `api/CommunityToolkit.Aspire.Hosting.GlitchTip.cs`       | Regenerate GenAPI baseline                                                        |

---

## Open questions

1. **Provisioner image publishing**: Where and when is the provisioner image built and pushed? Does it need to be pre-published (like `glitchtip/glitchtip:6`) or built during `aspire publish` (like EF migration bundles)?
2. **`WaitForCompletion` in `WithRedis`/`WithPostgres`**: Should these extension methods internally also wire `WaitFor(provisioner)` on the GlitchTip server, or is the existing `WaitFor(database)` / `WaitFor(redis)` sufficient?
3. **Error propagation**: If the provisioner exits non-zero, `WaitForCompletion(exitCode: 0)` causes the dependent service to fail to start. Should `DsnTcs` be faulted so `ConnectionStringExpression.GetValueAsync` throws a descriptive error instead of hanging?

