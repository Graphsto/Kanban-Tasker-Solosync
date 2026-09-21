# Privacy — Kanban Tasker SoloSync

Maintained by Graphsto. Last updated: 21 September 2026.

Kanban Tasker SoloSync has no account, advertising, application telemetry or
developer-operated cloud storage. Board contents are not sent to Graphsto.

## Local files

The app reads and writes the JSON file you choose. It contains boards, optional
board groups, notes, tasks, tags, dates and synchronization metadata. The file is
plain text, not encrypted by the app. Deletion markers can retain old contents;
deleting a card is not secure erasure.

Local settings record the selected path, language, theme, board/group selection
and a random device identifier. This identifier is used to reconcile file edits;
it is not an online account or advertising identifier.

The Store edition keeps its profile under the Windows local application-data
location named `KanbanTasker.SoloSync.Store`. Windows can redirect this into the
installed package's per-user storage. Local development/test editions use
`KanbanTasker.Revived` instead. Profiles are not automatically migrated.

The profile's **Recovery** subdirectory stores a local recovery copy and workspace
path mappings. It is separate from the program directory and your chosen data
file. This copy and matching conflict copies can be merged automatically when
the workspace is opened or refreshed, protecting saved changes against delayed
file replacement. Recovery is not an independent backup history. Unsaved form
input exists in memory only.

## Synchronization and Windows reminders

If you place the JSON in Nextcloud or another synchronized folder, that service
handles transfer, retention and backups under its own privacy policy. The app
does not connect to that provider. This edition supports sequential use on your
own devices; wait for synchronization before changing devices.

Windows stores scheduled reminders and may display task titles/descriptions on
the lock screen or while the app is closed, according to your notification settings.
Selecting another workspace updates the app's scheduled reminders.

## Updates and external links

The Store edition receives updates through Microsoft Store according to Windows
settings. Its update button opens its Store page; it does not query GitHub for
updates or upload board data. Microsoft's services follow the
[Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).
Windows and the bundled Microsoft runtime components are also subject to
Microsoft's diagnostic-data policies and the applicable Windows settings.

Opening documentation, support or privacy links in a browser contacts that website.
GitHub's handling of those visits and submitted issues is described in the
[GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
Do not post personal workspace files in public issues. Local test editions may
stage a manually chosen installer temporarily; old staging is cleaned on startup.

## Removal and contact

Uninstalling the app does not delete a JSON file stored outside its app profile.
To remove board information, also consider local Recovery files, conflict copies,
manual backups, your sync provider's version history and scheduled notifications.
The app does not promise secure erasure.

For privacy questions, contact Graphsto through
[the project's Issues](https://github.com/Graphsto/Kanban-Tasker-Solosync/issues)
without including personal information. For security-sensitive reports, use
[private vulnerability reporting](https://github.com/Graphsto/Kanban-Tasker-Solosync/security/advisories/new).
