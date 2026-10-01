# Changelog

All notable changes to this project are described here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), the versions follow
[Semantic Versioning](https://semver.org/).

## [0.1.0] — first public version

An MCP server with direct access to an Altium Workspace vault. It works without Altium Designer:
it talks to the server over SOAP and the vault script mechanism, with the same calls Altium uses.

### What the server can do

* **Reading and search.** The folder tree (`vault_folders`), an analog of the Components panel on the
  Workspace search index (`vault_components`: types, facets, overview, list), components as a table
  (`vault_table`), a part card (`vault_component_detail`), parameters of earlier revisions, content types,
  folder types, component templates.
* **Editing.** Writing a table back (`vault_table_write`), pinpoint parameter edits, bulk parameter cleanup,
  relinking symbols, footprints and templates in groups, several footprints per part, copies of a part
  with duplicate protection (MPN, LCSC Part#), moving, deleting to the trash and restoring from it,
  folders (create, rename, move, delete, system folders), component types and templates (create from a
  sample, edit the `.cmpt` file, move parts to a new template revision).
* **Models.** Export of symbol and footprint files for editing in altium-designer-mcp, upload of edited
  files as new revisions, creating a new model from a file, moving components to a new model revision.
* **Compatibility with Altium Designer.** Everything written is brought to the form Single Component Editor
  saves: numbers of typed parameters, link roles, vault GUIDs, footprint data; `vault_check_components`
  finds and fixes what Altium would clear or not show.
* **Safety.** Write modes `guarded` / `unguarded` / `readonly`; every write tool has a dry run
  (`dryRun`); bulk edits first return a preview and a confirmation token, or go under one approved plan
  (`vault_plan`); a change log with the earlier values (`vault_audit`); a protection that never deletes an
  item referenced by a live part or a template.
* **Bounded responses.** A response never exceeds `ALTIUM_MAX_RESPONSE_CHARS` characters: lists are paged,
  write responses are shortened to a summary plus the first rows; errors name the next step.
  Responses keep non-ASCII letters (for example Russian part names) as they are, not as `\uXXXX` escapes.
* **Login.** The Windows account (NTLM) on Windows, or a login through the browser (OAuth2 + PKCE)
  elsewhere; one saved session is reused between runs, so only one license slot is taken.
* **Operation.** Stdio server and a TCP bridge; `vault_status` shows the server version and the sign-in method
  and state.

### Notes

* All tool descriptions, responses, error texts, server instructions and documentation are in English.
* Link roles are given in English (`symbol`, `footprint`, `footprints`, `template`, `datasheet`, `simulation`);
  any other role name is refused with the list of allowed ones.
* Token login against Altium 365 workspaces is experimental and has not been verified on a live Altium 365.
