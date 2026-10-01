altium-vault — direct access to the production Altium Workspace parts vault. Error texts name the next step.

How to work
1. Start with vault_folders without parameters (top levels of the tree). A folder address is the full path from the response (or its ending: Passive Components\Resistors); use under for a branch, nameContains to find a folder by name.
2. What is in the vault — vault_components: types → facets → list (search index; filters: Value=10k, Case/Package=0603). Do not walk folders. vault_table — for editing and fresh parts (parameterEquals=["Name=Value"], % — part of a value); always set columns. Page long results: offset=nextOffset.
3. Edit through the table: vault_table → vault_table_write, one call with all changes. Copies — vault_copy_components: pass comment, description, parameters (including Manufacturer Part Number), links and componentType in the same call; editing after a copy costs an extra revision.
4. Dry-run an unfamiliar or large write first with dryRun=true: everything is really read, the write is only shown.
4b. Several footprints — vault_set_links, role footprints, targets (the first is primary); to add one — vault_model_files create, footprintMode=add.
5. An edit of more than {threshold} objects first returns a preview and a confirmToken and writes nothing. Show the preview and repeat the same call with confirmToken.
5b. For bulk work (tens–hundreds of objects) use one plan instead of confirming every call: vault_plan create (summary, operations, folders, maxObjects), show the summary to the owner, after their "yes" — vault_plan approve planToken=…. While it is active, writes under it need no preview or token; remainder — vault_plan show.
5c. Move models (symbols, footprints, templates) out of a folder before deleting the folder: deleting a folder with a model that a live part outside it references is rejected even with force. Trash — vault_restore_items action=list; links to models in the trash — vault_check_components.
6. Write typed parameters as in Altium: 5.5V, 100nF, 4.7k, 1%, 70°C.
7. Every part needs a component type (else it is not in the Components panel): vault_component_detail shows componentType, vault_check_components finds parts without a type.
8. Reading needs no questions. vault_session reset and close — only when the owner asks: the number of sessions is limited by the license.
9. "Login required" — vault_session login: show loginUrl to the user, then login_status.

Details: docs/tools.md.
