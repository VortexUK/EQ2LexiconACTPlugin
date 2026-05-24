# Security Policy

## Reporting a vulnerability

**Please do not report security issues via public GitHub issues.**

If you've found a security issue in this plugin, report it privately via [GitHub Security Advisories](https://github.com/VortexUK/EQ2LexiconACTPlugin/security/advisories/new) on this repo. That creates a private channel between you and the maintainer to coordinate a fix before public disclosure.

You should expect an initial response within 7 days. If the issue is confirmed, a fix typically lands in a patch release within 2 weeks.

## Threat model

This plugin runs as a .NET 4.8 DLL loaded into the user's local ACT process at the user's Windows privilege level. We don't try to defend against an attacker who already has code execution on the user's machine — that's already game over.

The most sensitive asset is the user's **API token**, which grants upload + delete access to their own parses on the EQ2 Lexicon site. Treat it as a password. The plugin:

- Stores it DPAPI-encrypted at rest (`CurrentUser` scope), with a `DPAPI:` prefix in the config XML. Legacy plaintext from v0.1.0–v0.1.4 still loads but gets re-wrapped on next save.
- Sends it only as a `Bearer` header to the configured `ServerUrl`, which `UploadClient.ValidateServerUrl` constrains to `https://` (or `http://` to `localhost` / `127.0.0.1` / `[::1]` for dev).
- Never echoes it in status labels, error messages, log lines, or the "Show payload" dialog.

The server response is parsed by a deliberately narrow hand-rolled `ExtractJsonString` (not a full JSON parser). It's bounded by the response body size and only reads the single `detail` / `status` / `discord_name` string fields.

## In scope

- API token exfiltration paths in plugin code
- TLS / certificate validation bypasses
- Injection paths through the payload-building or response-parsing code
- Privilege escalation beyond the user's normal Windows context
- Disclosure of unsanitised user data in upload payloads

## Out of scope

- Anything that requires existing code execution on the user's machine
- Vulnerabilities in ACT itself (report those to [EQAditu/AdvancedCombatTracker](https://github.com/EQAditu/AdvancedCombatTracker))
- Vulnerabilities in the EQ2 Lexicon server (those belong in [VortexUK/EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon))
- SmartScreen warnings on first install (the DLL is unsigned by design)

## Past security audits

- **v0.1.4** ([9eb39e0](https://github.com/VortexUK/EQ2LexiconACTPlugin/commit/9eb39e0)) — initial hardening pass: HTTPS-only enforcement, response-size bounds, status-message sanitisation.
- **v0.1.5** ([5f9e11a](https://github.com/VortexUK/EQ2LexiconACTPlugin/commit/5f9e11a)) — DPAPI wrapping for the API token at rest.
