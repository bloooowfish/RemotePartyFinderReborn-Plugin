# Remote Party Finder Reborn - Dalamud Plugin

A Dalamud plugin that collects Party Finder listings from FFXIV and synchronizes them to a remote server.

## Disclaimer

This is a fork of the [original Remote Party Finder project](https://github.com/zeroeightysix/remote-party-finder)'s client part by zeroeightysix.

**Note**: This plugin is a proof of concept created for personal interest. There are no plans for a public release.

## Key Features

- **Real-time Data Collection**: Scans in-game Party Finder listings and transmits them to the server.
- **Crowd Sourcing**: Contributes local PF data to the centralized web view.
- **Leader Info Collection**: Gathers party details for FFLogs integration on the server side.

## Configuration

The plugin is configured to send data to a specific server endpoint. Check `Configuration.cs` or the in-game plugin settings to point it to your local or hosted server instance.

### Tomestone API

Tomestone official API integration is an optional, standalone contributor pipeline owned by the plugin. The Tomestone API key is stored locally in plugin configuration (DPAPI-protected on Windows) and is sent only as an `Authorization: Bearer` token to official `https://tomestone.gg` API requests.

When the Tomestone API contributor is enabled and an API key is configured, the plugin independently leases exact-encounter jobs from `/contribute/tomestone/jobs` and submits authoritative progression results to `/contribute/tomestone/results`. Requests never happen unless the plugin is operational, the contributor option is enabled, an API key is present, the key has not been rejected, and no local rate-limit cooldown is active.

Tomestone failures, missing data, 404s, malformed responses, and `429` cooldowns are completely isolated from the FFLogs contributor pipeline and never block FFLogs result uploads.

### Recommended Worker Timing Presets

The FFLogs worker timing values are available in the plugin debug tab.

These values are provisional starting points (not derived from controlled production benchmarking yet).
Adjust them based on your own measured endpoint behavior and failure patterns.

| Preset | `Base Delay` | `Idle Delay` | `Max Backoff` | `Delay Jitter` |
| --- | ---: | ---: | ---: | ---: |
| Low-load / personal server | 5000 ms | 12000 ms | 60000 ms | 1500 ms |
| Balanced (default) | 5000 ms | 10000 ms | 60000 ms | 2000 ms |
| High-load / API-sensitive | 8000 ms | 15000 ms | 90000 ms | 2500 ms |

- `Base Delay` controls retry backoff base and disabled/misconfigured wait interval.
- `Idle Delay` controls polling interval when no jobs are returned.
- `Max Backoff` caps retry delay after repeated transient failures.
- `Delay Jitter` reduces synchronized spikes when many clients poll together.

Circuit breaker defaults (`Failure Threshold=3`, `Break Duration=1 min`) are a good baseline and should usually be tuned only if your endpoint is frequently rate-limited.

## License

No open-source license has been declared for this repository at this time.

Please read `LEGAL_NOTICE.md` for provenance and policy details.
