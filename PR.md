# Add GlitchTip hosting and client integrations

Adds two new packages for [GlitchTip](https://glitchtip.com/), an open-source error monitoring server with a Sentry-compatible API:

- **`CommunityToolkit.Aspire.Hosting.GlitchTip`** — container resource with a built-in provisioner that handles first-run setup and surfaces a Sentry-compatible connection string for consuming services.
- **`CommunityToolkit.Aspire.GlitchTip`** — client integration that bridges the injected connection string into `Sentry:Dsn` in configuration, which the Sentry .NET SDK reads automatically.

The provisioner authenticates with the GlitchTip API and idempotently configures the required resources on each run. The connection string is set once provisioning completes, so `WaitFor(glitchtip)` on a consuming service ensures it is available at startup.

`WithPostgres` and `WithRedis` wire the required backends.

## PR Checklist

- [x] Created a feature/dev branch in your fork (vs. submitting directly from a commit on main)
- [x] Based off latest main branch of toolkit
- [x] PR doesn't include merge commits (always rebase on top of our main, if needed)
- [x] New integration
    - [ ] Docs are written
    - [ ] Added description of major feature to project description for NuGet package (4000 total character limit, so don't push entire description over that)
- [ ] Tests for the changes have been added (for bug fixes / features) (if applicable)
- [x] Contains **NO** breaking changes
- [ ] Every new API (including internal ones) has full XML docs
- [ ] Code follows all style conventions

## Other information

The client package targets `WebApplicationBuilder` directly and calls `builder.WebHost.UseSentry()` with the injected DSN — no manual SDK initialization needed. There is no keyed overload — Sentry middleware is process-wide and does not support multiple DSNs per application.

The provisioner has no integration tests — it requires a live GlitchTip container. Unit tests cover resource structure, parameter defaults, env-var formats (using the `EnvironmentCallbackAnnotation` pattern), and null-argument guards.

v1 runs GlitchTip in `SERVER_ROLE=all_in_one` mode only. Deploy mode is not supported.
