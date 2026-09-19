# Kanban Tasker

A small Windows kanban app for solo use, with a WinUI 3 interface and one local JSON data file. Built with .NET 10 for Windows 10 and Windows 11, on x64 and ARM64.

Source repository: [Graphsto/Kanban-Tasker-Solosync](https://github.com/Graphsto/Kanban-Tasker-Solosync).

This is an independent local-file edition of [Hunter Johnson's Kanban Tasker](https://github.com/hjo12/kanban-tasker-uwp), under the original MIT license. It installs alongside the original Store app. There is no Microsoft sign-in, OneDrive API, Syncfusion license, telemetry service, database server, or SQLite dependency. Existing Store-app databases are not imported.

## Using the app

1. Choose **Create data file** or **Open data file**. The suggested filename is `KanbanTasker.kanban.json`.
2. Create a board. Its initial columns are Backlog, To Do, In Progress, Review and Completed.
3. Use **+** in a column to create a task. Select a card to edit it; commit changes with **Save**. Cancelling a draft leaves the saved task unchanged. A discard confirmation appears only when you have changed fields, including a tag you have not submitted. Opening a task without editing it, or undoing all edits, closes without a prompt.
4. Drag cards between columns or within a column. Dropping above the first card, including on the column heading or controls, highlights the whole column and inserts the card at the beginning, even when its list is scrolled. Drag a column's heading to change column order. The full card or column follows the pointer, with a subdued placeholder at its original position and an insertion marker at the destination. Escape cancels the move. Column menus also provide **Move left/right**; card context menus provide keyboard-accessible movement. Cards appear immediately without a loading animation on startup or board updates.
5. Column menus rename columns, change task limits, or delete columns and their tasks. The visible up/down arrows change the limit immediately by one; typed numbers remain supported. A limit of zero means unlimited. Limits are visual warnings, not a restriction on dropping tasks.
6. **Calendar** opens the month view immediately. Dates with due tasks on the current board are highlighted; select a day to see its tasks and select a task to edit it. Highlights and results update while the calendar is open. Tasks support priorities, tags, descriptions, creation/start/finish/due dates, due times and reminders. Windows handles scheduled reminders, including snooze/dismiss.

Settings opens on **General**, with data-file and update actions. The **Appearance** tab contains language and theme dropdowns. Choose **Use system setting**, **Dark**, **Light**, **Light blue** or **Dark blue**. The default follows Windows light/dark changes while the app is open. Windows contrast themes take precedence over custom blue colors. Language and theme apply immediately and are saved on the current device without changing workspace data or open task drafts. Board and column deletions require confirmation. Column collapse state and the selected board are saved on the current device.

On startup, the window is centered in its monitor's available work area, accounting for the taskbar and monitor position. The initial size fits smaller displays. Moving or resizing an open window does not recenter it.

## Nextcloud and similar sync folders

This edition is a **solo client**: work on one device, then continue on another after synchronization finishes. It does not provide cooperative editing or real-time collaboration. Before switching devices, save and close the app on the first device, wait for its sync client to finish uploading, then wait for the second device to finish downloading before opening the board there. Concurrent edits can produce conflict notifications from the sync client even when Kanban subsequently merges the data.

Put the data file in a synchronized local folder. On each other device, wait for the file to download and open that same file. Mark it **always available locally/offline** in your sync client. Each device remembers its own local path.

The app saves completed actions, monitors changes, and checks again on activation and periodically while running. **Saved locally** describes this device's file; it does not confirm upload or delivery to other devices.

The following merge/recovery safeguards protect against delayed or overlapping file versions; they are not a promise of simultaneous collaboration:

- Different tasks and independently edited fields are combined.
- Conflicting edits to one field use the greater stored timestamp, logical counter, then device ID. Device clocks should be synchronized: offline devices with inaccurate clocks cannot establish a perfect real-world edit order.
- Tags are one field. Each column order and each card order is one field. Concurrent reordering uses the winning order and appends missing items deterministically.
- Deletions win over concurrent edits. Old files cannot restore deleted entries.
- JSON conflict copies with the same document ID in the same folder are merged too. The app never deletes them.
- A durable local recovery copy preserves changes across failed writes, sync-client replacements and restarts.

Convergence requires the sync client to deliver files and affected devices to run the app again. There is no direct cloud connection or instant collaboration service. Keep provider-generated conflict copies until devices have reconciled.

If the file is missing, locked, read-only, damaged, or from an unsupported format version, the app keeps the last valid state and displays an error. It does not replace an invalid file with an empty workspace. If an action was already accepted into local recovery, the status says so and publication is retried. Unsaved task drafts remain in the open editor; they are not shared or persisted after intentionally discarding/closing them.

Use **Settings → General** to open/create another file. Opening a different workspace switches documents without merging unrelated data.

## Local updates

Version **2.4.1.3** adds a full-column drop highlight above the first card and inserts header drops at the beginning of the column.

Version **2.4.1.2** restores automatic field-level merge, conflict-copy handling and durable recovery from before 2.4.1.1, and removes the temporary manual recovery action. The independent security fixes for change counters, bounded input reading, update manifests and reminder calculations remain. The file-authoritative behavior in the superseded 2.4.1.1 test build was withdrawn at the user's request.

Version **2.4.1.0** shows the window once its XAML shell is loaded, reads the saved workspace and cleans old update files in the background, and creates the task editor only when first needed. This reduces startup work and avoids exposing the unpainted native window.

Version **2.4.0.0** adds the direct month calendar with highlighted due dates, immediately usable limit arrows, and moves installer cleanup off the UI thread so Finish can close the window without waiting for temporary-file deletion.

Version **2.3.2.0** disables card-list entrance, content and reorder animations that replayed across the whole board after a change. The card/column preview still follows the pointer during dragging.

Version **2.3.1.0** fixes transparent settings and confirmation dialogs in the dark theme, including **Use system setting** when Windows is dark. Dialogs now use an opaque background in every theme.

Version **2.3.0.0** adds General/Appearance settings tabs, five theme choices and discard confirmation only for changed task drafts.

Version **2.2.0.0** added visible card/column drag previews and live language selection, now in **Settings → Appearance → Language**: English, Deutsch, Español, Français and Italiano. The choice is saved on this device; existing profiles start in English. Menus, dialogs, validation messages, priorities, dates and scheduled reminder text use the selected language. Board/column names, tags and task content are user data and are not translated. Open task drafts survive language changes.

The workspace remains UTF-8 JSON and works across devices using different interface languages. Priority values remain `Low`, `Medium` and `High` in the file. Standard Windows file dialogs and external installers can use Windows' own language. See [localization maintenance](docs/localization.md).

Version **2.1.0.0** includes the selected **logo variant 3** in the app, window/taskbar icons, Start menu and installer.

Version **2.1.1.0** fixes startup window centering using the actual monitor work area and window size.

In **Settings → General → App updates**, choose **Install update from file…** and select a newer Kanban Tasker Setup.exe or MSIX stored on your PC. The app checks its Windows signature, signing certificate, identity, architecture and version. Equal or older versions are rejected with the installed and selected version numbers. After confirmation, the app closes and opens the installer. Changed task drafts require a separate discard confirmation; cancel the update and save them first if needed. Saved boards, the selected data-file path and local recovery data are retained.

For the first update from 2.0.1 or earlier, close Kanban Tasker and start the new **Setup.exe** directly, then select **Update**. Its new Settings button becomes available after that installation. Running the same installer again reports **already installed** instead of suggesting an update succeeded. The installer never forcibly closes a running app.

There is no online update check or download configured. Builds and tests are local; GitHub release integration is deferred.

## Install locally

For a normal installation or update, use one of these files from the build output `build/packages`. Installers are not included in a source-only checkout; generate them with `scripts/package.ps1`. Existing local test packages were retained in the migration archive described in [project handoff](docs/project-handoff.md).

- **`KanbanTasker-Setup-2.4.1.3-x64.exe`** for Intel/AMD PCs.
- **`KanbanTasker-Setup-2.4.1.3-arm64.exe`** for ARM64 PCs.

1. Copy the appropriate **Setup.exe** to the receiving PC. This single file contains the app package, its public signing certificate and the installer's runtime.
2. Close Kanban Tasker. Double-click the installer normally and select **Install** or **Update**. Do not start it under a different administrator account.
3. On first installation, approve the Windows administrator prompt for certificate trust. Select **Finish**, then open **Kanban Tasker** from Start.

No PowerShell script, PowerShell 7, separately installed .NET runtime or Visual Studio is needed. Minimum OS build is Windows 10 2004 (19041); the Windows 10 acceptance target is 22H2. Administrator approval is required once on each PC to trust this privately signed app. A managed PC may require its administrator to perform that trust step.

The installer checks its embedded package hash, identity, architecture, version and certificate before installation. Only certificate trust is elevated, using **Local Computer → Trusted People**; app registration runs under the original user, including when different administrator credentials are entered in the Windows prompt. Windows validates the MSIX signature during deployment. No execution policy or signature validation is disabled, and no root CA is installed.

For diagnostics, `KanbanTasker-Setup-2.4.1.3-x64.exe --verify-only` checks the included payload without installing the app or changing certificate trust.

The raw `KanbanTasker-2.4.1.3-x64.msix` / `arm64.msix` packages and `KanbanTasker.Local.cer` are still available for manual/managed deployment. The old `scripts/install.ps1` remains an optional developer tool, but is not required or recommended for normal installation.

**Errors 0x800B010A / 0x800B0109 (publisher certificate cannot be verified):** the local signing certificate must be trusted on each receiving PC before opening the MSIX. Importing it under Current User or Personal is insufficient for App Installer. This build uses a private self-signed certificate; it is not signed by a public certificate authority. See [Microsoft's certificate trust instructions](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide#testing-distribute-to-testers-with-a-self-signed-certificate).

For manual installation, an administrator can run:

```powershell
Import-Certificate -FilePath .\KanbanTasker.Local.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then install the package as the intended user:

```powershell
Add-AppxPackage -Path .\KanbanTasker-2.4.1.3-x64.msix
```

Build scripts do not change certificate trust or install the app. For updates, keep the same certificate and increase the four-part `Version` in `Directory.Build.props`. Packaging uses that value for both the app and installer; `-Version` overrides it for a local test build. Replacing an EXE/MSIX without increasing its version does not update an installed app. Identity: `KanbanTasker.Revived`, publisher: `CN=KanbanTasker.Local`; the original Store package is unaffected.

Version policy: small changes and fixes increase only the fourth component, for example `2.4.1.0` → `2.4.1.1`. Only substantial changes increase the third component and reset the fourth, for example `2.4.1.1` → `2.4.2.0`. The first two components stay unchanged unless the user explicitly requests otherwise. Do not renumber previous releases; documentation-only changes do not require an app version bump. These rules are also recorded in [AGENTS.md](AGENTS.md).

## Build and test

Requirements: .NET SDK **10.0.302** (or a later patch in that feature band), Windows SDK **10.0.26100** with MSIX tools, and Windows. Open the repository root in VS Code and run the scripts below from its terminal. The `.csproj` files and root solution are also used by the .NET CLI. Visual Studio 2026 with WinUI tools remains an optional IDE.

```text
src/
  KanbanTasker.Core/
  KanbanTasker.Desktop/
  KanbanTasker.Setup/
  Shared/
tests/
  KanbanTasker.Tests/
  DesktopSmokeTests.cs
  SetupSmokeTests.cs
scripts/
branding/
docs/                       # local project documentation
build/                      # generated; ignored by Git
KanbanTasker.sln
Directory.Build.props
global.json
```

`Directory.Build.props` uses the [.NET SDK's centralized output layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output) to put build outputs and restore metadata in `build/bin/<project>/...` and `build/obj/<project>/...`. Scripts use the same root for packages, staging directories and test reports. `build/packages` contains finished installers/MSIX; `build/test-results`, `build/ui-tests` and `build/setup-ui-tests` contain test evidence. All generated output is excluded by `/build/` in `.gitignore`. Existing local exclusions for editor settings, project notes and branding context are retained. Source assets, test code, scripts and dependency lock files remain part of the repository.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\test-desktop.ps1          # isolated WinUI control test; shows its own test window
.\scripts\test-setup.ps1            # isolated installer window test; does not install anything
.\scripts\package.ps1                 # both architectures, self-contained and signed
.\scripts\package.ps1 -Architecture x64 -Version 2.4.1.3
.\scripts\package-installer.ps1       # rebuild Setup.exe from existing signed MSIX files
.\scripts\verify-packages.ps1         # signatures, metadata, notices and x64 payload; no installation
.\scripts\export-notices.ps1          # restored dependency inventory and supplied license texts
```

Packaging uses or creates a non-exportable local code-signing key in the current user's Windows certificate store. Only its public certificate goes into the output. Pass `-CertificateThumbprint` to use an existing compatible certificate.

`package.ps1` builds both MSIX and a signed, self-contained single-file EXE installer per architecture. `package-installer.ps1` can rebuild only the EXEs, verifying the existing package signer and embedding that exact package and public certificate. The installer calls Windows package deployment APIs directly; it does not run PowerShell.

Version 2.0.1 fixes the installed-app startup failure in the initial 2.0.0 package: publishing now includes the XAML resource index with the MSIX identity. Packaging checks the index and embedded App/MainWindow resources before signing. Replace old 2.0.0 packages with 2.0.1 or later.

Dependencies are pinned in project files and checked-in `packages.lock.json` files. Scripts restore in locked mode. For intentional dependency changes, run `dotnet restore --force-evaluate` and review the lock-file changes.

The committed PNG/ICO exports in `src/KanbanTasker.Desktop/Assets` are generated from `branding/kanban-tasker-win11-v3.png` by `scripts/export-icons.ps1`. It creates all supported Windows scale/target sizes and the multi-resolution executable icon. Run it after an intentional source-logo change. The original comparison icon and other design variants remain in `branding/context`; obsolete UWP/API/database directories are no longer part of this source tree.

Open `KanbanTasker.sln`:

| Project | Responsibility |
| --- | --- |
| `src/KanbanTasker.Core` | Document model, merge, commands, validation, atomic storage and recovery; no Windows UI dependency. |
| `src/KanbanTasker.Desktop` | WinUI shell, native pickers, cards/columns, task editor, calendar and Windows reminders. |
| `tests/KanbanTasker.Tests` | Concurrent edits, deletion, ordering, file failures and recovery tests. |
| `src/KanbanTasker.Setup` | Standalone installer, embedded payload checks, certificate trust helper and Windows package deployment. |

Unpackaged development builds can be launched from `build/bin/KanbanTasker.Desktop/debug_win-x64` (or the corresponding Release/ARM64 folder). Accept scheduled notifications against the installed MSIX: they depend on app identity and Windows notification settings.

Preferences, device identity and recovery files live beneath `%LOCALAPPDATA%\KanbanTasker.Revived` (Windows may virtualize this for packaged apps). The workspace stays in its chosen location. Recovery is automatically reconciled and is not a backup history or a way to deliberately roll back a workspace.

See [file format](docs/file-format.md), [verification status](docs/verification.md), [release preparation](docs/release-readiness.md), [privacy](docs/privacy.md), [security reporting](SECURITY.md) and [project handoff](docs/project-handoff.md). Historical UWP screenshots and local build/test outputs were archived outside the source tree.

## License

[MIT](LICENSE). Original application and image assets: copyright 2019 hjohnson12. Original attribution is preserved. Dependencies have their own terms; see [third-party notices](THIRD-PARTY-NOTICES.md).
