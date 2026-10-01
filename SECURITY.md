# Security

This server talks **directly to a production Altium Workspace**: it reads and writes components,
models, folders and templates with the rights of the signed-in account. Treat it like any tool that
holds those rights.

## What protects your data

* **Write modes** (`ALTIUM_WRITE_MODE`): `readonly`, `guarded` (default) and `unguarded`. In `guarded`
  mode an edit of more than `ALTIUM_CONFIRM_THRESHOLD` objects first returns a preview and a
  confirmation token and writes nothing; bulk work goes through a plan that a person approves once
  (`vault_plan`).
* **Dry runs**: every write tool accepts `dryRun=true` — everything is read for real, nothing is written.
* **Deletion guard**: a model, folder, template or component type that live parts still use cannot be
  deleted, with or without `force`.
* **Audit log**: every write is recorded with the values before the change (`vault_audit`).
* **Revisions**: Altium keeps every released revision; an edit is always a new revision.

## Credentials

* On Windows the server signs in with the current Windows account (NTLM); no password is stored.
* With `ALTIUM_AUTH=token` the user signs in in the browser; the server keeps tokens in memory, or on
  disk only when `ALTIUM_TOKEN_STORE=file` is set (protected with DPAPI on Windows, mode 600 elsewhere).
  The server never asks for a password and never accepts one from a chat.
* The OAuth client identifier (`ALTIUM_OAUTH_CLIENT_ID`) is chosen by the user; none is built in.
* `ALTIUM_SOAP_TRACE` writes raw SOAP messages, **including the session identifier**, to a folder.
  Use it only for diagnostics, never commit or share that folder.

## Reporting a vulnerability

Please open a private security advisory on GitHub (Security → Report a vulnerability) instead of a
public issue. Include the version (`vault_status`), the server type (On-Prem or Altium 365) and the
steps to reproduce.
