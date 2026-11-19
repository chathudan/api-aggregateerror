# 0013 — Playwright for admin UI tests and documentation screenshots

- Status: Accepted
- Date: 2026-11-19
- Deciders: Chathura Wijesinghe

## Context
The admin UI is the product for technicians and QA. Until now nothing tested it: engine, gateway and
admin API tests stop at the HTTP boundary. The guides also need screenshots that stay current as the
UI changes. Both need a real browser driving the real Blazor Server app, with its own JavaScript, its
websocket circuit and the apps behind it.

## Decision
Use **Microsoft.Playwright** (NuGet, test-only) in a new project `tests/ApiAggregator.UI.Tests`.
- A fixture starts the **built** mocks, gateway, admin API and UI as separate processes on free ports,
  over a temporary copy of `samples/config`, so no test changes the repository.
- Tests cover the main journeys (add a service from a spec, build and publish an endpoint, see the logs
  for a test run) and page checks (named controls, one main heading, one primary button, focus, width).
- Browsers are installed once with `scripts/install-playwright.sh`. It uses the Node runtime bundled in
  the package, so it needs no PowerShell or npm. Without browsers, `[UiFact]` tests are **skipped** with
  that instruction, so a plain `dotnet test` still passes.
- `scripts/screenshots.sh` writes `docs/images/*.png` on the usual ports. Plain test runs never write
  into the repository.

> **Note (ADR 0014):** since the merge, the fixture starts three processes (mocks, gateway, admin app) and signs in
> once through the development sign-in.

## Consequences
- The UI's main paths are tested end to end. A UI change that breaks a journey fails the build where
  browsers are installed (recommended in CI).
- `dotnet test` takes about 50 seconds longer when browsers are present.
- **Dependency and supply chain:** Microsoft.Playwright 1.65.1 had no known advisories when it was added.
  It downloads Chromium (about 100 MB) from Microsoft's Playwright CDN at install time. It's used only
  by tests, and nothing ships with the gateway, the admin API or the UI. Upgrade it with the other
  test packages, then run the install script again.
- **Privacy:** the tests use only the invented sample data. Screenshots show sample data only.

## Alternatives considered
- **bUnit component tests:** no real browser, so no circuit or JavaScript and no screenshots.
- **Node Playwright (npm):** the same engine, but it would bring Node and package.json into a .NET-only repo for one task.
- **Selenium:** more setup (drivers per browser) and slower, flakier waits than Playwright's auto-waiting.
