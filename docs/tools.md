# Instructions for the agent: the altium-vault server


## Altium Workspace

The MCP server `altium-vault` is connected. It talks directly to a live Altium Workspace
and reuses one saved session.

### Reading

- `vault_status` — the server version, address, account, sign-in method and state (with browser login and no login yet — a note to call `vault_session action=login`), write mode. Start with it.
- `vault_folders` — the folder tree, **start with it**: without parameters — the top levels, one
  line per folder, `[+N]` — how many nested folders are hidden. Then `under` (the needed branch;
  accepts a folder address, see "Folder address" below), `depth` (how many levels), `nameContains`
  (find a folder by name at any depth). GUIDs (`includeGuids`), the `Datasheets` system folders
  (`includeSystem`) and folder templates (`includeTemplates`) — on request only.
- `vault_components` — **what is in the vault**, by the Workspace search index (seconds over the whole vault):
  `action=types` — the type tree with part counts and `withoutType` (parts without a type); `facets` with
  `type` — type parameters and their frequent values, for numeric ones the range; `list` with `type`, `filters`,
  `text`, `columns` — parts as a table like `vault_table`, `total` and `nextOffset`. `type` — a path in the
  type tree, its ending or a name (`Resistors Small`); parts of the type and of the nested types are taken.
  `filters` — `Name=Value`, `Name>=Value`, `Name<=Value`; numbers are compared **by number**
  (`Value>=1k`, `Value<=10u`), text — whole and case-insensitive (`Case/Package=0603`, `*`
  and `?` are allowed). Values in `facets` are shown in lower case (that is how the index stores them), in `filters` case does not matter.
  The index is updated with a delay after a write: look for a just created part with `vault_table`.
  **Folder and footprint.** `folder` — a path, its ending or a GUID (loosely, as in reading;
  the response has `folder` and, if the path was matched loosely, `resolvedFolder`): parts of the folder **and
  of the nested ones** are taken. `footprint="R 0603"` — the whole footprint name (case does not matter, searched among
  all footprints of the part); on a typo the response suggests similar ones. In `list` the `footprint`
  column is the current footprint (for several — comma-separated with the number in `[]`); without `columns`
  it is always shown, with `columns` — only if listed (`columns=["footprint"]` is the
  fastest "part → footprint" list, one call per folder, ≤200 rows). `includeGuids=true`
  adds `footprintRevision` like `PCC-0009-3` (the footprint HRID and the revision number);
  the same format is accepted as a target by `vault_set_links` — it returns the given revision if it
  exists, otherwise an error with the list of existing numbers; without a revision number (`PCC-0009`) the
  active one is taken. For old parts the index does not store the revision — the column is empty, and the response says for how many rows.
  **Compact list.** `compact=true` removes `folder`, `revision`, `comment`,
  `description` from the row — only `hrid` and the requested columns remain (`columns`, including `footprint` and
  `footprintRevision`); a row is shorter (≈40 characters instead of ≈155), so noticeably
  more rows fit the response budget per call. The row limit per call, as before, is determined by the response budget
  (`ALTIUM_MAX_RESPONSE_CHARS`), not by a count. Without `compact` the output does not change. In
  `facets` — the `footprints` summary: how many parts use which footprint (top 15 and `[+N]`) and
  how many have none. Like the Components panel, the output does not show the "not applicable" states
  (Obsolete, Abandoned, Deleted; the response has `excludedStates`); `includeAllStates=true` returns them too.
  **Summary.** `action=overview` with `type` (and optionally `folder`, `footprint`, `filters`) —
  one short response (≤ 8 000 characters, about a second) "what parts these are and how filled in they are":
  `total`; `gaps` — how many parts have no `Manufacturer Part Number`, no `LCSC Part#` and no footprint
  (count and share); `folders` — the top 10 folders with part counts and `[+N]`; `parameters` — lines
  "Name — filled (share): value (count), …" by descending fill rate with the top 5 values; parameters
  with a fill rate below 2 % are folded into `rare` ("rare: N parameters"). An empty value does not count as
  filled. Without `type` — `byType`: the top tree types with part counts and the share without MPN; there are no
  parameters. Parts without a type are not in the top types (the count is in the hint). There is no distribution by template:
  the search index does not store the component template (for templates — `vault_templates`, `vault_component_detail`).
- `vault_table` — **the main reading tool**: components as a table of columns and rows.
  `parameterEquals=["Name="]` finds a parameter with an "empty" value, not its absence;
  for "no parameter at all" — `parameterMissing` (requires `folder`, costs more time).
- `vault_component_detail` — parameters, the component type (`componentType`), revisions, models (for
  footprints — `footprintIndex`: the number, 0 — primary, and `isDefaultFootprint`; the primary goes
  first, additional ones have the role "additional footprint n") and the usage of one part.
- `vault_content_types` — content types: `altium-component`, `altium-symbol`,
  `altium-pcb-component`, `altium-component-template`.
- `vault_folder_types` — folder types.
- `vault_templates` — component templates, one compact line per template: the type, default symbol and
  default footprint; the folder is shown only for templates outside the common folder.
  A reference to a deleted or missing item shows instead of `SYM-…`/`PCC-…`
  "in trash: … (deleted …)" or "not in vault: …"; the list of all problem references at once is
  `vault_template action=check`.
- `vault_component_types` with `action=list` — the component type tree.
- `vault_revision_parameters` — parameters of earlier revisions: what was there before an edit.
- `vault_model_files` — symbol (.SchLib) and footprint (.PcbLib) files:
  `download` for altium-designer-mcp, `list` — what is already exported, `upload` — releasing an
  edited file, `create` — a new model from a file, `relink` — moving components
  to the active model revision. `create` with `component` has the parameter `footprintMode`: `replace`
  (the default) — the new footprint becomes primary instead of the old one, `add` — it is added
  to the existing additional ones, with the last number (for a part without footprints it becomes
  primary); it does not affect the symbol. `download` of a component exports **all** its footprints.

### Changing

Every write tool has `confirmToken`: it is needed only if the edit affects
more than 4 objects (the section "Confirmation of bulk edits" below), and `dryRun`.

- `vault_table_write` — writing the table back. The pair `vault_table` → `vault_table_write`
  replaces exporting to Excel and pasting back. By default an empty cell is an empty value;
  `emptyMeans="delete"` removes the parameter from the revision entirely.
- `vault_update_parameters` — a pinpoint edit of one or two components; `deleteParameters`
  removes a parameter from the revision entirely (not the same as writing an empty string into it).
- `vault_cleanup_parameters` — removes parameters in bulk by the rule `keep`/`drop` for a list of
  components or a folder (like `vault_check_components`): `dryRun` shows the plan,
  `fix=true` applies it.
- `vault_set_links` — relinking a symbol, footprint or template (the roles are `symbol`, `footprint`, `footprints`,
  `template`, `datasheet`, `simulation`; any other name is refused with this list); for a migration —
  **in groups in one call**: `assignments=[{components:[…], links:[{role:"footprint",
  target:"PCC-…"}]}, …]` ("these 120 → `R 0603`, these 80 → `R 0402`"). The target is a model
  identifier (PCC-…, SYM-…), the same identifier with a revision number after a hyphen (`PCC-0009-3`)
  or a revision GUID from `vault_components`. An identifier with a revision number is first
  looked up as a whole (parts like `CMP-000-0960` stay one identifier); if there is no such
  item, the number after the last hyphen is read as a revision number — if there is no such revision,
  the response names which exist. One group is shorter:
  `components` + `links`. A component cannot be in two groups with the same role (a refusal with
  a list). It is written in batches of 200, the confirmation is one per call and shows groups (with the
  target revision given and a "not active" mark if it is not the latest revision), not
  parts. The response has: `applied`, `skipped` (counts), `failed`, `elapsedMs`, refusal reasons in
  `failures`; no more than `MaxWriteBatch` (500) are changed per call — the remainder is in `remaining`,
  repeat the same call (the relinked ones are skipped).
  **Several footprints.** The `footprint` role (one target) changes only the primary
  footprint and leaves the others alone. To give a part package variants (Normal / Least /
  Most), set **the whole set** with the `footprints` role and a `targets` list — the first footprint is primary:
  `vault_set_links components=["CMP-…"] links=[{role:"footprints", targets:["PCC-000-0527",
  "PCC-000-0528", "PCC-0014"]}]`; the same `targets` go in `assignments` of any group. Altium stores
  them as "PCBLIB" (primary), "PCBLIB 1", "PCBLIB 2": the server assigns the numbers by the list order.
  The list is the **whole set**, not an addition: footprints that are not in it are removed. So
  *to change the primary* — reorder the list (`["PCC-…Least", "PCC-…Normal", "PCC-…Most"]`), *to remove
  one* — send the list without it, *to remove all* — `targets: []`. A repeated call with the same set
  creates nothing: the part goes into `skipped`. To add one footprint to the existing ones without listing the
  others — `vault_model_files create … component=… footprintMode=add` (for a new file), and for an
  existing footprint — read the set from `vault_component_detail` (`models`,
  `footprintIndex`) and send it whole with the addition. `footprint` and `footprints` cannot be
  combined on one component; the role "PCBLIB 1" is not assigned directly. **An empty target of `footprint` with
  several footprints is a refusal for this part** (before writing; the other parts of the call go on as usual, in
  `failures`): "CMP-… has N footprints; to remove the primary, give the whole set with the `footprints` role
  (the first in the list becomes primary) or an empty `footprints` list to remove all". For a part with one
  footprint an empty target removes it, as before. The preview shows the set:
  "… (primary), … (#1), … (#2)".
- `vault_copy_components` — copies of a sample, including a whole series of values at once. **Pass all
  differences of a copy in this same call:** `comment` (the name), `description`, `parameters`
  (including `Manufacturer Part Number`), `links` (another footprint etc.),
  `componentType` (the type; without it — the sample's type). Each copy is created by one operation: one
  revision, the type and datasheet links at once; a separate edit after copying creates an extra
  revision. The copy's **type** is the first of: `componentType` from the request (`explicit`), the type of the copy's template
  (`template`), the type of the folder template (`folderTemplate`), the type of the sample (`source`). The response shows
  `comment`, the type (`componentType.name/path/source`, `warnings` — if the sources disagree and
  another type is needed, name it explicitly), the models and the differences from the sample — no separate check is needed.
  `none` — the type is not set anywhere: the part will not be visible in the Components panel tree, assign the type
  (`vault_component_types` with `assign`). **Protection against duplicates:** before creation each copy
  is checked by `Manufacturer Part Number` and `LCSC Part#` — including a value inherited
  from the sample unchanged (the main case: forgot to change the MPN of a value). If a part with the same
  value is found — the response has `duplicates` (`copy`, `parameter`, `value`, `existing` — the HRIDs found),
  `existingParts` (name and folder), `created: 0`, nothing is created; `allowDuplicates=true` creates everything
  anyway. The match is strict and case-insensitive; an empty value and a dash are not searched. The check is
  one search request (`duplicateCheckMs`). The index is updated with a delay: a copy created seconds
  ago may not be found yet, so the same new MPN/LCSC Part# value on two copies
  **of the same call** is caught separately, without a request to the search service: `created: 0`,
  `duplicates` with the mark "inside the call: copies #…", `allowDuplicates=true` lifts that too.
  `vault_update_parameters` and `vault_table_write`, when changing these parameters to a value that
  **another** part has (in the vault or in another row of the same call), **warn**
  (`duplicates`, `duplicatesNote`) but apply the edit. A dry run shows `duplicates` the same way.
- `vault_move_items` — moving components, symbols and footprints to another folder.
  One folder — `items` + `targetFolder`; a layout over several — `moves`:
  `[{"items":[…],"targetFolder":"…"}, …]`. All folders are resolved before writing; an item in two
  groups or `moves` together with `items` is a refusal before writing, nothing is moved. All groups go
  in one request to the server; datasheets go with the parts of each group. The response: `moved`, `groups`
  (by folders), `skipped` (already in place — repeating the same call gives `applied=false`), `failed`
  (datasheets that could not be moved, with the parts moved), `remaining` (no more than
  500 items per call; repeat the call), `elapsedMs`. The confirmation is one per call by the sum of
  items, or a plan (`vault_plan`).
- `vault_delete_items` — moving to the trash. The usage check runs **always**, with `force` and
  without: a reference from the **active revision of a live part** or from a **template** (the default symbol and
  footprint, `ModelLinks` in `.cmpt`) is a refusal with a list of parts and templates.
  First relink the part to another model (`vault_set_links`), change the model of the
  template or move the item itself (`vault_move_items`). If usage could not be fully checked
  (the "where used" pages were not read to the end, the template `.cmpt` was not read) — also a refusal.
  `force` lifts only the server's refusal on references from earlier (not active) revisions.
- `vault_restore_items` — the trash: `action=list` (the default is `restore`) — what is in the
  trash, the path before deletion (restored from the chain of deleted parents), when it was deleted
  (`deletedAt`) and who references it — live parts and templates **separately** (`usedByComponents`,
  `usedByTemplates`: `count` and the first 3 `hrids`). **`deletedAt`** — if the folder itself of the item (or
  of any ancestor of its folder) is deleted in the trash, this is the deletion time of the **nearest**
  deleted ancestor folder (`deletedWithFolder` in the row — its path): the server updates
  `LastModifiedAt` only for an explicitly deleted folder, not for the items and nested folders that
  went to the trash with it, so their own `LastModifiedAt` is not
  suitable for cutting out a deletion window. If neither the folder nor its ancestors were
  deleted separately — `deletedAt` = the last-edit time of the object itself (the server does not keep
  a separate deletion time), `deletedWithFolder` — `null`. `pathContains` narrows by path,
  `deletedAfter`/`deletedBefore` (ISO time, for example `2026-01-15T10:00:00Z`) — by this
  `deletedAt`, this is how the window of an accidental deletion is cut out, `contentTypes` (a type HRID, for example
  `["altium-symbol","altium-pcb-component"]`) — by content type; all three select **before** the
  usage count. `onlyUsed` — only what something references. Items are paged by
  `offset`/`limit`, folders — as a separate page `foldersOffset`/`limit` (with its own `foldersNextOffset`
  in the response): the row order in both lists is stable (path, then HRID/GUID), pages do not
  overlap and do not lose items — **to walk the whole trash**: call with `offset=0` (and
  separately `foldersOffset=0` for folders), then substitute `itemsNextOffset`
  (`foldersNextOffset`) from the response until it is `null`; the sum of what is shown over all pages equals
  `itemsTotal` (`foldersTotal`). The response always fits the size budget (shared by `folders`
  and `items`) — if the budget cuts a page earlier than `limit`, the next offset in the response
  is recomputed to what was actually shown, nothing is skipped or repeated on continuing.
  **Items come first** and get the budget first, folders get the rest: with `limit=200`
  and long folder paths the page still shows items, and `itemsNextOffset` is always greater than
  `offset`, so the paging does not loop. If folders did not fit — `foldersNextOffset` equals
  `foldersOffset`: keep paging the items, and when `itemsNextOffset` becomes `null`, the folders get
  the whole response; folders only — `offset=itemsTotal` (the items page is empty) and `foldersOffset` from the
  previous response.
  **The window of an accidental deletion** — `deletedAfter="2026-01-15T10:00:00Z" deletedBefore="2026-01-15T10:03:00Z"`
  (and, if only models are needed, `contentTypes=["altium-symbol","altium-pcb-component"]`).
  The usage count is limited by a time budget (~60 s): if not in time — `timedOut=true` and
  `itemsNextOffset` points at what is not yet counted (the same call with this `offset` counts further,
  losing nothing). Usage is counted by a **targeted batch** for the whole checked
  page at once (the page — without `onlyUsed`, the whole selected tail — with it): a few requests
  in batches following the number of checked items, not the size of the whole vault and not one request
  per item — this resolved the earlier cause why the list either hung for minutes
  (one by one) or, after the first attempt to fix it, became even slower — up to ~90 s regardless of
  the number of targets (the old batch walk read the whole vault at once).
  `action=restore` accepts `itemGuids` and/or `folders` (folder GUIDs from `list`; a folder
  is restored with its contents, nested ones — parents before children).
- `vault_folder` — folder actions: `create` (accepts `namingScheme`, for example
  `CMP-016-{00000}`; as a list — `paths[]`, full paths or relative to `parent`: missing
  parents are created before children in one request, existing ones — `skipped`; conflicts — an empty
  path, a non-existent root, the name `Datasheets` — a refusal before writing with the whole list; the response:
  `created`, `skipped`, `failed`, `remaining` (no more than 200 folders per call), `elapsedMs`;
  the confirmation is one per call by the number of folders being created), `rename`, `move`, `delete`, `set_template`.
  `repair_system` brings the `Datasheets` system folders to the form Altium creates them in
  (a special type, hidden in Explorer, a name scheme): `folder` — one folder, empty — all
  differing ones; do a dry run first. The folders that `vault_move_items` creates for
  datasheets are now correct from the start.
  **`delete`** counts objects = the subtree folders + the live items in them (not "one for a folder"):
  without `force` a non-empty folder is refused in advance with a hint; with `force`, if the
  subtree has an item referenced by the active revision of a live part or a template
  **outside** the subtree — a refusal even with `force`, with a list "model → how many parts → where" and a hint to
  move the models (`vault_move_items`) before deleting the folder.
- `vault_component_types` — component types: `create`, `rename`, `move`, `delete`, `assign`.
  `delete` deletes the type with nested ones **irreversibly** (there is no trash), but only an empty one: parts (the search
  service, all states) and templates (the type in `.cmpt`) in the whole subtree — a refusal with a list "type → parts,
  templates" (the first 10); first `assign` and `vault_template set_type`. The search index lags a minute or two.
- `vault_template` — template editing: `show` (the type is taken from the `.cmpt` file, as in Altium;
  both the type from the parameter and the folder, default symbol and default footprint are shown), `create`,
  `set`, `set_type`, `set_default_folder`, `set_symbol`, `set_footprint`. The type, folder, symbol and
  footprint defaults are in the `.cmpt` file of the template revision: `set_type` (`type` —
  a name, path or GUID), `set_default_folder` (`folder`), `set_symbol` (`symbol` — `SYM-…`, the GUID of an
  item or revision; empty removes the link) and `set_footprint` (`footprint`, the same for `PCC-…`)
  download the package of the active revision, edit only this field in `.cmpt` and release a new
  revision with all package files; the revision parameter `ComponentTypeGuid` is also updated
  by `set_type`. `set_symbol`/`set_footprint` check the content type of the target (`altium-symbol` /
  `altium-pcb-component`) and write the **item** GUID of the model into the file, even if a revision
  GUID is passed. **A template setting applies to parts created later; for already
  existing parts the type is changed by a tag — `vault_component_types assign`, the models —
  `vault_set_links`.** If the package has no `.cmpt` (and a template created through `create` without
  `basedOn` has none), the action refuses: the file is not made up.
  **Moving parts to a new template revision.** A part's link to a template points to a **specific
  revision** of the template; every edit (`set_type`, `set_default_folder`, `set_symbol`, `set_footprint`,
  `set`) releases a new one, and the parts stay on the old one (in this case Altium offers a batch
  update). The response of these actions contains `laggingComponents`: `count` — how many parts stayed on
  the old revisions, the first ten names and the ready call string `relinkCall`; a dry run
  shows the same number before writing. To move the parts — the **`relink`** action (`components` — which ones,
  empty — all lagging; written in batches of 200 through `RevisionBatch`, no more than
  `MaxWriteBatch` per call, the remainder — `remaining`, a repeated call skips the already moved; more than 4 parts
  in guarded mode — a preview and `confirmToken`, or an approved plan, `vault_plan` with the operation
  `vault_template`) or the parameter **`relink=all`** of `set_*` — the same move in the same call
  (`none` — the default). **If the parts are edited by a migration anyway, it is more efficient to add
  `role: template` to the same `vault_set_links` as the footprint: one part revision instead of
  two.** The move does not change the part type — the type is a tag (`vault_component_types assign`).
  **`create` with `basedOn`** creates a template with a file: the sample's `.cmpt` is taken as is, and in it
  only `ItemGUID`, `RevisionGUID` and what is named in `type`, `defaultFolder`
  (a path or GUID), `namingTemplate` (the part name template), `symbol` and `footprint` are replaced;
  the other bytes are not touched, unset models stay as in the sample.
  `comment` — the template name (unique within the folder; `description` is taken by default).
  **`set`** — `comment` (the template name; the Comment column of the revision), `description` (the revision description)
  and/or `parameters`; one of the three is enough, a call without parameters but with a name or description is
  legitimate. The revision is released with all package files carried over (`.cmpt` intact); the name is unique within
  the folder (the template is not its own twin); the response has the old and the new value; what is already set
  does not deserve a revision. **Templates are rejected** before writing (named individually) by all paths that release
  a revision without package files: `vault_table_write`, `vault_update_parameters`, `vault_restore_from_revision`,
  `vault_set_links`, `vault_repair_links`, `vault_check_components fix=true`, `vault_cleanup_parameters fix=true`:
  a general edit would release a revision without the `.cmpt` file and wipe the type. A template **as a link target**
  (`role: template`, `target: CMPT-…`) is ordinary `vault_set_links` work, the refusal does not concern it.
  `vault_model_files relink` (and the link after `upload`) skips templates and names them in `notes`.
  `vault_move_items` and `vault_delete_items` release no revisions — they are safe for a template.
  `vault_copy_components` **refuses** a template as a copy sample before writing: the copy
  would come out without the `.cmpt` file and without a type; a new template from a sample — `vault_template create basedOn=…`.
  Without `basedOn` the template is created without a file (the response has `warning`), and `type`, `defaultFolder`,
  `namingTemplate`, `symbol` and `footprint` are refused: specify `basedOn`. `dryRun` shows the file and its size.
  Try `dryRun` first: it shows the old and new type, the file name, the new revision number and
  a fragment of `.cmpt` before and after the edit. The `ComponentTypeGuid` parameter is not accepted separately in `set` and `create` —
  the type is set by `set_type`.
  **`check`** (read-only) — the status of the `ModelLinks` references (the default symbol and footprint) against
  a live item: without `template` — all templates in one call, with `template` — only that one. It reads
  GUIDs in batches (first live items by one read, the remaining ones in the trash by one or two more),
  not by a request per template. The response has a summary (`templatesChecked`,
  `linksChecked`, `templatesWithProblems`) and rows of problem references only, `problems`: `ok`
  never appears in the response. For `inTrash` — `hrid`, `restoredPath`, `deletedAt` and a ready `hint`
  (`vault_restore_items action=restore itemGuids=[…]`; for all references at once — `restoreAllInTrash`
  at the root of the response); for `missing` — a hint for `vault_template set_symbol`/`set_footprint`.
  The response fits the `ALTIUM_MAX_RESPONSE_CHARS` budget by the same technique as `vault_check_components`
  (`problemsOmitted` if there are more rows than fit).
- `vault_restore_from_revision` — return values from an earlier revision after a failed edit.
- `vault_repair_links` — return lost links to the symbol and footprint.
- `vault_check_components` — check that Altium will read the components correctly and will not
  damage them on save; it also looks for **parts without a component type** (they are not visible in the
  Components panel tree) and **a reference to a model in the trash** (the link is intact, but the
  symbol, footprint or template is deleted — Altium will not open the model or will hang). `fix=true`
  fixes what was found: the format — with a new revision, the type — by a tag without a revision (the one given by the part's
  template, otherwise the folder template; no source — the part is unfixable, the type is assigned
  by `vault_component_types` with `assign`). A reference to a model in the trash is not repaired by `fix` — first
  restore the model (`vault_restore_items`), the hint names its `itemGuids`. **The footprint
  set** is checked as a separate kind, "footprint set is broken": two primaries, no
  primary among several, a repeated number, "PCBLIB n" with a number other than n; the check **only
  reports** and does not repair — which footprint is primary is the owner's decision; the fix is `vault_set_links`
  with the `footprints` role and the full `targets` list.
- `vault_plan` — one owner confirmation for the whole bulk job instead of confirming
  each portion (the section "Plan approval" below): `create`, `approve`, `show`, `close`.
- `vault_audit` — the change log with earlier values; `plan` narrows it to the entries of one plan.
- `vault_session` — `show` (or `status`, the same) for diagnostics; `reset` and `close` — only when the owner asks.
  **Token login** (`ALTIUM_AUTH=token`, the default outside Windows): if any tool answers
  "login required", call `action=login` — it returns `loginUrl` at once. **Show this link to
  the user** (they open it in the browser and log in with their own account; you do not need a password
  and must not enter it yourself), then call `action=login_status` (`waitSeconds` up to 60)
  until `state` becomes `completed`; on `failed` read `error` and start again. The link
  waits 5 minutes. The tokens refresh themselves; `reset` in this mode — a new login link.

### How to work fast

The vault is large, and a badly composed request takes a minute instead of a second.

0. **What is in the vault — `vault_components`:** `types` → `facets` → `list`. This is how to find out "is there
   10k 0603 1%", "which packages exist", "how many parts have no type". Edit, and read what was just created,
   through `vault_table`.
1. **Search by parameter value, not by walking folders.** `parameterEquals`
   runs on the server: a selection over the whole vault takes seconds. Walking folders
   and reading all components in a row takes tens of seconds and risks a server failure.
2. **Set `columns`** if you do not need all parameters. A heterogeneous selection gives
   a hundred and fifty columns, almost all cells of which are empty.
   **Page a long selection:** `vault_table` returns 100 rows per call (`limit`), and if the rows do not
   fit the response, there are fewer; `truncated: true` and `nextOffset` — the position to continue from
   (`offset=nextOffset`). Do not set `limit` in the thousands: the server reads `offset + limit` records. The same
   goes for `vault_audit` and the detailed list of `vault_check_components`/`vault_cleanup_parameters`.
   If a response is nevertheless too large, a refusal "Response of tool … is N characters, over the limit" with advice comes.
3. **Edit by table.** Read `vault_table`, change the needed cells, hand it to
   `vault_table_write` — two operations for a batch of any size. One-by-one calls of
   `vault_update_parameters` on tens of components are noticeably slower.
4. **Do not repeat a read without need.** The `vault_table` response already contains everything
   needed for an edit; `vault_component_detail` is needed only for models,
   revision history or a part's usage.
5. **Large edits go in portions.** At most
   `ALTIUM_MAX_WRITE_BATCH` components are changed per call: the server releases about four revisions
   per second, and two thousand at once would not finish in time. The response reports the remainder — just
   repeat the call for the remaining rows, without splitting the work by hand in advance.

### Folder address

A folder can be named by a full path (`Components\Passive Components\Resistors`), by a **path
ending** by whole segments (`Passive Components\Resistors`), by a unique name
(`Resistors`, if there is one) or by a GUID; case and `/` instead of `\` do not matter.

* **Reading** (`vault_table.folder`, `vault_folders.under`, `vault_check_components.folder`)
  forgives inexactness: `Passive\Resistors` will find `Components\Passive Components\Resistors`.
  If the address was inexact, the response has `resolvedFolder: {requested, path, how}` —
  check that the right folder was chosen.
* **Writing** (`targetFolder`, `parent`, `folder` in `vault_folder`, parameter cleanup) rejects an inexact
  address: the refusal lists similar folders — take the full path from there.
* The `Datasheets` system folders are not chosen loosely — only if they are named explicitly.
* One name for several folders — a refusal with a list; specify the path.
* If you do not know where a folder is, `vault_folders` without parameters shows the top levels.

### If something is not found

Refusals suggest the next step — read their text: for a non-existent identifier
(`CMP-000-99999`) the nearest identifiers of the same family come; for a typo in
`parameterEquals`, `parameterMissing` or `columns` (`Resistanse`) — up to five similar parameter
names; for an unknown `contentType` — the list of allowed ones; for a wrong folder — similar
folders (see "Folder address"). An empty selection by `parameterEquals` is not an error: the value is compared
as text, write it as Altium shows it (`10k`, `100nF`), for part of a value — `%`
(`Value=10k%`).

### Server instructions

Besides this file, on connect the server passes the client short instructions (`instructions`
in the `initialize` response, up to 2500 characters): a dozen rules that must always be known, even without
skills. The source is [`src/AltiumWorkspaceMCP/Mcp/ServerInstructions.md`](../src/AltiumWorkspaceMCP/Mcp/ServerInstructions.md);
when the tools' behavior changes, edit it too. To see what the client sees:
`python tools\mcp-call.py --init`.

### What happens on a write

Versioning is observed automatically and needs no intervention. A released
revision is immutable, so an edit creates the next one: with the previous parameters,
the changes made and the links to the symbol, footprint and template carried over,
and then it is released. Components without changes are skipped, extra revisions
do not appear.

### Confirmation of bulk edits

The vault is protected by revisions, the trash and a daily backup, so an edit of up to
4 objects (components, folders, templates) goes through at once. **A bulk edit — more than 4 —
first returns a preview**: `confirmationRequired: true`, the `preview` list (up to 20
real changes), `confirmToken`. Nothing is written at that point. Show the preview to the
owner (unless they asked to act without questions) and, once they agree, **repeat the same
call with `confirmToken`** — the other parameters are not needed, exactly what was
shown is applied. The token is single-use and lives 30 minutes; if it is lost — repeat the call without it.
It works in all write tools; the list of waiting tokens — `vault_apply_status`.
If a trial run wrote nothing ("Nothing to change"), there is nothing to confirm.

### Plan approval

For bulk work (tens to hundreds of objects) there is a way shorter than confirming
every call: one confirmation for the whole volume. `vault_plan create` (`summary` — one
line, `operations` — which tools the plan covers, `folders` — root folders,
`maxObjects`, `hours` — 2 by default, at most 8) returns `planToken`. Show the
summary to the owner and, only after an explicit "yes", call `vault_plan approve
planToken=…` — this is an agent rule, the server does not ask for the "yes" itself. While an approved plan is
active, a write by a tool from `operations`, in the folders from `folders`, within the
remaining volume goes **without a preview and confirmToken at all**; the remainder is charged
after a successful write by the number of really changed objects (`applied`), not by what
was declared before the write — a dry run does not use up the volume. The response of such
a write has the line `plan #N: R of M left, until HH:MM`. A write outside the plan (another
tool, another folder, a volume or period beyond the remainder) follows the old rules:
a preview and `confirmToken`.

One plan per server process: `create` while one is active is refused, close the previous one
(`close`). The plan lives in process memory — a server restart cancels it, `create`
says so directly. `show` — the remainder, the period and what was applied; `vault_apply_status`
also shows the active plan. Deletion (`vault_delete_items`, `vault_folder`)
falls under the plan only if named in `operations` explicitly.

### Dry run

Every write tool has `dryRun`. With `dryRun=true` everything is really read,
but nothing is written, and the response shows three parts: `result` — what a real
call would return, `read` — what was read from the server, `wouldWrite` — what would go to the
server, down to the parameters of the new revision and the links. Do a dry run before
an unfamiliar or large edit and when the owner asks to show the plan first.

A write response — both of a dry run and of a real one — always fits the response limit:
the counters (`applied`, `skipped`, `failed`), `groups` and notes come in full, and the per-part lists
(`results`, `failures`, `rejected`, `duplicates`…) — the first ones that fit; every
shortened list `X` has `XOmitted` ("N more"), and `responseTrimmed` has an explanation.
Only the response is shortened: the operation ran on all objects of the call.

### Typed parameters

Voltage, current, temperature, frequency, capacitance, resistance, power and percent are
typed parameters. Write the values as Altium shows them: `5.5V`,
`100µA`, `70°C`, `35MHz`, `100nF`, `4.7k` (for resistance without a unit), `1%`.
The server computes the number for Altium itself. A value that does not parse is rejected:
such a component goes into `rejected` with an explanation and an example of the format, the other
edits are applied. The types and units of a part's parameters are shown by
`vault_component_detail` in `parameterTypes`.

### If Altium does not see a footprint or clears values

This is a sign of a component written without the data Altium expects. Run
`vault_check_components` on the component or folder: it lists the mismatches, including
values that Altium already cleared on save. `fix=true` releases a new
revision with the fix and returns the cleared values from history. Before a bulk
fix do a dry run.

### Symbols and footprints

Model files are edited by altium-designer-mcp. The order:

1. `vault_model_files` with `action=download` and a component (or the model itself) —
   the response has the `agentPath` of each file.
2. Pass this path as `filepath` to the altium-designer-mcp tools:
   `list_components`, `read_pcblib`, `get_component`, `update_pad` and others.
   Edit the file in place, do not rename or move it: `vault-source.json` lies next to it,
   by which the file returns to its model.
3. `vault_model_files` with `action=upload` and `file` = the same `agentPath` releases
   a new revision of the model. By default (`relink=source`) the component the model
   was exported from is moved to it; `relink=all` moves all components with this
   model — the response lists those left on old revisions in `stillOnOlderRevisions`.
   Before editing a model shared by many components, do a `dryRun` and ask the
   owner whether to move everyone.
4. A refusal "changed after the export" means that the model was released again in the meantime:
   export it again and repeat the edit. `force=true` — only on the owner's instruction.
5. `vault_model_files` with `action=relink` moves components to the active revision of the
   model without uploading a file.
6. **A new model from a file** — `vault_model_files` with `action=create`: `file` (or `files[]`) —
   `.PcbLib`/`.SchLib`, `folder` — the model folder (it or its parent must have a naming
   scheme, otherwise a refusal), `name` — the model name (empty — the file name without extension),
   `description`, `component` — link to a part at once (the role by file type). It always
   creates a **new** item (`PCC-…`/`SYM-…`) and does not change an existing one: a model with the same
   name in the folder is a refusal, for a new revision of the same model there is `upload`. `dryRun` first.
   More than 4 files — a confirmation. One file — one model: if a library has several
   footprints, the response says so in `notes` (splitting the file is the job of altium-designer-mcp).
   A new model has no preview images — only Altium draws them.
   `footprintMode` (only with `component`): `replace` (the default) — the new footprint
   becomes **primary** instead of the old one; `add` — it is added to the existing
   **additional** ones, with the last number. This way a part gets the Normal / Least / Most variants, one
   call per file; several footprint files of one part in one call
   are still rejected (one model of each role per call).
7. **Several footprints on a part** (the Normal / Least / Most variants) — `vault_set_links` with
   the `footprints` role and a `targets` list: the whole set, the first footprint is primary; reordering the list
   changes the primary, a list without a footprint removes it, `[]` removes all (in detail — in the description of
   `vault_set_links` above). To see the set — `vault_component_detail` (`models`: `footprintIndex`,
   `isDefaultFootprint`). Set violations (two primaries, a repeated number) are looked for by
   `vault_check_components`, which only reports them.

### Three coordinates of a part

In Altium a part is placed in three dimensions at once, and order in the vault requires all three:

1. **The folder** — `vault_folders`, `vault_move_items`, `vault_folder`.
2. **The component template** — `vault_templates`; assigned to a part through
   `vault_set_links` with the `template` role, to a folder — through `vault_folder` with `set_template`.
3. **The component type** — `vault_component_types`. This is what is visible in
   Preferences → Data Management → Component Types: the tree of types by which a part is
   found in the Components panel. Types can be created, renamed, moved
   and deleted, and assigned to parts with the `assign` action — this creates no revisions.
   A template's type is set by `vault_template set_type` (editing the `.cmpt` file); it applies to parts
   created later, and for existing ones the type is changed by `assign`.

### Datasheets

A datasheet is a separate `altium-datasheet` object, and it does not follow a part by itself.
`vault_move_items` moves datasheets after the parts to the `Datasheets` folder next
to them. A datasheet that is also attached to parts outside the moved set stays
in place — the response lists such ones separately. There is no need to move them by hand.
