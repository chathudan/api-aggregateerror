---
name: add-auth-step
description: Add a new downstream authentication kind (an IAuthStep) to the API Aggregator engine, e.g. mTLS, HMAC request signing, or token exchange. Use when a service needs a sign-in method the six built-in kinds don't cover.
---

# Add a downstream auth step

Built-in kinds: Basic, ApiKey, StaticBearer, PassThroughUserToken, OAuthClientCredentials,
SessionLogin. Check first that a combination of these doesn't already cover the need: profiles chain steps.

## Steps

1. **Create the class** in `src/ApiAggregator.Engine/Auth/Steps/<Kind>Step.cs`:
   - `public sealed class XStep : IAuthStep`
   - `Kind` = the exact string used in `auth-profiles.json`
   - `RequiredSettings` = settings that must be present (checked when a profile is saved)
   - `ApplyAsync(AuthStepContext c, CancellationToken ct)` changes `c.Request` only
   - Read settings with `await c.SettingAsync("name", ct)` (this resolves `secret:` references) or `c.Plain("name")` for non-secret flags
   - Anything fetched (token, session) goes through `c.Cache.GetOrCreateAsync(c.CacheKey, …)` with an `ExpiresAt`, which gives single-flight behaviour
   - Make outbound calls with `c.HttpClientFactory.CreateClient(EngineHttpClients.Auth)`
   - Throw `AuthFailedException` with a short, secret-free message on failure
2. **Register it** in `ServiceCollectionExtensions.AddAggregatorEngine`: `services.AddSingleton<IAuthStep, XStep>();`
3. **Secret detection:** if a new setting name holds a secret but doesn't contain "password" or "secret", or equal "token" or "value", extend `DownstreamAuthenticator.LooksLikeSecretName`.
4. **401 retry:** if the step caches credentials, add its kind to `CompositionExecutor.HasCachedCredentials`.
5. **Tests:** add a test that applies the step to an `HttpRequestMessage` and asserts the headers, plus one for the missing-setting error.
6. **Docs:** add a row to the auth table in `docs/architecture/03-auth-and-security.md`. If it changes the trust model (for example forwarding user identity), write an ADR (`write-adr` skill).
7. **Mock (optional):** add a matching endpoint to `samples/MockServices/Program.cs` and a profile to `config/auth-profiles.json`.

## Rules

- Never log the request headers or any secret value.
- Never cache per-user credentials in the shared credential cache.
- Don't follow redirects or set cookies on a shared jar. The handlers are already configured; don't create your own HttpClient.
