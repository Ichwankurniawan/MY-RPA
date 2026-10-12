# ADR-0051: Roles, access tokens, Runner identities and the Runner listener

- Status: **Accepted** (owner, 2026-10-12: "Roles + access tokens (Recommended)", "Separate HTTPS runner port
  (Recommended)").
- Phase: 10. Related: ADR-0025 (local-mode security), ADR-0049 (orchestrator).
- Amends ADR-0025:
  - the Server may open a second, non-loopback listener;
  - requests may carry bearer tokens for `/api/orchestrator/*` and `/runner/v1/*`.

## Context

Local mode (ADR-0025) has one user, the person who opened the printed start link, and listens only on loopback. Runners
on other machines must reach the Server. Tools need least-privilege access. Neither may weaken the browser session's
protections (Host check, Origin and anti-forgery checks, CSP).

A full identity system (user accounts, organizations, SSO) is Phase 11 (PRD 11.1) and is not built here.

## Decision

1. **Roles, ordered:**
   - `Viewer` reads;
   - `Operator` also submits, cancels and retries jobs and enables or disables schedules;
   - `Developer` also publishes, deploys, rolls back, configures and manages schedules;
   - `Admin` also manages Runners, tokens and policy, and reads the audit history and diagnostics.

   The orchestrator checks the role of every change. Endpoints check it for administrative reads. Job arguments and
   outputs are returned to Operators and above only.
2. **Actors:**
   - **The Studio session** from the start link is the local owner, `Admin`.
   - **Access tokens** (`myrpa_token_<id>.<secret>`) are created and revoked by an Admin, each with one role.
   - **Runner tokens** (`myrpa_runner_<id>.<secret>`) belong to exactly one Runner and authenticate only the Runner
     protocol.

   Secrets are 256 random bits. Only their SHA-256 is stored, compared in constant time. A token is shown once, never
   logged, and never returned by any API afterwards. A Runner token can be rotated (the old one stops working) or
   revoked (the Runner stops with exit code 3).
3. **Listeners:**
   - **The Studio listener** stays loopback-only with ADR-0025's rules. In addition:
     - `/api/orchestrator/*` also accepts a bearer access token, in which case cookie, Origin and anti-forgery checks do
       not apply (browsers never send bearer tokens on their own);
     - `/runner/v1/*` accepts Runner tokens, so a Runner on the same machine needs no second listener.
   - **The Runner listener** (`--runner-url`) serves only `/runner/v1/*` (Runner tokens) and `/api/orchestrator/*`
     (access tokens). Everything else is not found: no Studio files, no cookies.
     - It must use https with `--runner-cert <pfx>` (password in `MYRPA_RUNNER_CERT_PASSWORD`). Plain http is accepted
       only on a loopback address.
     - Request bodies are capped (5 MB). Runner messages must be JSON and carry the protocol version header.
4. **Runners validate the Server**:
   - with normal TLS validation;
   - or, for a self-signed Server certificate, with exactly the pinned SHA-256 (`serverCertificateSha256`).

   Validation is never switched off. A Runner configuration refuses plain http to a non-loopback Server, and refuses a
   token that is not a Runner token.
5. **Administrative changes are audited**, recording actor, action, target and details (never secrets). This covers
   Runners, tokens, policy, publish, deploy, rollback, enable/disable, configuration and schedules.

## Consequences

- Least-privilege API access exists now, without user accounts. The Administration page says so.
- Remote management works through the Runner listener with access tokens. The Studio UI itself is not served there.
- **Token provisioning is manual:** an Admin creates a Runner, copies the token once and stores it on the Runner machine
  in a file with restricted permissions or in an environment variable. Rotation is a button.
- **Not in Phase 10:** user accounts, login, SSO and organizations (Phase 11).
