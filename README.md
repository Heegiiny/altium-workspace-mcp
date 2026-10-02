# Altium Vault MCP

An MCP server with direct access to an Altium Workspace vault: navigation, search,
batch editing of component parameters and organizing the folder structure.

It works **without Altium Designer installed**. All communication with the server goes
over SOAP and through the standard vault script mechanism, with the same calls that
Altium itself uses.

This is an unofficial project: it is not affiliated with, endorsed or supported by Altium.
"Altium" is a trademark of its owner.

Works best with https://github.com/embedded-society/altium-designer-mcp. This MCP serves the Vault path and altium-designer-mcp is used to create and edit the parts.

## What it can do

| Tool | Purpose |
|---|---|
| `vault_status` | Server version, connection, sign-in method and state, write mode, vault size |
| `vault_folders` | A compact folder tree as lines (`under`, `depth`, `nameContains`; GUIDs, system folders and templates — on request; without parameters — the top levels, `[+N]` — folders hidden by depth). A folder address: a path, a path ending, a name or a GUID; when reading, an inexact address is forgiven and the response has `resolvedFolder` |
| `vault_folder_types` | Folder types |
| `vault_components` | **An analog of the Components panel** on the Workspace search index: `types` — the type tree with part counts, `facets` — type parameters and their values, `overview` — a summary by type: part count, folders, gaps (no MPN, LCSC Part#, footprint), parameter fill rates with the top 5 values (without `type` — by top types), up to 8 000 characters; `list` — parts as a table by filters (`Value=10k`, `Voltage Rating>=16`), text, folder (`folder`) and footprint (`footprint`), with a footprint column; `compact=true` leaves only `hrid` and the requested columns in a row — more rows per call; fast over the whole vault |
| `vault_table` | **Components as a table** — columns and rows, an analog of Batch Edit. 100 rows per call by default; a long selection is paged with `offset`/`nextOffset` |
| `vault_component_detail` | A card: revisions, models, where used |
| `vault_revision_parameters` | Parameters of earlier revisions |
| `vault_content_types` | Content types (components, symbols, footprints, templates) |
| `vault_component_types` | **Component types**: the tree, create, move, delete (irreversible; refused if the type has parts or templates), assign |
| `vault_templates` / `vault_template` | Component templates: a list as a compact line (type, symbol, footprint), creation from a sample (`create basedOn`), editing — name, description, parameters, type, folder, default symbol and footprint (the `.cmpt` file), a check of references to deleted or missing symbols and footprints (`action=check`). The general table edit and copying (`vault_copy_components`) reject templates |
| `vault_table_write` | **Writing the table back** |
| `vault_update_parameters` | Pinpoint parameter edit |
| `vault_set_links` | Relinking a symbol, footprint, template; several "parts → model" groups in one call and in a batch; a template is a link target but not an editable part. **Several footprints on a part** (the Normal / Least / Most variants): the role `footprints` with a `targets` list — the whole set, the first is primary; `footprint` (one target) changes only the primary, an empty target on it with several footprints is refused with a hint about `footprints` |
| `vault_repair_links` | Restoring lost links from the revision history (templates in the list are refused) |
| `vault_restore_from_revision` | Returning values from an earlier revision |
| `vault_copy_components` | Copies of a sample, including a series of values; each copy is one atomic operation (a revision, datasheet links and type at once), the differences (`comment`, `parameters`, `links`, `componentType`) are passed in the same call; component templates cannot be copied — the copy would have no `.cmpt` file; a copy with an already existing MPN or LCSC Part# is rejected (`duplicates`), including a match with another copy of the same call, until `allowDuplicates` is set |
| `vault_move_items` | Moving items; datasheets move along. A layout over several folders in one call — `moves[]` = `{items[], targetFolder}`; those already in place — `skipped` |
| `vault_delete_items` | Moving to the trash; a reference from the active revision of a live part or the default symbol/footprint of a template cannot be deleted, with or without `force` |
| `vault_restore_items` | The trash: `list` (what is there, the path before deletion, items and folders are paged separately — `offset`/`limit` and `foldersOffset`/`limit`, the response always fits the size budget — items come first, folders get the rest (if they did not fit, they are paged separately by `foldersOffset`); `deletedAt` — the deletion time of the nearest deleted ancestor folder if the item went to the trash with it; selection by this time `deletedAfter`/`deletedBefore` and by content type `contentTypes`, who references — live parts and templates separately; usage — by a targeted batch whose cost follows the number of checked items) and `restore` (items and/or folders with contents) |
| `vault_folder` | A folder: create (including a list `paths[]` — parents before children, existing ones are skipped), rename, move, delete, assign a template; `repair_system` brings the Datasheets system folders to the Altium form. `delete` counts the subtree and contents, refuses even with `force` on an external reference from a live part or template |
| `vault_check_components` | **Checking and fixing the Altium format**: parameter numbers, links, cleared values, parts without a component type (the type is assigned by a tag, without a new revision), a reference to a model in the trash, footprint set violations — two primaries, a repeated number (only reports, does not fix) |
| `vault_cleanup_parameters` | **Bulk removal of parameters by a rule** `keep`/`drop` — not clearing a value but deleting the key |
| `vault_model_files` | **Model files**: export of `.SchLib`/`.PcbLib` for altium-designer-mcp, upload of edited ones, **creating a new model from a file (`create`)** and moving components to a new revision |
| `vault_apply_status` | Deferred bulk edits of all write tools and their tokens (guarded mode); the active plan |
| `vault_plan` | **One owner confirmation for the whole bulk job**: `create`/`approve`/`show`/`close` |
| `vault_session` | Show (`show`, also `status`), recreate (`reset`) or close (`close`) the session; with token login — `login`/`login_status` (the browser link and waiting for the login) |
| `vault_audit` | The change log with earlier values (`count`, `offset`/`nextOffset`, `plan`) |

`vault_table` + `vault_table_write` replace the chain "export Batch Edit to Excel —
process — paste back": the selection arrives as a ready table, the changes go
back in one call. A batch of edits of any size fits two calls.

Every write tool has `dryRun`: everything is really read from the server, while the
changes are collected and not sent. The response contains the result that a real call
would return, the list of reads (what was read, with which filter, sample records)
and what would go to the server — down to the parameters of the new revision with their numbers and
the links with vault GUIDs.

## Connecting

### Build

A double click on `publish.cmd` in the repository root (or `.\publish.ps1` from PowerShell)
builds the server into a permanent directory:

```
<repository>\Release\altium-vault-mcp.exe
```

This path is what goes into all clients. The .NET 10 Runtime is required.
A running server holds its files, so before rebuilding close
Claude Desktop (from the tray, not just the window) and Claude Code started from `Release` —
the script will remind you if the server is still running from there.

Check the connection to the vault before connecting a client:

```powershell
<repository>\Release\altium-vault-mcp.exe probe
```

### Claude Desktop

1. Open Claude Desktop → **Settings → Developer → Edit Config**. The file
   `%APPDATA%\Claude\claude_desktop_config.json` opens.
2. Add the server to the `mcpServers` section (backslashes are doubled in JSON):

   ```json
   {
     "mcpServers": {
       "altium-vault": {
         "command": "C:\\path\\to\\repository\\Release\\altium-vault-mcp.exe",
         "args": [],
         "env": {
           "ALTIUM_BASE_URL": "http://vault.example.local:9780",
           "ALTIUM_EXCHANGE_DIR": "D:\\AltiumExchange"
         }
       }
     }
   }
   ```

   If the file already has other servers or settings, add only the
   `"altium-vault"` block inside the existing `mcpServers`. `ALTIUM_BASE_URL` can be
   omitted if the variable is set in the system.
3. Fully restart Claude Desktop: the tray icon → **Quit**, then start it
   again. The `vault_*` tools appear in the chat tool menu.

The login uses the Windows account, no password is needed. If the server did not
appear, the reason will be in the log `%APPDATA%\Claude\logs\mcp-server-altium-vault.log`.

### Claude Code

```powershell
claude mcp add altium-vault --scope user --env ALTIUM_BASE_URL=http://vault.example.local:9780 -- C:\path\to\repository\Release\altium-vault-mcp.exe
```

All clients — Claude Desktop and Claude Code — share one vault session, that is, one
license slot.

### Environment variables

| Variable | Purpose |
|---|---|
| `ALTIUM_BASE_URL` | The workspace address. Required |
| `ALTIUM_AUTH` | The login method: `windows` (NTLM, Windows only, the default there) or `token` (through the browser, the default elsewhere) |
| `ALTIUM_OAUTH_CLIENT_ID` | The application identifier (client_id) for token login; required with `token`, not hard-coded |
| `ALTIUM_TOKEN_STORE` | `memory` (the default — tokens in memory only) or `file` (the `tokens.bin` file in the state directory) |
| `ALTIUM_USERNAME` | The user name; empty — login with the Windows account |
| `ALTIUM_PASSWORD` | The password, if a user name is set |
| `ALTIUM_WRITE_MODE` | `guarded` (the default), `unguarded`, `readonly` |
| `ALTIUM_CONFIRM_THRESHOLD` | The confirmation threshold in `guarded` mode (default 4) |
| `ALTIUM_WRITABLE_FOLDERS` | Folder paths separated by `;`; writes outside them are forbidden |
| `ALTIUM_MAX_WRITE_BATCH` | How many components are changed per call (default 500) |
| `ALTIUM_MAX_RESPONSE_CHARS` | The tool response length limit in characters (default 20000): the client rejects a larger result entirely |
| `ALTIUM_STATE_DIR` | The directory of the session file and the audit log |
| `ALTIUM_EXCHANGE_DIR` | The model file exchange directory (`exchange` in the state directory by default) |
| `ALTIUM_EXCHANGE_AGENT_DIR` | The same directory in the paths the agent uses, if it sees the file system differently from the server. Empty — the paths are the same |

Login with the Windows account needs no password: the identity is taken from the
NTLM negotiation, nothing secret goes to disk. Both sign-in methods are described below, in the section
"Signing in".

These variables can be set by hand or with `tools\env-editor.ps1` — without parameters
it shows the current values, with `-Set` and `-Remove` it edits them:

```powershell
tools\env-editor.ps1                                          # Windows user system variables
tools\env-editor.ps1 -Set @{ ALTIUM_WRITE_MODE = "guarded" }
tools\env-editor.ps1 -Remove ALTIUM_WRITE_MODE

tools\env-editor.ps1 -ClaudeConfig -Set @{ ALTIUM_WRITE_MODE = "guarded" }  # claude_desktop_config.json
tools\env-editor.ps1 -CodexConfig  -Set @{ ALTIUM_WRITE_MODE = "guarded" }  # ~/.codex/config.toml
```

By default the script edits ordinary Windows user environment variables — they are
seen by `tools\mcp-call.py` and any direct start of the `.exe`, but terminals that are already open will not
pick them up until restarted. `-ClaudeConfig` and `-CodexConfig` edit the `env` of the same
server in the config of the corresponding client — there the client itself must be restarted
(Claude Desktop — tray → Quit).

## Write modes

The confirmation rules: any read — no questions; a single edit (at most
`ALTIUM_CONFIRM_THRESHOLD` components, folders or templates at a time) — at once, without
confirmation; a bulk edit — more than `ALTIUM_CONFIRM_THRESHOLD` — always requires
a preview and confirmation by token. The vault is also protected by revisions,
the trash and a daily backup.

* `guarded` — the default: an edit of up to `ALTIUM_CONFIRM_THRESHOLD` objects is applied
  at once, a larger one first returns a preview and a single-use token.
* `unguarded` — everything is applied immediately regardless of size, the log is kept.
* `readonly` — any changes are rejected.

**Confirmation of bulk edits.** It is the same in all write tools
(`vault_table_write`, `vault_update_parameters`, `vault_set_links`, `vault_copy_components`,
`vault_restore_from_revision`, `vault_repair_links`, `vault_move_items`, `vault_delete_items`,
`vault_restore_items`, `vault_component_types` with `assign`, `vault_check_components` and
`vault_cleanup_parameters` with `fix`, `vault_model_files` with `relink`). If an edit affects
more than `ALTIUM_CONFIRM_THRESHOLD` objects, the tool writes nothing and returns
`confirmationRequired: true`, `preview` (up to 20 changes — built by a dry run of the same
operation), `confirmToken` and `expiresAt`. To apply the shown edit, repeat **the same
call** with `confirmToken` (the other parameters are not needed; the token is single-use and lives 30 minutes).
What counts: copies — the number of copies; moving and deleting — the number of items (datasheets that move
along do not count); `assign` — the number of components; `relink` — the number of components being moved;
folders, templates and types — one each. Deferred edits are visible in `vault_apply_status`.

**Plan approval.** For bulk work there is a way shorter than
confirming every call: `vault_plan create` (`summary`, `operations` — which
tools the plan covers, `folders` — root folders, `maxObjects`, `hours` — 2 by
default, at most 8) creates a plan and returns `planToken`; the agent shows the
summary to the owner and after an explicit "yes" calls `vault_plan approve planToken=…`. While an
approved plan is active, a write from `operations` in the folders from `folders`, within the
remainder, goes **without a preview and confirmToken**; the remainder is charged after a successful
write by the number of really changed objects, a dry run does not use it up. A write
outside the plan follows the old rules. One plan per server process, kept in memory —
a server restart cancels it; `vault_plan show` and `vault_apply_status` show
the remainder and the period, `vault_plan close` closes it early.

Every change is written to `audit.jsonl` together with the earlier values, so the
log can be used to restore the state by hand; an edit charged to a plan carries its number
(`vault_audit plan=…`).

## Speed

A vault of 13 thousand objects answers slowly if a request is composed badly,
so the selection is moved to the server and the changes go in batches.

| Operation | Before | Now |
|---|---|---|
| Selection by a parameter value | ~50 s, walking the whole vault | 0.3–6 s |
| Selection by a description substring | ~50 s | ~6 s |
| Editing a component | ~0.8 s | 0.26 s |
| Editing 2200 components | ~30 min, cut off by timeout | ~9 min in portions |

What serves this:

* `parameterEquals` becomes a request to the parameters on the server side, and only the
  found items are read in full;
* the links of a whole portion are read by one request, and creating and releasing revisions go
  into one script — one server call instead of three per component;
* a table instead of an array of objects: parameter names are not repeated in every row;
* selections by a list of identifiers are split into parts — the server rejects lists
  longer than 1500 values.

Beyond that comes the limit of the server itself: it creates and releases about four
revisions per second, and batching cannot get around it. So at most `ALTIUM_MAX_WRITE_BATCH`
components are changed per call, and the remainder is returned as
an explicit list — the call finishes in time instead of being cut off by a timeout.

**Response size.** The Claude client accepts a tool result of roughly up to 25 thousand
tokens and drops a larger one entirely. So a response is no longer than `ALTIUM_MAX_RESPONSE_CHARS`
(20000 characters): lists are issued page by page (`offset`, `nextOffset`, `truncated`), and if a
response is nevertheless longer, a refusal with advice on how to narrow the selection comes instead.
For write tools (including with `dryRun`) the response is shortened by itself instead of a refusal: the summary
(`applied`, `skipped`, `failed`, groups) comes in full, per-part lists — the first ones that
fit, the rest as a count (`resultsOmitted`, for any list `X` — `XOmitted`).
Responses are serialized with the letters of part names unescaped (a Russian name is not turned
into `\uXXXX` sequences): this saves the character budget and the agent's tokens.

## Three coordinates of a part

A part in Altium is placed in three dimensions at once, and all three are needed for order in the vault.

1. **The folder** — `Item.FolderGUID`; managed by `vault_folder` and `vault_move_items`.
2. **The component template** — a separate item of the type `altium-component-template`,
   linked to the part by a revision link with the role `ComponentTemplate`. A folder keeps
   the default template in its parameters `TemplateItemGUID` and `TemplateRevisionGUID`.
   It is edited with `vault_template`; a new template is created from a sample
   (`create basedOn=CMPT-…`) — with the sample's `.cmpt` file, otherwise Altium will not see it.
   The template keeps the default symbol and footprint in the same file
   (`set_symbol`/`set_footprint`) — a part created from the template **later** gets them;
   for existing parts the models are changed by `vault_set_links`. A template
   reference may point to a deleted or missing item — `vault_template action=check`
   finds all such references at once (a symbol and a footprint, in the trash or nowhere),
   with a ready hint for restoring or reassigning.
   A part's link points to a **specific revision** of the template, so after every template edit
   the parts stay on the old one: the response of `vault_template set_*` says how many lag behind and gives
   a ready `vault_template relink` string (or `relink=all` in the call itself). If the parts are edited
   by a migration anyway, it is more efficient to add `role: template` to the same `vault_set_links` as the
   footprint — one part revision instead of two.
3. **The component type** — what is visible in Preferences → Data Management → Component Types.
   Altium stores types as tags of a system family: the hierarchy is set by a tag's reference to
   its parent, a part's membership — by assigning the tag to the item, and a template keeps the default
   type in its `.cmpt` file (Altium reads it; it is set by `vault_template set_type`
   and applies to parts created later). Managed by `vault_component_types`:
   the tree, create, rename, move, delete and assigning to parts.

Assigning a type creates no revisions: the type is stored separately from the part content.

## Datasheets

A datasheet is an independent `altium-datasheet` item, attached to a part by a link
between items, not between revisions, so it does not follow the part by itself.
`vault_move_items` moves datasheets after the parts to the `Datasheets` folder next
to them, creating it if needed. A datasheet that is also attached to parts outside the
moved set stays in place and is listed in the response: one datasheet is
often shared by several values.

## How a parameter edit is performed

Versioning repeats the behavior of Altium, and this is not an implementation detail but a
server requirement.

The server forbids editing a released revision. So a parameter change
turns into a new revision: it inherits from the previous one, gets its parameters with
the edits made, and is released together with the links to the models. Carrying the links over
is mandatory — without it the component in Altium would be left without a symbol and
footprint.

If the active revision is not released yet, it is edited in place and released —
extra revisions are not multiplied, and work begun earlier and left unfinished is brought to
a consistent state.

Moving components and folder operations create no revisions: they change
the placement, not the content.

## Compatibility with Altium Designer

The vault server accepts what Altium Designer does not fully understand, so
everything written is brought to the form in which Single Component
Editor saves a component.

* **Numbers of typed parameters.** For voltage, current, temperature, capacitance and
  other quantities Altium stores next to the displayed value its number
  (`ParameterRealValue`, the format `7.00000000000000E+0001`) and works with exactly that.
  Altium considers a value without a number invalid and clears it on a manual save of the component.
  The number is computed automatically; the rules were checked by the `codec-check` command
  against the whole vault history — 55 thousand values, no mismatches. A value that
  does not parse as a quantity of its type is rejected with an explanation and an example.
* **Types of new parameters** are taken from same-named parameters in the vault.
* **Links.** The role in `HRID`, vault GUIDs on both revisions, for a footprint —
  `{"Footprint":{"FootprintIndex":0,"IsDefaultFootprint":true}}`. Without this data
  Altium does not show the footprint although the link exists on the server. Several footprints on a part
  are stored the same way as in Altium: the primary — the role `PCBLIB` and index 0, the additional ones — `PCBLIB 1`,
  `PCBLIB 2` with indexes 1, 2 and the flag `false`.

`vault_check_components` finds components written earlier without this, and values
that Altium has already cleared: in an earlier revision the value was without a number, and in the next one it
became empty. With `fix=true` a new revision is released in which the missing is appended and the
cleared values are returned from history.

## Model files

A symbol and a footprint are separate vault items, their content lies in the
revision payload (`Released/*.SchLib`, `Released/*.PcbLib`).
`vault_model_files` exports these files to the exchange directory and reports the path as
the agent sees it — it is passed as `filepath` to the tools of
[altium-designer-mcp](https://github.com/embedded-society/altium-designer-mcp).
`vault-source.json` with the origin of the file is put next to them; the `list` action shows
which files were edited after the export.

The edited file is returned by the `upload` action: it is released as a new revision of the same
model — by the same release script as Altium's, with the file in `Released`. If after the
export the model got another revision, the upload is refused so as not to overwrite
someone else's edits (`force=true` releases over it). A component link points to a specific
model revision, so components are moved to the new one explicitly: `relink=source` — the one
the model was exported from (the default), `all` — all on old revisions, `none` —
none. The `relink` action moves components to the active model revision without
an upload. An uploaded revision has no preview images: only Altium draws them.

The cycle of a full component edit:

1. `vault_component_detail` — parameters, value types, models and mismatches.
2. `vault_update_parameters` / `vault_table_write` — parameters, description, the part name. They reject templates (`CMPT-…`): the name, description and parameters of a template are edited by `vault_template set`.
3. `vault_model_files download` → editing the file in altium-designer-mcp →
   `vault_model_files upload` — the symbol and footprint.
   A new model from a ready file (`.PcbLib`, `.SchLib`) is created by `vault_model_files create`:
   `folder`, `name`, `description`, optionally `component` — link it to a part at once;
   `footprintMode=add` adds the footprint as an additional one instead of the primary.
   `create` does not touch an existing model.
4. `vault_set_links` — another symbol, footprint or template (in groups: `assignments`);
   several footprints on a part — the role `footprints` with a `targets` list (the first is primary).
5. `vault_component_types`, `vault_move_items` — the type and the folder.

## Signing in

The server signs in to the workspace in one of two ways; `ALTIUM_AUTH` selects it. The choice is the
user's: it depends on where the server runs and what your workspace accepts.

| Method | `ALTIUM_AUTH` | Where it works |
|---|---|---|
| NTLM with the Windows account | `windows` (the default on Windows) | On-Prem workspace, Windows machine in the same network or domain |
| Token login through the browser | `token` (the default elsewhere) | On-Prem workspace from any system; Altium 365 is experimental (see below) |

`vault_status` and `vault_session action=show` report the method in use and its state.

### NTLM (Windows, On-Prem)

The identity is taken from the Windows account, no password is needed and nothing secret goes to disk.
With `ALTIUM_USERNAME` and `ALTIUM_PASSWORD` set, the login is by that name and password instead. The server keeps
**one** workspace session (see "Sessions" below).

### Token login through the browser

On Linux, macOS and in a Claude cloud session the server signs in the same way as Altium Designer: **through
the browser**, by OAuth2 (authorization code + PKCE). The server does not accept or store a password — the user
enters it on the page of the Altium login server.

```
ALTIUM_AUTH=token                  # the default outside Windows; on Windows — explicitly
ALTIUM_OAUTH_CLIENT_ID=<client_id> # required: see below
ALTIUM_TOKEN_STORE=file            # optional: remember the login between runs
```

**What the login server requires.** Altium gives token login only to a registered application: a
`client_id` is needed for which the login server knows a set of scopes and accepts the `redirect_uri` of the
login service (`<login server>/api/AuthComplete`). Altium 365 asks for the same. Which `client_id` to use is
decided by the user, not by the MCP server — there is no identifier in the code or the examples. If an
identifier unknown to the server is set, `vault_session action=login` will report that the scope list is empty.

**How it looks for the agent.**

1. Any tool without a login answers "login required" and names `vault_session action=login`.
2. `vault_session action=login` returns `loginUrl`. The agent shows the link to the user;
   they open it and log in with their own account (the link waits 5 minutes).
3. `vault_session action=login_status` waits for the login for up to `waitSeconds` seconds; `state` is `pending`,
   `completed` or `failed` (with a reason).

The `login` call returns at once and does not wait for the browser: otherwise the agent could not show the link.
To log out and change the user — `vault_session action=reset` (a new link) or `close`
(the refresh token is revoked on the server).

**Tokens.** They live in process memory; by expiry (`exp` from the JWT) they are refreshed automatically by the
refresh token. With `ALTIUM_TOKEN_STORE=file` they are saved in `tokens.bin` of the state directory
(Windows — DPAPI, otherwise a file with permissions 600) and survive a restart. To log in without an MCP client —
the command `altium-vault-mcp login` (with `ALTIUM_TOKEN_STORE=file`), `altium-vault-mcp logout`
forgets the tokens.

### Altium 365 (experimental, not yet verified)

For Altium 365 workspaces the same flow is implemented the way Altium Designer signs in, but it
**has not been verified on a live Altium 365**: only an On-Prem workspace with the same login server was checked.
The user needs: the workspace address (`ALTIUM_BASE_URL`, `https://<workspace>.365.altium.com`), a `client_id`
registered with Altium with the scopes and the `redirect_uri` of the login service, and — for Altium 365 — a
workspace token (a token exchange with the scope `a365:workspace:<guid>`), which this server does not do yet: it
takes the scopes from `api/ClientScopes`. If token login does not work on your workspace, send the text of the
response of `vault_session action=login`.

## Sessions

The number of concurrent sessions on the server is limited by the license, and an issued session
lives about a month. So the server keeps **one** session and reuses it
between runs: the identifier is saved in the state directory, encrypted with
DPAPI for the current user, and is checked on the server at start. Process
shutdown intentionally does not close the session — otherwise every restart would spend a new
slot.

To close the session explicitly:

```powershell
<repository>\Release\altium-vault-mcp.exe logout
```

The `vault_session` tool does the same, and its `reset` action
additionally frees the slot occupied by a hung session.

## How it works

The server is a console program that speaks MCP over stdio. On the other side it talks to the workspace
the way Altium Designer does:

* **SOAP services of the vault** — reading and writing items, revisions, links, folders and the trash. The proxies
  in `Soap/Vault/` are generated by `dotnet-svcutil` from the service description (WSDL) that the server publishes
  itself.
* **A script mechanism** — a revision is created and released by one script, the same as Altium sends, so the server
  accepts the result and Altium Designer shows it correctly (see "Compatibility with Altium Designer").
* **REST services** — the search index behind the Components panel: fast selections over the whole vault.
* **The login service** — the session (NTLM) or the browser login (see "Signing in").

Between the tools and the services there is a safety layer: write modes, confirmation of bulk edits, work plans,
an audit log with earlier values, and a limit on the size of every response. All logic lives in `Vault/` and
`Safety/`; the files in `Tools/` only describe the MCP tools and call it.

```
publish.cmd, publish.ps1   Build into Release\ (a double click on publish.cmd)
Release/                   The built server — the path for clients (not stored in git)
src/AltiumWorkspaceMCP/
  Configuration/   Settings and write modes
  Vault/           The session, the SOAP gateway, directories, components, revisions, scripts
  Vault/Rest/      The Workspace REST services: the service directory, search (the Components panel index)
  Safety/          The confirmation rules and the audit log
  Tools/           The MCP tools
  Mcp/             The stdio host and passing refusal reasons to the client
  Soap/Vault/      The SOAP proxies of the vault service, generated from the server WSDL (do not edit)
  Soap/Ids/        The login service (IDS) contracts, written by hand
  Auth/            Token login through the browser: PKCE, the login service client, the tokens and their file
tools/generate-proxies.ps1 Generating the proxies from tools/wsdl/VaultService.wsdl
tools/mcp-call.py          Calling tools over stdio for manual checks
tools/smoke.py             A read-only check of a live server by a list of cases (smoke-cases.example.json)
tools/token-session-check.py A live check of token login through the browser
tools/env-editor.ps1       Viewing and editing the server environment variables in the client config
tools/check-public.py      Scans the sources and documents for leaks of internal names and text
docs/tools.md              Instructions for an agent working with the server
tests/                     Unit tests (xUnit)
CHANGELOG.md               The release history
```

The SOAP proxies are generated from the WSDL that the server publishes itself. To update them after a server update:

```powershell
pwsh tools\generate-proxies.ps1 -Download
```

Without `-Download` the generation works from the saved `tools/wsdl/VaultService.wsdl` and does not need the server.

**Diagnostics.** The variable `ALTIUM_SOAP_TRACE=<directory>` turns on recording of all outgoing
SOAP messages and release scripts. The messages contain a **real session identifier**: the recording
directory must not be kept in a repository or forwarded.

## Limitations

* Model files are edited by altium-designer-mcp; this server exports and releases them but
  does not draw them. A new model is created from a ready file (`vault_model_files create`); it has no preview
  images — only Altium draws them.
* Numeric revision numbering is supported, including multi-level. Letter
  schemes are rejected with a clear message.
* Selection by description is slower than selection by parameter: the server searches the substring by walking.
* Deletion moves to the trash; the final emptying of the trash is done in Altium.

## Known issues

* **Empty preview for new models.** A symbol or footprint created with `vault_model_files create` shows an
  empty preview in the Altium Explorer panel. The model itself is complete and opens in the editor; the
  preview images appear once the model is saved (released) from Altium Designer. Generating previews on the
  server side is planned.
* **Restore from the trash may be refused.** On some servers `vault_restore_items action=restore` returns the
  server's generic "Failed to perform restore." for some objects (folders in particular); restoring the same
  objects from the server's web page `/Trash` may also fail. Deleting models that live parts still use is
  blocked for this reason.
