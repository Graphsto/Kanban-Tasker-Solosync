# Development and contributing

This guide covers working on Kanban Tasker SoloSync, building a fork and producing local test packages. For the app itself, see the [README](README.md). For Store builds and manual release preparation, see [STORE.md](STORE.md).

## Prerequisites

For Windows builds, use:

- Windows 10 or Windows 11, with an interactive desktop for UI tests.
- The exact .NET SDK selected by [global.json](global.json): **10.0.401**, delivering self-contained runtime **10.0.12**. Automatic SDK roll-forward is disabled because SDK-provided packages such as ILLink are also captured in the lock files. Update the SDK, runtime acceptance check and affected lock files together.
- Windows SDK **10.0.26100** with `makeappx.exe`, `makepri.exe` and `signtool.exe` for packaging. The packaging script expects its standard installation location.
- PowerShell 7 for the repository scripts.

Open the repository root in VS Code and use its terminal. Visual Studio is optional; Visual Studio 2026 with WinUI tooling can open `KanbanTasker.sln`. The `.csproj` files and solution also work with the .NET CLI.

The application targets Windows build 19041 or later; Windows 10 22H2 is the Windows 10 acceptance target. Desktop and Setup builds are self-contained. End users do not need to install the SDK, Visual Studio or a separate .NET runtime.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/KanbanTasker.Core/` | Platform-independent document model, commands, validation, merge, atomic storage and recovery. |
| `src/KanbanTasker.Desktop/` | WinUI 3 shell, cards and columns, task editor, calendar, local preferences and Windows reminders. |
| `src/KanbanTasker.Setup/` | Standalone installer, payload verification, certificate trust helper and package deployment. |
| `src/Shared/` | Update validation and the five localisation catalogs. |
| `tests/KanbanTasker.Tests/` | Portable tests for the core and shared application logic. |
| `tests/DesktopSmokeTests.cs` | Isolated WinUI control tests and rendered test evidence. |
| `tests/DesktopGroupTests.cs` | Group management, filtering, drafts and incoming group changes through real controls. |
| `tests/DesktopShowcase.cs` | Fictional data and capture steps for README screenshots. |
| `tests/SetupSmokeTests.cs` | Installer control tests, without installing the app. |
| `scripts/` | Build, test, packaging and asset-generation commands. |
| `packaging/` | Shared Local/Store distribution profiles and supplemental upstream license text. |
| `branding/` | Source logo and committed documentation screenshots. |
| `build/` | Generated output; ignored by Git. |

[Directory.Build.props](Directory.Build.props) centralises binaries and restore metadata under `build/bin/<project>/` and `build/obj/<project>/`. Packages, staging directories, reports and test profiles also stay under `build/`. Do not commit these outputs, private signing keys or personal workspace files.

The local `docs/`, `.vscode/`, `.vs/`, `branding/context/` and `AGENTS.md` paths are intentionally ignored. They are not required for a fresh checkout; keep shared contributor instructions in tracked files such as this one.

## Build and run

Run from the repository root:

```powershell
.\scripts\build.ps1
.\scripts\build.ps1 -Configuration Release -Architecture x64
```

`build.ps1` restores locked dependencies, builds Desktop and runs the portable tests. Use `-Architecture arm64` to cross-build for ARM64.

Launch an unpackaged development build with:

```powershell
.\build\bin\KanbanTasker.Desktop\debug_win-x64\KanbanTasker.exe
```

Ordinary development builds use the normal local app profile. Use a disposable workspace for development. The UI harnesses below use their own isolated profiles instead. Validate scheduled Windows reminders with the installed MSIX; they depend on package identity and notification settings.

Dependencies are pinned in project files and checked-in `packages.lock.json` files. Scripts restore in locked mode. For an intentional dependency change, update the project reference, run `dotnet restore --force-evaluate` for the affected project and review the lock-file diff. Self-contained releases bundle runtimes, so runtime fixes require a rebuilt and redistributed package.

## Tests

```powershell
.\scripts\test.ps1
.\scripts\test-desktop.ps1
.\scripts\test-desktop.ps1 -Channel Store
.\scripts\test-setup.ps1
node --test .github/scripts/release.test.cjs
```

- **Portable tests** exercise merge convergence, field conflicts, ordering, deletion, file failures, recovery, input validation and shared logic. Results: `build/test-results/core-tests.trx`. The core does not require an installed Windows app. On another platform with a compatible .NET SDK, restore `tests/KanbanTasker.Tests/KanbanTasker.Tests.csproj` with `dotnet restore --locked-mode`, then run `dotnet test` on that project with `--no-restore`.
- **Desktop tests** publish a separate harness, open their own test windows and exercise actual controls. Workspace, first-run, missing-file and invalid-file scenarios leave reports and PNGs under `build/ui-tests/<run>/`.
- **Setup tests** open an isolated installer harness and leave evidence under `build/setup-ui-tests/<run>/`. They do not install a package or change certificate trust.

UI tests require a Windows desktop session and should run on the selected architecture. The test-only `KanbanUiSmokeTest` and `KanbanSetupSmokeTest` builds must never be distributed. Packaging rejects the Desktop test flag and verifies that harness code is absent.

A successful build or control test is not full release acceptance. Before distributing changes, check the relevant flows on Windows 10 22H2 and Windows 11, including keyboard use, display scaling, light/dark/contrast themes, real installation/update, and reminders with the app closed. Exercise ARM64 on actual ARM64 Windows when shipping it. Test sequential file transfer with two devices and the real sync client. Record unavailable environments as untested, not passed.

## GitHub Actions CI

[CI](.github/workflows/ci.yml) runs for pushes to `main`, pull requests targeting `main` (including forks), and manual **Actions → CI → Run workflow** requests. A newer run cancels an older run for the same branch or pull request.

| Check | Scope |
| --- | --- |
| Secret scan | Gitleaks scans the checked-out Git history with redacted output. The CLI version and download checksum are pinned; no secret reports are uploaded. |
| Core tests | Portable tests on Ubuntu 24.04 and Windows Server 2025, using `scripts/test.ps1`. Tests for Windows read-only attributes and delete-sharing locks run on Windows and are explicitly reported as skipped on Linux. |
| Windows Release | Desktop and Setup compiled for x64 and ARM64 on Windows Server 2025. ARM64 is a cross-build, not a hardware test. |
| Windows UI tests | The isolated x64 Desktop harness for both Local and Store profiles, plus the Setup harness. Includes Store-launch failure and draft retention tests. No app installation or certificate trust changes. |
| Store bundle and release policy | Builds and validates the unsigned x64/ARM64 bundle, checks shipped binaries and notices, and tests release gates and safe retries. |
| CI passed | A single combined result; every preceding check must succeed. |

The workflow selects .NET through `global.json`, restores dependencies in locked mode and caches NuGet packages. It uploads test reports, UI screenshots and build logs for seven days, including available evidence after failures. It does not upload executables, create releases or sign packages. No repository secrets are required. Actions are pinned to full commit hashes; the workflow has read-only repository permissions and does not retain Git credentials.

The **active** main ruleset requires **CI passed** from GitHub Actions, an up-to-date branch, and blocks branch deletion and force pushes, with no bypass actors. Changes go through pull requests. Keep the check name stable and avoid path filters that could prevent required checks from running. The [ruleset template](.github/rulesets/main.json) starts disabled for bootstrapping a fork; import it, run CI successfully, then activate it in Settings → Rules → Rulesets.

Hosted Windows CI has exercised the WinUI harness successfully. These checks do not replace Windows 10/11, ARM64 hardware, installation, reminder or real sync-client acceptance tests. Every release must inspect the results for its exact commit.

## Dependency updates and security checks

[Dependabot](.github/dependabot.yml) checks NuGet packages and GitHub Actions **monthly**, scheduled for the first day of the month at 09:00 Europe/Berlin. Each ecosystem has one update group and a limit of one open version-update PR, giving at most two open version-update PRs across the repository. All projects are referenced by the root solution. Automatic rebasing is disabled; update a branch manually if it conflicts with `main`. No automatic merge, reviewer or assignee is configured.

The monthly schedule does not control Dependabot security-update PRs. To preserve the requested monthly cadence, **Dependabot security updates** remains disabled in repository settings. Vulnerability alerts are a separate setting and are not changed by these files. If security-update PRs are enabled later, they may arrive outside the monthly schedule and are exempt from the version-update PR limit. GitHub can also run an initial check when the configuration is first added or changed. Grouping reduces PR activity; it cannot guarantee a particular number of emails. Personal delivery settings are under [GitHub notification settings](https://github.com/settings/notifications).

[CodeQL](.github/workflows/codeql.yml) analyzes C# and GitHub Actions on pushes and pull requests targeting `main`, weekly, and on manual runs. C# uses an explicit Release build of the solution, including WinUI-generated code, with shared compilation disabled. The workflow uses the `security-extended` queries. The analysis job has `security-events: write` for findings and `actions: read` for private-repository run metadata; other repository access is read-only. This detects potential issues; it is not a full security audit or a promise of a vulnerability-free release.

This repository is public and CodeQL is enabled. A private fork without licensed Code Security skips analysis; an eligible private fork may opt in with the Actions variable `CODEQL_ENABLED=true`. The variable does not buy or enable the service. Do not enable CodeQL default setup alongside this advanced workflow. Release preparation requires successful CodeQL on the exact main commit when the repository is public.

The Gitleaks job is included in **CI passed**, including in private forks. Update its pinned version and SHA-256 together. Native **Secret scanning**, **Push protection** and **private vulnerability reporting** are enabled in this public repository. Verify them in Settings when creating a fork; these settings are not inherited from source files. Gitleaks detects committed secrets after a push and complements server-side Push Protection.

The manual [Prepare release](.github/workflows/release.yml) workflow prepares an unsigned Store submission bundle and unpublished GitHub draft. Microsoft signs the Store distribution. Public EXE signing and a GitHub updater are deferred; see [STORE.md](STORE.md).

## Package and verify local test builds

```powershell
.\scripts\package.ps1                         # x64 and ARM64
.\scripts\package.ps1 -Architecture x64       # one architecture
.\scripts\package-installer.ps1              # rebuild EXEs from existing signed MSIX packages
.\scripts\verify-packages.ps1                # no installation
```

Finished output is in `build/packages/`:

- `KanbanTasker-Setup-<version>-x64.exe` and `-arm64.exe`.
- `KanbanTasker-<version>-x64.msix` and `-arm64.msix`.
- `KanbanTasker.Local.cer`, the public certificate for these local test packages.

`package.ps1` publishes a fresh self-contained app per architecture, validates its XAML resource index and package identity, includes dependency notices, signs the MSIX, then builds and signs a self-contained Setup.exe containing that exact package. Setup uses Windows deployment APIs; it does not run PowerShell.

`verify-packages.ps1` checks both architectures' signatures, metadata, versions, localisation resources, notices and harness exclusion, and runs the x64 installer's payload verification. To diagnose one installer without installing anything:

```powershell
.\build\packages\KanbanTasker-Setup-<version>-x64.exe --verify-only
```

Replace `<version>` with the generated filename. Cross-building ARM64 and validating its package does not test ARM64 execution.

### Signing and local installation

The scripts use or create a non-exportable code-signing key in the current user's Windows certificate store. Only the public certificate is exported. Use `-CertificateThumbprint` with `package.ps1` to select an existing compatible signing certificate. Its subject must match the configured publisher, currently `CN=KanbanTasker.Local`.

Build and package scripts do not install the app or change certificate trust. Test Setup.exe as the intended user: it requests administrator approval only for trusting the certificate in **Local Computer → Trusted People** and registers the app for the original user. It does not install a root CA or disable signature validation. A private test certificate is not a publicly trusted publisher certificate; establish the intended distribution/signing channel before a public release.

For managed/manual MSIX testing, first inspect the certificate and package source. An administrator can trust the corresponding public certificate with:

```powershell
Import-Certificate -FilePath .\build\packages\KanbanTasker.Local.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then, as the intended user:

```powershell
Add-AppxPackage -Path .\build\packages\KanbanTasker-<version>-x64.msix
```

Errors `0x800B010A` / `0x800B0109` indicate an unverified certificate chain. Trusting the certificate only in Current User or Personal does not satisfy this installation flow. Never commit private keys or include them in a release.

### Versions and forks

The four-part version in `Directory.Build.props` is the source for app and installer packaging. `package.ps1 -Version <four-part-version>` is available for a deliberate local override. Keep release versions and the application manifests consistent.

Small fixes and additions increase only the fourth component, for example `2.4.1.0` → `2.4.1.1`. Substantial changes increase the third and reset the fourth. The first two components require an explicit maintainer decision. Never renumber an existing release. Documentation-only changes do not need a version bump.

Updates require a higher version and compatible package identity/publisher/signing trust. Replacing a file without increasing its version does not update an installed app. This edition uses identity `KanbanTasker.Revived`, separate from the original Store app.

For an independently distributed fork, choose your own identity, publisher, Store ID, profile directory and signing strategy in `packaging/Distribution.props`. The manifest, Desktop resource index and app metadata read this common profile. Review installer/update checks, certificate tooling and tests together; changing only the display name is insufficient. Update repository/support links and branding, preserve the original MIT copyright and license, and retain third-party notices. Do not present a fork as an update signed by this project's publisher.

## Storage and product boundaries

Keep this edition a solo application with one chosen UTF-8 JSON workspace, without requiring a server or an account. Sequential transfer between the user's devices is supported; simultaneous collaborative editing is not promised.

The versioned document contains stable GUIDs, groups, boards, columns, tasks and field-level change stamps. Format **2** adds a `groups` collection and a nullable, versioned `groupId` field on every board. Format 1 is deliberately unsupported; this pre-release change has no migration. Older files are rejected without overwriting them. All devices sharing a workspace need a compatible app version.

Groups have independent name stamps and terminal deletion markers. Deleting a group never deletes boards or tasks: references to deleted groups are displayed as ungrouped, including assignments from delayed files. Group visibility and the selected filter are local preferences; groups and board assignments are workspace data. Disabling the feature must not rewrite those assignments. Unsaved task drafts survive incoming reassignments by revealing all boards when necessary.

Tasks reference columns by ID. Merge compares UTC milliseconds, logical counter and device ID; deletion markers remain terminal. Order lists merge as whole fields, with display projection filtering stale IDs and deterministically adding missing live items. See the implementation in [WorkspaceDocument.cs](src/KanbanTasker.Core/WorkspaceDocument.cs), [WorkspaceMerge.cs](src/KanbanTasker.Core/WorkspaceMerge.cs) and the corresponding tests.

[IWorkspaceStore](src/KanbanTasker.Core/WorkspaceStore.cs) serialises access, rereads and merges before publication, and atomically replaces the primary file using a temporary file in the same directory. It retains local recovery and reads matching sibling conflict copies; it does not delete those copies. Semantically unchanged files are not rewritten. Missing, malformed or unsupported primary files must not be replaced with an empty workspace.

Preferences, device identity and recovery live beneath `%LOCALAPPDATA%\KanbanTasker.Revived` for Local builds and `%LOCALAPPDATA%\KanbanTasker.SoloSync.Store` for Store builds (Windows may virtualise these locations). Profiles are not automatically migrated. Recovery is a latest merged state, not a backup history or a rollback feature. Opening an older copy can merge newer local changes back into it. Plain text edits without updating field stamps are not a supported mutation API; use `WorkspaceEditor` for programmatic changes.

Protect these invariants when contributing. Keep fixes focused, use artificial test data and add regression tests for data loss or save failures. Explain user-visible behaviour, test results and any remaining manual checks in your pull request. Follow [SECURITY.md](SECURITY.md) for security reports.

## Languages, icons and screenshots

Interface catalogs live in `src/Shared/Localization/`: English, German, Spanish, French and Italian. Update all five catalogs when adding UI text and preserve format placeholders. Board names, task contents and stable priority values in JSON are user data, not translatable UI text.

Regenerate the app icons after an intentional source-logo change:

```powershell
.\scripts\export-icons.ps1
```

The source is `branding/kanban-tasker-win11-v3.png`; committed PNG/ICO exports live in `src/KanbanTasker.Desktop/Assets/`.

Regenerate the seven README screenshots with:

```powershell
.\scripts\capture-screenshots.ps1
```

This builds the isolated WinUI harness under `build/showcase/<run>/`, creates a fictional project and renders the actual controls to PNG. It does not read the normal app profile, install a package or schedule reminders. Only the displayed status-bar path is shortened to the demo filename. The board, editor, drag preview, calendar and settings are the real app UI; dialog captures contain just the dialog surface. Calendar controls can follow Windows regional settings.

The script copies the seven images to `branding/screenshots/`, which is intentionally tracked. Review all images before committing: text must be legible, controls should not be cut off, and no private paths or user tasks should appear. The fixture uses September 2026 dates; update those together with the calendar selection when refreshing the examples. No screenshot flags or demo data are included in normal packages.

## Licenses and release notices

Run `scripts/export-notices.ps1` after restoring dependencies to collect package-provided licenses, notices and `dependencies.json`. The Store packager additionally passes its actual payload for SHA-256 attribution of every shipped DLL/EXE in `shipped-binaries.json`, and rejects unknown binaries or absent license texts. Build-input and shipped-binary inventories serve different purposes; neither is automatic legal clearance. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for scope and upstream terms.

Preserve [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Before public distribution, review the actual shipped dependencies and redistribution terms, including native runtime components. Keep release-specific notes with the release rather than adding an implementation history to the user README.
