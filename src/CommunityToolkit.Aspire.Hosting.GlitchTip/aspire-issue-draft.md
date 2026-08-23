### Is there an existing issue for this?

- [x] I have searched the existing issues

### Is your feature request related to a problem? Please describe the problem.

Many self-hostable services require **post-start bootstrap that produces values their consumers need**: the value does not exist until the service's container is running and an API call has been made against it. Examples:

- **GlitchTip / self-hosted Sentry** — create org/team/project, obtain the **DSN** that client apps need in their `Sentry:Dsn` config (my concrete use case, in a Community Toolkit integration).
- **Keycloak** — create a realm and clients, obtain **client secrets**.
- **Grafana** — create a service account, obtain an **API token**.
- **MinIO / RabbitMQ / Vault** — create users/buckets/vhosts, obtain **access keys / tokens**.

In **run mode** this composes beautifully with existing primitives: subscribe to `ResourceReadyEvent`, call the service's HTTP API in-process, set properties consumed by a lazily-evaluated `ConnectionStringExpression`, and have consumers use `WithReference(...)` + `WaitFor(...)`. The DSN/secret flows to consumers with no extra concepts.

In **publish/deploy mode** there appears to be no equivalent, regardless of compute environment:

1. Every publisher resolves all values at **artifact-generation time** — Docker Compose writes parameter values into `.env.{env}` during `prepare-{env}`; the Kubernetes publisher resolves parameters into Helm `values.yaml` during its prepare step. But the value I need only exists _after_ the compute deploy step has run and the service is up.
2. The pipeline never runs the app model, so `ResourceReadyEvent` never fires and connection string expressions are not lazily awaited per consumer as in run mode.
3. `WaitFor` translates to startup _ordering_ only (e.g. compose `depends_on`); it cannot carry a value.
4. There is no feedback path from "deployed and provisioned" back into dependent resources' configuration **within the same pipeline run**.

The closest workaround is compute-agnostic but takes two deploys: build the connection string from parameters, run a custom pipeline step after deploy that provisions the service over HTTP and saves the values to `IDeploymentStateManager` under `Parameters:{name}`; deployment state is loaded into configuration at startup, so the values materialize on the **next** `aspire deploy`. Single-deploy convergence is only achievable with publisher-specific hacks (patching the generated `.env` file and invoking `ComposeUpAsync` a second time so changed services are recreated), which do not generalize to Kubernetes or other compute environments and depend on internals like step naming and artifact layout.

### Describe the solution you'd like

A first-class, compute-agnostic concept for **deploy-time provisioned values**: values that are declared at model-build time, produced by integration code at a defined point during deployment (after the producing resource's compute is up), and materialized into dependent resources' configuration by each publisher using its native late-binding mechanism.

Sketch of the shape this could take (names illustrative):

```csharp
var dsn = builder.AddDeferredValue("glitchtip-dsn", secret: true);

var glitchtip = builder.AddGlitchTip("glitchtip")
    .WithProvisioner(async (ctx, ct) =>
    {
        // Runs during deploy, after this resource's compute environment reports it deployed.
        // ctx exposes a resolved, reachable endpoint for the resource.
        var result = await ProvisionAsync(ctx.Endpoint, ct);
        dsn.SetValue(result.Dsn);
    });

builder.AddProject<Projects.Api>("api")
    .WithReference(glitchtip)   // ConnectionStringExpression includes the deferred value
    .WaitFor(glitchtip);
```

Key properties:

1. **Publisher-native materialization.** Docker Compose emits an env-file placeholder; Kubernetes emits a Secret; Azure targets emit their secret/config primitive. Integration authors never touch publisher artifacts.
2. **A defined pipeline phase** ("provision-after-compute", between the compute deploy steps and `Deploy`) where producer callbacks run, with a documented contract for how dependents receive the value in the same deploy — whether that's ordering dependent compute after producers, or a targeted reconcile/restart of dependents whose deferred inputs changed.
3. **Persistence semantics**: produced values are saved to deployment state automatically so subsequent deploys resolve them up front (today's parameter/state behavior, but as a supported contract rather than an emergent one).
4. **Run-mode parity**: in run mode the same provisioner callback could be driven by the existing eventing (`ResourceReadyEvent`), so one code path serves both modes.

Alternatives considered:

- **Two-deploy state pattern** (described above): works on every publisher today, but first-deploy UX is "deploy, then deploy again", and it relies on the undocumented `Parameters:{name}` state→configuration round-trip.
- **Artifact patching + re-running the deploy step**: single-deploy, but inherently publisher-specific and fragile (internal step names, artifact formats).
- **Provisioner job/sidecar containers** in the target network: solves _where the code runs_, but there is still no portable channel for getting the job's output into sibling resources' env, and it adds an image-publishing burden on integration authors.

### Additional context

Concrete motivating implementation: a GlitchTip hosting integration for the Aspire Community Toolkit (`CommunityToolkit.Aspire.Hosting.GlitchTip`). Run-mode provisioning (org/team/project + DSN via `ResourceReadyEvent`) works exactly as desired with `WaitFor`; deploy mode currently has no path that doesn't either bake in a specific compute environment or require a second deploy. Happy to share the full design analysis and to validate an API proposal against this use case.
