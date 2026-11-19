# Your configuration (real mode)

This folder starts empty. It's where **your** services, sign-ins and published endpoints go when you
run the aggregator against real systems:

```bash
./scripts/dev-run.sh --real
```

| File or folder | Holds |
|---|---|
| `services.json` | Every service: base address, sign-in profile id, endpoints |
| `auth-profiles.json` | How the gateway signs in to each service. Secrets appear only as `secret:<name>` |
| `recipes/<id>.json` | Published aggregated endpoints (what the gateway serves) |
| `drafts/<id>.json` | Work in progress from the admin UI (never served) |
| `history/<id>.v<n>.json` | Every published version, with who and when (ADR 0012) |
| `secrets.local.json` | Development only: secrets typed in the admin UI. Git-ignored, never commit it |

The sample data used by the guides lives in [`samples/config`](../samples/config), and that's what
`./scripts/dev-run.sh` uses by default.
