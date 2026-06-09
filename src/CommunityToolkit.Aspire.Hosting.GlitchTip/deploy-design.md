# GlitchTip Deploy-Mode Provisioning — Scenario Analysis

## Problem statement

`aspire deploy` must get a GlitchTip DSN — which only exists after the GlitchTip server is running and an org/team/project has been created — into the configuration of consuming app services.

Hard requirements:

1. **Compute-environment agnostic.** The design must work for Docker Compose, Kubernetes, and future compute environments. It must not depend on publisher-specific artifacts (patching `.env` files, rewriting Helm values, re-running `docker compose up`).
2. The deploy (CI) machine has HTTP access to the published GlitchTip endpoint (no firewall restrictions).
3. Run mode keeps the existing in-process provisioning (`ResourceReadyEvent` → `GlitchTipProvisioner` → `DsnKey`/`ProjectId`), which works well with `WaitFor(glitchtip)`.

## The fundamental gap (verified against Aspire source)

There is **no Aspire-native way** to run a resource, provision it during deployment, and feed the resulting value into sibling resources' environment using only `WaitFor`:

- Every publisher resolves all values at **artifact-generation time**: Docker Compose writes parameter values to `.env.{env}` in `prepare-{env}` (`DockerComposeEnvironmentResource.PrepareAsync`); Kubernetes resolves parameters into Helm `values.yaml` during its prepare step (`KubernetesResource` — "Parameter values resolved during the prepare step, replacing the placeholder in values.yaml").
- The deploy pipeline never runs the app model, so `ResourceReadyEvent` never fires and `ConnectionStringExpression` is never lazily awaited the way it is in run mode.
- `WaitFor` translates to startup *ordering* only (compose `depends_on: service_started`; the `service_healthy` condition is commented out in `DockerComposeServiceResource.SetDependsOn`). It cannot carry a value.
- There is no post-deploy feedback loop: nothing reads state from the deployed application back into consumer configuration within the same pipeline run.

This is a general Aspire limitation, not a GlitchTip quirk — it affects any service requiring post-start bootstrap that yields credentials/identifiers (Keycloak client secrets, Grafana API tokens, MinIO access keys, Vault tokens). It is captured as an upstream feature request: see `aspire-issue-draft.md`.

## The one compute-agnostic channel: parameters + deployment state

What *is* portable across publishers, verified end to end:

1. A `ParameterResource` referenced from `ConnectionStringExpression` is materialized natively by every publisher (compose → `${GLITCHTIP_DSN_KEY}` placeholder + env file; k8s → `values.yaml` entry/Secret).
2. Deployment state is loaded into `IConfiguration` at AppHost startup (`DistributedApplicationBuilder.LoadDeploymentState` → `AddJsonFile`), so a value stored under `Parameters:{name}` resolves the parameter on the **next** pipeline run, regardless of compute environment.
3. `IDeploymentStateManager` is writable from any pipeline step (`AcquireSectionAsync` → `SetValue` → `SaveSectionAsync`).

The corollary: a value computed *during* deploy can only take effect at the **next** artifact generation. Single-deploy convergence is impossible today without publisher-specific hacks (env patching + re-up was the rejected compose-only variant of this design).

## Options

| # | Option | Compute-agnostic | First-deploy UX | Verdict |
|---|---|---|---|---|
| A | **Parameters only; manual provisioning.** Publish-mode connection string is built from `{name}-dsn-key` / `{name}-project-id` parameters. The operator provisions GlitchTip once (UI or script), sets the two values in CI config (`Parameters__glitchtip-dsn-key=...`), redeploys | ✅ | Sentry disabled until values supplied | ✅ **v1 baseline** — zero pipeline code, works everywhere |
| B | **A + post-deploy provisioning step.** A step ordered after compute deploy polls the GlitchTip URL from CI, runs the existing HTTP provisioning flow, and saves the values to deployment state under `Parameters:{name}-*`. Values flow into artifacts on the next deploy automatically | ✅ (step touches only HTTP + deployment state) | Sentry disabled on first deploy; auto-converges on second | ✅ **v1 recommended add-on** — strictly additive over A, severable if unwanted |
| C | Patch publisher artifacts in-pipeline and re-trigger the deploy step (env-file rewrite + second `ComposeUpAsync`) | ❌ compose-only | Single-deploy convergence | ❌ Rejected: violates requirement 1 |
| D | Provisioner job/sidecar container inside the target network | ❌ per-target output plumbing | — | ❌ Rejected earlier: image publishing burden, no portable output channel |
| E | Wait for upstream Aspire support (deferred/pipeline-produced values) | ✅ | Single-deploy | File the issue; adopt when it exists |

**Recommendation: A + B now, E later.** If B proves troublesome, dropping it leaves A fully functional (per the explicit fallback decision: deploy-mode auto-provisioning is droppable; run mode keeps it).

### Why option B stays agnostic

- It needs an externally reachable URL — supplied by the `{name}-provision-url` parameter (required for k8s/remote docker; defaults to `http://localhost:{port}` when an explicit host port was configured, matching the local-compose deploy story).
- Ordering "after compute is deployed" has no portable hook (compose's up step is tagged `docker-compose-up`; k8s uses `helm-deploy-{env}` names; no shared `DeployCompute` tag in practice). The step therefore: adds guarded `DependsOn` edges for *known* step names when present (`docker-compose-up-{env}`), and otherwise relies on its own readiness polling — it waits for `GET /api/0/` to return 200 with a generous timeout, so even running concurrently with the deploy step it converges. Polling is required anyway (first boot runs Django migrations).
- It writes **only** to deployment state. No artifact is touched; each publisher's own prepare step materializes the values next run.

### First-deploy behavior (both A and B)

The DSN parameters use the `AddParameter(name, Func<string>)` overload reading `Parameters:{name}` from configuration with an empty-string fallback — they never prompt (headless CI safe) and resolve from deployment state once present. On the first deploy consumers receive an incomplete DSN (`http://@glitchtip:8000/`). The companion client package `CommunityToolkit.Aspire.GlitchTip` must map a key-less DSN to an empty `Sentry:Dsn` (SDK disabled) instead of passing it to `SentrySdk.Init`, which throws on malformed DSNs. Apps run normally; they just don't report errors until the DSN lands.

## Constraint catalog (verified)

| Constraint | Source |
|---|---|
| Publishers resolve all values at artifact-generation time; no post-deploy feedback into config | `DockerComposeEnvironmentResource.PrepareAsync`, `KubernetesResource` values.yaml handling |
| `WaitFor` → ordering only; no value channel; compose emits `service_started` (healthy commented out) | `DockerComposeServiceResource.SetDependsOn` |
| Parameters materialize natively per publisher; param config key convention `Parameters:{name}` (`ConfigurationKey` is internal) | `DockerComposeServiceExtensions.AsEnvironmentPlaceholder`, `ParameterResource` |
| Deployment state JSON loads into `IConfiguration` at startup → params round-trip across runs | `DistributedApplicationBuilder.LoadDeploymentState` |
| Empty parameter values are not persisted by the parameter processor (no state poisoning) | `ParameterProcessor.SaveParametersToDeploymentStateAsync` |
| No portable "after compute deploy" ordering hook (publisher-specific step names/tags) | `DockerComposeEnvironmentResource` (`docker-compose-up-{env}`), `KubernetesEnvironmentResource` (`helm-deploy-{env}`) |
| Pipeline APIs need `#pragma warning disable ASPIREPIPELINES001` (state: `002`) | Pipeline API annotations |

## Open questions

1. Endpoint exposure: provisioning over HTTP requires a published/reachable endpoint per target (compose host port, k8s ingress). Auto-mark external in publish mode, or document + fail fast?
2. Should option B be opt-in (`WithDeployTimeProvisioning()`) rather than default, so users who provision out-of-band never pay the polling timeout on deploys where GlitchTip is unreachable from CI?
3. DSN rotation: B self-heals (always re-runs the idempotent ensure flow and updates state on drift) — but rotated values again take one extra deploy to land. Acceptable?
