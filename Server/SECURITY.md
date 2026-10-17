# Security notes

## Implemented controls

- Identity password hashing, lockout, HttpOnly cookies and Secure cookies outside Development.
- Login requires confirmed email. The default API authorization policy also checks the current user's EmailConfirmed state, including for previously issued sessions. Unconfirmed profiles and photos are excluded from discovery and viewing.
- Credential and passkey endpoints limited to 8 requests per IP per minute; account-recovery endpoints have a separate 5-request quota and Identity GET endpoints a 30-request quota. Messages and photo uploads each limited to 30 requests per authenticated user per minute.
- Email confirmation and password recovery use Identity tokens valid for one hour. Recovery responses do not disclose account existence; reset codes are single-use. SMTP uses mandatory TLS and a configured public origin, not a client-controlled Host header.
- Passkeys use FIDO2/WebAuthn server-side verification with discoverable credentials and `userVerification=required`. Raw biometric data never reaches the app. Public keys are stored per user; challenge records are random, single-use, expire after five minutes and are held in process memory. Registration requires an authenticated, confirmed-email session.
- JPEG, PNG and WebP uploads decoded and reencoded as JPEG on the server. EXIF, GPS and other metadata removed; maximum input 5 MiB, 24 million pixels and 12000 pixels on either edge. Output fits inside 1600 by 1600 pixels.
- Only one avatar is retained. Public and private libraries each allow four additional photos. A SQLite transaction serializes quota checks and writes.
- Public coordinates snapped to a 0.005-degree grid (about 550 m north-south and 350-400 m east-west in mainland France). Distances and search inclusion use these approximate coordinates, not exact coordinates; distances rounded to 100 m. The displayed increment is not an accuracy guarantee: the combined position error can reach roughly 700 m in France. This neighborhood-scale precision reveals a smaller area and does not guarantee anonymity. Changing exact coordinates inside the same cell does not change public distance or search inclusion.
- Photo ownership and private album authorization checked on the server. Private grants expire after 30 days.
- API responses marked no-store and nosniff. Authentication failures return HTTP 401/403 instead of redirecting to the app.
- Every unsafe `/api` request requires an ASP.NET Core antiforgery request token in the `X-CSRF-TOKEN` header; the Blazor client obtains a fresh token before each mutation. The antiforgery cookie is HttpOnly, SameSite=Strict and Secure outside Development.
- Production startup requires an absolute `DataProtection:KeysPath` and persists the application key ring there. Protect the mounted directory with restrictive permissions and the hosting provider's storage encryption.
- Forwarded `X-Forwarded-For` and `X-Forwarded-Proto` headers are processed only from IPs explicitly configured under `ForwardedHeaders:KnownProxies`; one proxy hop is accepted.
- Self-service account deletion requires the current password, removes the member’s profile, photos, passkeys, photo-access requests and messages in one database transaction, then clears the authentication cookie. Backups remain subject to the operator’s retention and deletion policy.
- Members can block another member or submit one pending report per target; blocking hides both profiles and conversations and prevents messages and photo requests in either direction. Report submissions are limited to five per member per minute.
- Moderation access is restricted to confirmed, non-suspended accounts whose exact email address is listed in `Moderation:ModeratorEmails`. Moderators can review the capped report queue, dismiss reports, suspend accounts, and reinstate suspended members; suspended accounts cannot use protected API features but can still access account deletion.
- Premium subscriptions are created through Stripe Checkout; card details are handled by Stripe and never stored by 2gether. Premium access is granted only by verified, deduplicated Stripe webhooks for a current `active` or `trialing` subscription; stale subscription events cannot overwrite newer state. Webhook signatures are verified, webhook bodies are size-limited, and the webhook endpoint alone is exempt from antiforgery validation.
- Account deletion first expires any reserved Checkout session and cancels any known Stripe subscription; if Stripe cannot confirm the operation, deletion is refused and logged. Stripe credentials are environment-specific secrets, required together in Production, and must use a live API key there.
- SQLite native dependency updated to a version beyond the CVE-2025-6965 affected range.

## Verification

From the repository root:

```powershell
dotnet test Server/Tests/TwoGether.SecurityTests.csproj -c Release
dotnet list Server/Tests/TwoGether.SecurityTests.csproj package --vulnerable --include-transitive
```

The regression test starts a local server on an ephemeral port with a temporary SQLite database and deletes that database after completion. It never uses the normal application database.

## Before public deployment

- Serve only HTTPS. HTTP localhost is for development only.
- Apply schema changes through the checked-in EF Core migrations. Existing databases created before migrations must be backed up, checked against the initial schema, and explicitly baselined with `Scripts/BaselineLegacySqlite.sql`; startup refuses to stamp or overwrite an untracked legacy schema.
- Configure trusted reverse proxy forwarding before relying on IP quotas; never trust arbitrary client-provided forwarded headers. Behind an unconfigured proxy, authentication requests share the proxy IP quota.
- Configure Stripe's monthly EUR Price, customer portal, Automatic Tax and signed webhook endpoint before enabling Premium. Keep test and live credentials separate; validate subscription creation, cancellation, tax settings and webhook delivery in test mode before accepting payments.
- Persist and protect Identity Data Protection keys; restrict database and backup file access. Data in SQLite, including exact coordinates and messages, is not application-encrypted or end-to-end encrypted.
- Existing images stored before these changes are not automatically reencoded. Remove or securely reprocess them before public deployment.
- Configure at least one trusted moderator email and define a review/retention procedure for reports. Confirmed mailboxes are not proof of real identity and do not prevent disposable addresses or automated accounts.
- Keep the antiforgery token header on every new cookie-authenticated mutation; SameSite cookies remain defense in depth, not a replacement for the token.
- Monitor disk usage and traffic, and test backups and restores. Per-process quotas alone do not prevent distributed abuse or unlimited account creation over time.
- Review dependency advisories regularly and keep .NET and its dependencies on supported releases.

These checks address specific regressions, not a complete penetration test or a security certification.

## Email delivery configuration

In Development only, the default configuration writes MIME messages to `Server/App_Data/Mail`. No mail is sent externally. Open the generated `.eml` in a mail reader or read its confirmation link locally. These files contain activation tokens and must remain private; App_Data is excluded from static hosting, publishing and future Git additions. Ignore rules do not remove any files already tracked by Git.

For real delivery configure the following environment variables in the hosting provider's secret/configuration manager:

| Variable | Value |
| --- | --- |
| `Email__Delivery` | `Smtp` |
| `Email__PublicOrigin` | The application's public HTTPS origin, e.g. `https://2gether.example` |
| `Email__FromAddress` | Sender address authorized by the mail provider |
| `Email__Host` | SMTP hostname |
| `Email__Port` | `587` for STARTTLS, or `465` with implicit TLS |
| `Email__ImplicitTls` | `false` for STARTTLS; `true` for implicit TLS |
| `Email__Username` | SMTP username, if required |
| `Email__Password` | SMTP password or provider credential, stored as a secret |

The application refuses to start in Production without a valid HTTPS origin and SMTP configuration. Never disable certificate validation or expose a development mailbox endpoint. Configure sender-domain SPF/DKIM/DMARC with the provider and verify delivery to real mailboxes before public release.

Existing accounts are not marked confirmed automatically: their owners must request a confirmation link from the login form. Inactive accounts may still exist in AspNetUsers, but cannot log in or appear as active profiles. Retention/cleanup of abandoned registrations is a separate policy to define before deployment.

Password recovery uses the Identity `forgotPassword` and `resetPassword` endpoints. In Development, the reset code is written to the same private pickup directory as confirmation mail. Protect those `.eml` files as temporary credentials.

## Passkey deployment requirements

The WebAuthn relying-party ID and permitted origin are derived from `Email:PublicOrigin`. It must exactly match the public browser origin, including a non-default port. Public sites must use HTTPS; only loopback HTTP is allowed in Development. User verification is required, so the device must verify the user locally with a biometric, device PIN, or compatible authenticator.

`PasskeyChallengeStore` is per-process memory, bounded to 4,096 outstanding operations, and operations expire after five minutes. Run one server instance until challenge storage is moved to a shared atomic store; do not assume a load balancer's affinity provides durable multi-instance correctness. Build and publish with `dotnet publish Server/TwoGether.Server.csproj -c Release`; the client static assets are copied into the publish directory.

See the French [user manual](../MANUEL-UTILISATEUR.md) and [installation manual](../MANUEL-INSTALLATION.md) for complete operating instructions.