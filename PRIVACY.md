# Privacy — Kanban Tasker SoloSync

Maintained by Graphsto. Last updated: 8 October 2026.

Local boards require no account. Kanban Tasker SoloSync has no advertising,
application telemetry or developer-operated cloud storage. Board contents are not
sent to Graphsto. Optional GitHub Projects uses your GitHub account and GitHub's API.

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

## Optional GitHub Projects

Only when you enable GitHub and authorize its registered App, the client contacts
`github.com` for device-code authorization/token refresh and `api.github.com` for
organization projects, views, permissions and cards. It periodically reads linked
projects and sends the draft text, status, card order or Status-option names you
save. GitHub receives account authentication and normal network metadata such as
your IP address. These requests follow the
[GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

Access and refresh tokens are stored separately using Windows DPAPI protection
for the current Windows user. The build includes only the public App Client ID;
it contains no Client Secret or private key. A **GitHub** subdirectory of the local
profile stores the active account ID/login and, separately per account, links,
project/card caches and a journal of incomplete or uncertain writes. Cached
contents and that journal are plain local JSON and can contain draft text. They
are separate from the chosen workspace JSON and its recovery/Nextcloud transfer.
Only fully loaded projects replace cached data. No write queue is stored for
offline editing, and GitHub cards do not schedule Windows reminders in this version.

Signing out removes local tokens, but retains links, cached contents and the
operation journal for offline reading and later review. Unlinking a view also
retains the project cache. To remove these local records, sign out, close the app
and remove the profile's GitHub directory. Also consider device backups. Revoke
the App authorization and/or installation on GitHub to remove server-side access.
Removing a local link or cache never deletes GitHub project data. Unsaved editor
input is held in memory only and is discarded when the app closes.

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
