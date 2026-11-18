# 0004 — Downstream auth as ordered step chains; secrets by reference

- Status: Accepted
- Date: 2026-11-18

## Context
Services authenticate differently: Basic, username/password login returning a session cookie
or a JWT, OAuth client credentials, API keys, the user's own token, and combinations of these.

## Decision
An **auth profile** is an ordered list of `IAuthStep`s applied to each outgoing request. Profiles
attach to a service and can be overridden per endpoint. Six kinds ship: Basic, ApiKey, StaticBearer,
PassThroughUserToken, OAuthClientCredentials, SessionLogin. Secret settings are `secret:<name>`
references resolved by `ISecretResolver` (Key Vault in production). Saving a plain-text secret is
rejected. Sessions and tokens are cached per profile with single-flight sign-in and a retry on 401.

## Consequences
- Any combination works without code changes, and a new kind is one class (skill `add-auth-step`).
- Credentials never appear in config, logs or the UI.
- The caller's token is never forwarded unless a profile explicitly uses PassThroughUserToken.
- Token exchange (RFC 8693) for user-delegated calls is on the roadmap.
