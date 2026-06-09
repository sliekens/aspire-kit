# CommunityToolkit.Aspire.GlitchTip

Provides Aspire client integration for [GlitchTip](https://glitchtip.com/), an open-source error monitoring server compatible with the Sentry SDK.

## Getting started

### Install the package

```dotnetcli
dotnet add package CommunityToolkit.Aspire.GlitchTip
```

## Usage

In your service's `Program.cs`, `AddGlitchTipClient` reads the Aspire-injected DSN and initializes the Sentry SDK:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddGlitchTipClient("glitchtip");

// ...
```

## Configuration

Settings can be overridden via the `Aspire:GlitchTip` configuration section (or `Aspire:GlitchTip:{name}` for named connections):

```json
{
  "Aspire": {
    "GlitchTip": {
      "Dsn": "http://key@host:port/1",
      "DisableHealthChecks": false
    }
  }
}
```

| Property | Default | Description |
|---|---|---|
| `Dsn` | from connection string | Sentry-compatible DSN |
| `DisableHealthChecks` | `false` | Disables the health check for the GlitchTip endpoint |

