# GlitchTip deploy-mode provisioning — implementation plan

## Goal

Deploy-mode support that is **compute-environment agnostic**: the GlitchTip connection string flows to consumers through standard Aspire parameters on every publisher (Docker Compose, Kubernetes, …), with no publisher-specific artifact manipulation. Provisioning values are supplied either manually (baseline) or by an optional post-deploy pipeline step that persists them to deployment state (auto-convergence on the next deploy).

Run mode is unchanged: `ResourceReadyEvent` → in-process provisioner → `DsnKey`/`ProjectId` on the resource, with `WaitFor(glitchtip)` guaranteeing availability.

See `deploy-design.md` for the analysis and `aspire-issue-draft.md` for the upstream feature request covering the underlying platform gap (single-deploy convergence is impossible today).

---

## High-level design

```
 aspire deploy (any compute environment)
 ─────────────────────────────────────────────────────────────────────────────
 process-parameters       {name}-dsn-key / {name}-project-id resolve from
        │                 Parameters:{name}-* config (deployment state),
        │                 fall back to "" — never prompt on CI
        │
 publish / prepare        publisher materializes the parameters natively:
        │                   compose → ${GLITCHTIP_DSN_KEY} + .env.{env}
        │                   k8s     → values.yaml / Secret
        │
 <compute deploy step>    services start; first deploy: DSN incomplete →
        │                 client package disables Sentry (no crash)
        │
 provision-glitchtip-{name}   [optional step — option B]
        │                 ① poll {provision-url}/api/0/ until 200 (timeout ~5 min)
        │                 ② HTTP from CI: auth, ensure org/team/project, fetch DSN
        │                 ③ save Parameters:{name}-dsn-key / -project-id
        │                    to deployment state (IDeploymentStateManager)
        ▼
 deploy complete          next `aspire deploy`: state → config → parameters
                          resolve up front → consumers get the real DSN
 ─────────────────────────────────────────────────────────────────────────────
```

No artifact is ever patched; no deploy step is re-run. The step writes only to deployment state — the single compute-agnostic channel.

---

## Changes to `GlitchTipResource`

Internal publish-mode parameter slots; connection string branches on their presence:

```csharp
internal ParameterResource? DsnKeyParameter { get; set; }      // publish mode only
internal ParameterResource? ProjectIdParameter { get; set; }   // publish mode only

public ReferenceExpression ConnectionStringExpression =>
    DsnKeyParameter is not null && ProjectIdParameter is not null
        ? ReferenceExpression.Create(
            $"{PrimaryEndpoint.Property(EndpointProperty.Scheme)}://{DsnKeyParameter}@{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}/{ProjectIdParameter}")
        : /* run mode: existing string-based expression (DsnKey / ProjectId) */;
```

Each publisher resolves the endpoint parts and parameters with its own conventions — compose yields `http://${GLITCHTIP_DSN_KEY}@glitchtip:8000/${GLITCHTIP_PROJECT_ID}`, Kubernetes the equivalent via values.yaml. The toolkit never needs to know which.

---

## Changes to `GlitchTipProvisioner`

Extract a transport-only core shared by run mode and the deploy step:

```csharp
internal static async Task<(string DsnKey, string ProjectId)> ProvisionAsync(
    string baseUrl, string email, string password,
    string orgName, string projectName,
    ILogger logger, CancellationToken ct)
```

The run-mode entry point becomes a wrapper resolving endpoint/parameters and assigning the resource properties. `GlitchTipAuthClient`/`GlitchTipApiClient` unchanged.

---

## Changes to `AddGlitchTip` (publish mode only)

```csharp
if (builder.ExecutionContext.IsPublishMode)
{
    // Deployment state loads into configuration at startup, so Parameters:{name}-*
    // resolves automatically once provisioned. Empty fallback ⇒ never prompts.
    var dsnKeyParam = builder.AddParameter($"{name}-dsn-key",
        () => builder.Configuration[$"Parameters:{name}-dsn-key"] ?? "", secret: true).Resource;
    var projectIdParam = builder.AddParameter($"{name}-project-id",
        () => builder.Configuration[$"Parameters:{name}-project-id"] ?? "").Resource;
    resource.DsnKeyParameter = dsnKeyParam;
    resource.ProjectIdParameter = projectIdParam;
}
```

This alone delivers the **manual baseline (option A)**: provision GlitchTip once by hand, set `Parameters__glitchtip-dsn-key` / `Parameters__glitchtip-project-id` in CI configuration, deploy.

### Optional automatic provisioning (option B)

```csharp
    var provisionUrlParam = builder.AddParameter($"{name}-provision-url",
        () => builder.Configuration[$"Parameters:{name}-provision-url"] ?? "").Resource;

    resourceBuilder.WithPipelineStepFactory(
        $"provision-glitchtip-{name}",
        ctx => GlitchTipDeployStep.ExecuteAsync(ctx, resource, provisionUrlParam),
        dependsOn: [WellKnownPipelineSteps.DeployPrereq],
        requiredBy: [WellKnownPipelineSteps.Deploy]);

    // Best-effort ordering after known compute deploy steps; the step's readiness
    // polling makes ordering a nicety, not a correctness requirement.
    resourceBuilder.WithPipelineConfiguration(ctx =>
    {
        var step = ctx.Steps.FirstOrDefault(s => s.Name == $"provision-glitchtip-{name}");
        if (step is null) return;
        foreach (var env in ctx.Model.Resources.OfType<IComputeEnvironmentResource>())
        {
            foreach (var candidate in new[] { $"docker-compose-up-{env.Name}", $"helm-deploy-{env.Name}" })
            {
                if (ctx.Steps.Any(s => s.Name == candidate))
                    step.DependsOn(candidate);   // guarded: unknown names throw
            }
        }
    });
```

Decision pending (open question 2 in the design doc): always-on vs. opt-in via a `WithDeployTimeProvisioning()` extension. Plan assumes opt-in to keep option A users free of the polling timeout.

Pragmas: `ASPIREPIPELINES001` for all pipeline APIs, `ASPIREPIPELINES002` for `IDeploymentStateManager`.

---

## New file: `GlitchTipDeployStep.cs`

1. **Resolve inputs** via `GetValueAsync`: admin email/password, org/project names, provision URL. URL resolution: `{name}-provision-url` if non-empty; else `http://localhost:{port}` when an explicit host port was given to `AddGlitchTip`; else fail fast naming both remedies.
2. **Readiness poll**: `GET {url}/api/0/` until 200, ~5-minute timeout (first boot runs migrations), progress via `ctx.Logger`.
3. **Provision**: `GlitchTipProvisioner.ProvisionAsync(...)` → `(dsnKey, projectId)`; ensure-style calls are idempotent.
4. **Persist**: write to deployment state sections `Parameters:{name}-dsn-key` / `Parameters:{name}-project-id` (`AcquireSectionAsync` → `SetValue` → `SaveSectionAsync` — on the manager, not the section). Skip the save when values are unchanged.
5. **Report**: `ctx.Summary.Add("GlitchTip", ...)` — org/project names only, never the key (secret). When values were newly written, log clearly: *“DSN provisioned and saved; run `aspire deploy` again (or restart consumers) to apply it.”*

Failures throw → pipeline fails with the step's message; re-running is safe.

---

## Changes to `CommunityToolkit.Aspire.GlitchTip` (client package)

Map a DSN without key/project-id (`http://@glitchtip:8000/`) to an empty `Sentry:Dsn` so the SDK is disabled rather than throwing in `SentrySdk.Init`. Required for the first-deploy window in both options A and B.

---

## Explicitly out of scope / removed

- ~~Env-file patching + second `ComposeUpAsync`~~ — compose-specific; violates the agnostic requirement.
- ~~Provisioner sidecar/job container, image publishing, stdout log-marker watching, `WaitForCompletion` contract~~ — removed with the CI-side HTTP model.
- Single-deploy convergence — impossible today without publisher-specific hacks; tracked upstream (`aspire-issue-draft.md`). When Aspire grows a deferred-value mechanism, option B's step becomes its natural producer.
- If option B is dropped entirely (acceptable fallback), everything above minus the step still ships: run-mode auto-provisioning + manual deploy-mode parameters.

---

## Files affected

| File | Change |
| --- | --- |
| `GlitchTipResource.cs` | Internal `DsnKeyParameter`/`ProjectIdParameter`; branch `ConnectionStringExpression` |
| `GlitchTipBuilderExtensions.cs` | Publish-mode parameters; optional `WithDeployTimeProvisioning()` wiring (step factory + guarded ordering) |
| `GlitchTipProvisioner.cs` | Extract `(dsnKey, projectId)` core overload; keep run-mode wrapper |
| `GlitchTipDeployStep.cs` | New — readiness poll, HTTP provisioning, deployment-state save |
| `CommunityToolkit.Aspire.GlitchTip` | Key-less DSN → disabled Sentry |
| `README.md` | Deploy docs: parameter contract, manual flow, optional auto-provisioning, two-deploy convergence note |
| Tests | Connection-string branching, parameter round-trip, step guards/idempotency, client DSN guard |
| `api/*.cs` GenAPI baselines | Regenerate if `WithDeployTimeProvisioning()` is added (other additions are internal) |

---

## Resolved decisions (as implemented)

1. **Endpoint exposure**: `WithDeployTimeProvisioning()` marks the http endpoint external (it needs the published port for automatic URL resolution, and operators need the UI). Option A leaves exposure to the user.
2. **Opt-in**: option B ships as `WithDeployTimeProvisioning()`, a no-op in run mode. Option A users never pay the polling timeout.
3. **Ordering**: only `docker-compose-up-{env}` gets a guarded `DependsOn` edge; no `helm-deploy-{env}` name guessing. Non-compose targets rely on the step's readiness polling and must set `{name}-provision-url`.
4. **Endpoint auto-resolution** (compose): the step reads `ProjectName`/`OutputPath` from the `DockerCompose:{env}` state section and queries `IContainerRuntime.ComposeListServicesAsync` for the published port of the GlitchTip service — the same primitive Aspire's own `PrintEndpointsAsync` uses — yielding `http://localhost:{publishedPort}` with `{name}-provision-url` as the override.
