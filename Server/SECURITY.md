# Security notes

## Implemented controls

- Identity password hashing, lockout, HttpOnly cookies and Secure cookies outside Development.
- Login requires confirmed email. The default API authorization policy also checks the current user's EmailConfirmed state, including for previously issued sessions. Unconfirmed profiles and photos are excluded from discovery and viewing.
- Credential and resend endpoints limited to 8 requests per IP per minute; Identity GET endpoints have a separate 30-request quota. Messages and photo uploads each limited to 30 requests per authenticated user per minute.
- Email confirmation uses Identity tokens valid for one hour. SMTP uses mandatory TLS and a configured public origin, not a client-controlled Host header. Unknown-account resend responses do not disclose account existence.
- JPEG, PNG and WebP uploads decoded and reencoded as JPEG on the server. EXIF, GPS and other metadata removed; maximum input 5 MiB, 24 million pixels and 12000 pixels on either edge. Output fits inside 1600 by 1600 pixels.
- Only one avatar is retained. Public and private libraries each allow four additional photos. A SQLite transaction serializes quota checks and writes.
- Public coordinates snapped to a 0.005-degree grid (about 550 m north-south and 350-400 m east-west in mainland France). Distances and search inclusion use these approximate coordinates, not exact coordinates; distances rounded to 100 m. The displayed increment is not an accuracy guarantee: the combined position error can reach roughly 700 m in France. This neighborhood-scale precision reveals a smaller area and does not guarantee anonymity. Changing exact coordinates inside the same cell does not change public distance or search inclusion.
- Photo ownership and private album authorization checked on the server. Private grants expire after 30 days.
- API responses marked no-store and nosniff. Authentication failures return HTTP 401/403 instead of redirecting to the app.
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
- Configure trusted reverse proxy forwarding before relying on IP quotas; never trust arbitrary client-provided forwarded headers. Behind an unconfigured proxy, authentication requests share the proxy IP quota.
- Persist and protect Identity Data Protection keys; restrict database and backup file access. Data in SQLite, including exact coordinates and messages, is not application-encrypted or end-to-end encrypted.
- Existing images stored before these changes are not automatically reencoded. Remove or securely reprocess them before public deployment.
- Add abuse reporting, blocking, moderation, account deletion and appropriate privacy/consent settings for sensitive dating data. Confirmed mailboxes are not proof of real identity and do not prevent disposable addresses or automated accounts.
- Review CSRF protections for cookie-authenticated mutations. Upload antiforgery is currently disabled; SameSite=Lax is not a replacement for a full threat-model review.
- Monitor disk usage and traffic, and test backups and restores. Per-process quotas alone do not prevent distributed abuse or unlimited account creation over time.
- Review dependency advisories regularly and plan migration to a supported .NET LTS release.

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