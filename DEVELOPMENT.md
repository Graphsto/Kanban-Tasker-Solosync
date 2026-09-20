# Development and contributing

This guide covers working on Kanban Tasker, building a fork and producing local test packages. For the app itself, see the [README](README.md).

## Prerequisites

For Windows builds, use:

- Windows 10 or Windows 11, with an interactive desktop for UI tests.
- The .NET SDK selected by [global.json](global.json): **10.0.302**, or a later patch in the same feature band.
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
.\scripts\test-setup.ps1
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
| Core tests | Portable tests on Ubuntu 24.04 and Windows Server 2025, using `scripts/test.ps1`. Windows-specific file-lock tests run on Windows. |
| Windows Release | Desktop and Setup compiled for x64 and ARM64 on Windows Server 2025. ARM64 is a cross-build, not a hardware test. |
| Windows UI tests | The isolated x64 Desktop and Setup harnesses, using `scripts/test-desktop.ps1` and `scripts/test-setup.ps1`. No app installation or certificate trust changes. |
| CI passed | A single combined result; every preceding check must succeed. |

The workflow selects .NET through `global.json`, restores dependencies in locked mode and caches NuGet packages. It uploads test reports, UI screenshots and build logs for seven days, including available evidence after failures. It does not upload executables, create releases or sign packages. No repository secrets are required. Actions are pinned to full commit hashes; the workflow has read-only repository permissions and does not retain Git credentials.

The [main ruleset](.github/rulesets/main.json) requires **CI passed** from the GitHub Actions app, requires the branch to be up to date, and blocks branch deletion and force pushes. It has no bypass actors. Its initial enforcement is **Disabled** so it cannot block the first workflow push. A matching rule is prepared in this repository's GitHub settings. After pushing the workflows and verifying a successful CI run on the current `main` commit, change this rule to **Active** under **Settings → Rules → Rulesets**. In a fork, import the JSON there first. Subsequent changes should go through a pull request so CI can run before they reach `main`. Keep the check name stable and do not add path filters that could prevent required checks from running.

Local validation cannot establish that a GitHub-hosted runner has a working WinUI desktop session. The first hosted run, especially UI startup and rendering, still needs verification. These checks also do not replace Windows 10/11, ARM64 hardware, installation, reminder or real sync-client acceptance tests.

## Dependency updates and security checks

[Dependabot](.github/dependabot.yml) checks NuGet packages and GitHub Actions **monthly**, scheduled for the first day of the month at 09:00 Europe/Berlin. Each ecosystem has one update group and a limit of one open version-update PR, giving at most two open version-update PRs across the repository. All projects are referenced by the root solution. Automatic rebasing is disabled; update a branch manually if it conflicts with `main`. No automatic merge, reviewer or assignee is configured.

The monthly schedule does not control Dependabot security-update PRs. To preserve the requested monthly cadence, **Dependabot security updates** remains disabled in repository settings. Vulnerability alerts are a separate setting and are not changed by these files. If security-update PRs are enabled later, they may arrive outside the monthly schedule and are exempt from the version-update PR limit. GitHub can also run an initial check when the configuration is first added or changed. Grouping reduces PR activity; it cannot guarantee a particular number of emails. Personal delivery settings are under [GitHub notification settings](https://github.com/settings/notifications).

[CodeQL](.github/workflows/codeql.yml) analyzes C# and GitHub Actions on pushes and pull requests targeting `main`, weekly, and on manual runs. C# uses an explicit Release build of the solution, including WinUI-generated code, with shared compilation disabled. The workflow uses the `security-extended` queries. The analysis job has `security-events: write` for findings and `actions: read` for private-repository run metadata; other repository access is read-only. This detects potential issues; it is not a full security audit or a promise of a vulnerability-free release.

The repository is currently personal and private, where GitHub Code Security and native Secret Scanning/Push Protection are not available for this repository. CodeQL jobs therefore remain skipped while it is private. They become eligible automatically after a deliberate change to public visibility. An eligible private fork with licensed Code Security already enabled may opt in with the repository Actions variable `CODEQL_ENABLED=true`; setting the variable does not buy or enable the service. Do not enable CodeQL default setup alongside this advanced workflow.

The Gitleaks CI job works while the repository is private and is included in **CI passed**. It detects secrets after a push, so it does not replace GitHub's server-side Push Protection. Update the pinned Gitleaks version and SHA-256 together when adopting a new CLI release. Before public distribution, enable/verify native **Secret scanning** and **Push protection** under **Settings → Advanced Security**. GitHub rejected their activation on the current private repository; they are not currently protecting pushes. No repository visibility, paid security plan or personal notification preferences were changed.

Signed release packaging should use a separate protected workflow once the public signing and distribution strategy is decided.

## Package and verify

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

For an independently distributed fork, choose your own package identity, publisher and signing key. Review `scripts/Package.appxmanifest.template`, the Desktop resource-index identity, installer/update checks, local-profile paths and their tests together; changing only the display name is insufficient. Update repository/support links and branding, preserve the original MIT copyright and license, and retain third-party notices. Do not present a fork as an update signed by this project's publisher.

## Storage and product boundaries

Keep this edition a solo application with one chosen UTF-8 JSON workspace, without requiring a server or an account. Sequential transfer between the user's devices is supported; simultaneous collaborative editing is not promised.

The versioned document contains stable GUIDs, groups, boards, columns, tasks and field-level change stamps. Format **2** adds a `groups` collection and a nullable, versioned `groupId` field on every board. Format 1 is deliberately unsupported; this pre-release change has no migration. Older files are rejected without overwriting them. All devices sharing a workspace need a compatible app version.

Groups have independent name stamps and terminal deletion markers. Deleting a group never deletes boards or tasks: references to deleted groups are displayed as ungrouped, including assignments from delayed files. Group visibility and the selected filter are local preferences; groups and board assignments are workspace data. Disabling the feature must not rewrite those assignments. Unsaved task drafts survive incoming reassignments by revealing all boards when necessary.

Tasks reference columns by ID. Merge compares UTC milliseconds, logical counter and device ID; deletion markers remain terminal. Order lists merge as whole fields, with display projection filtering stale IDs and deterministically adding missing live items. See the implementation in [WorkspaceDocument.cs](src/KanbanTasker.Core/WorkspaceDocument.cs), [WorkspaceMerge.cs](src/KanbanTasker.Core/WorkspaceMerge.cs) and the corresponding tests.

[IWorkspaceStore](src/KanbanTasker.Core/WorkspaceStore.cs) serialises access, rereads and merges before publication, and atomically replaces the primary file using a temporary file in the same directory. It retains local recovery and reads matching sibling conflict copies; it does not delete those copies. Semantically unchanged files are not rewritten. Missing, malformed or unsupported primary files must not be replaced with an empty workspace.

Preferences, device identity and recovery live beneath `%LOCALAPPDATA%\KanbanTasker.Revived` (Windows may virtualise this location for packaged apps). Recovery is a latest merged state, not a backup history or a rollback feature. Opening an older copy can merge newer local changes back into it. Plain text edits without updating field stamps are not a supported mutation API; use `WorkspaceEditor` for programmatic changes.

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

Run `scripts/export-notices.ps1` after restoring dependencies to collect package-provided licenses, notices and `dependencies.json`. Packaging includes these with the app and embedded installer payload. This is a build-input inventory, not an automatic license clearance or a complete binary SBOM.

Preserve [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Before public distribution, review the actual shipped dependencies and redistribution terms, including native runtime components. Keep release-specific notes with the release rather than adding an implementation history to the user README.
