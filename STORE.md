# Store publication and releases

Kanban Tasker SoloSync is distributed through Microsoft Store first. Microsoft signs the submitted MSIX packages and distributes installations and updates. The MIT source license is independent of this code signature.

The GitHub repository contains source, release notes and release drafts. Do not publish the local test installer, its certificate, or the unsigned submission bundle as an end-user download. There is no GitHub update check or download server in the app.

## Distribution profiles and versions

[packaging/Distribution.props](packaging/Distribution.props) is the common source for manifest identity, PRI resource-index identity and app settings.

| Setting | Store | Local development/testing |
| --- | --- | --- |
| Product name | Kanban Tasker SoloSync | Kanban Tasker SoloSync |
| Package identity | Graphsto.KanbanTaskerSoloSync | KanbanTasker.Revived |
| Publisher | CN=A5B79554-4F59-4D3F-984A-538E871FFD00 | CN=KanbanTasker.Local |
| Publisher display name | Graphsto | Kanban Tasker Local |
| Package family | Graphsto.KanbanTaskerSoloSync_vr8jpynpazs8y | Separate local identity |
| Store product ID | 9P7HG1RBVBW6 | Not a Store installation |
| Update action | Open the Microsoft Store product page | Select a local signed test installer |
| Profile directory under LocalAppData | KanbanTasker.SoloSync.Store | KanbanTasker.Revived |

Windows may virtualise the profile directory for packaged apps. No settings or recovery data are copied across channels. On the first Store launch, choose **Open data file** and select the existing format-2 JSON. The original Store app and local test edition can remain installed alongside this edition; avoid editing the same file simultaneously.

Two versions are defined in [Directory.Build.props](Directory.Build.props):

- **Version = 2.4.1.8:** visible product version, assembly version, GitHub tag `v2.4.1.8`, and local test package/installer version.
- **StorePackageVersion = 2.4.1.0:** MSIX and bundle version. Microsoft reserves the fourth component, so it stays zero. Each further submission advances the third component, for example product `2.4.1.9` with Store package `2.4.2.0`. This internal counter is not a product feature version.

The starting Store number assumes that no equal or higher version has already been submitted in Partner Center. **Check Partner Center before the first upload.** GitHub cannot inspect rejected, withdrawn or private Store submissions; reserve a new Store number for every subsequent submission even if the previous submission was not published. Keep both values and their mapping in source control. Neither workflow changes them automatically.

The app always displays the product version. The Store button opens `ms-windows-store://pdp/?ProductId=9P7HG1RBVBW6`; if launching the Store fails, it offers [the web listing](https://apps.microsoft.com/detail/9P7HG1RBVBW6). It does not guarantee an update is immediately available, close the app or discard drafts. Store-managed updates follow Windows settings, independently of this button. An offline Store that opens successfully reports its own network error.

## Prepare a release

1. Merge the intended changes and version edits into `main` through a PR. Wait for **CI** and **CodeQL** to succeed on the resulting main commit.
2. Open **Actions → Prepare release → Run workflow**, selecting **main**. The workflow fixes its source at the dispatch commit; if main advances before final validation, start a new preparation.
3. The workflow checks version ordering, unused tags and successful checks for that exact commit. It builds with SDK 10.0.401, locked dependencies and self-contained .NET 10.0.12.
4. Download the `store-submission-<product-version>-<run>-<attempt>` Actions artifact. It contains the x64/ARM64 MSIX bundle, `SHA256SUMS.txt`, `build-report.json`, `verification.json`, packaging logs and architecture-specific license/binary inventories. Archive it outside Actions before rerunning jobs or before its 30-day retention expires: GitHub can remove the preceding attempt's artifacts when a job is rerun.
5. Complete the acceptance below, then upload **only the verified .msixbundle** to the matching Partner Center product. Uploading, submission and publication are manual.
6. The workflow creates a **draft** GitHub release with generated change notes, the exact source commit, both versions and the Store link. Review its notes. After Store certification and confirmed availability, publish that draft manually.

No release is generated on ordinary pushes. Repository visibility, Store publication and versions are never changed by this workflow. Only the final draft job has repository write permission; it receives no signing secrets. CI uploads reports, not application downloads. Submission binaries are available as Actions artifacts to users with repository access; they are unsigned submission material, not installable public releases.

A repeated preparation for the same commit reuses an unchanged, asset-free release draft. GitHub does not expose private drafts to the read-only build token, so a retry can rebuild; each run/attempt has a unique artifact name. GitHub may remove previous job artifacts during a rerun, even with distinct names. Actions artifacts are temporary build evidence, so keep an external copy of the accepted attempt. The write-enabled draft job sees all drafts and makes the final version/commit check without changing an existing draft or published release assets. A failed build can be rerun. If an artifact has expired or the source/version mapping changed, investigate before preparing another version. An occupied version/tag, failed or stale checks, incomplete package evidence, unexpected profile or a checksum mismatch stops preparation.

**Release immutability is enabled in this repository.** It applies when a new release is published; a draft remains editable. Confirm Settings → General → Releases → Enable release immutability in a fork. Finish notes and intended assets before publishing. Do not move or recreate published version tags.

## Local bundle validation and WACK

Build an unsigned submission candidate without any certificate or trust installation:

```powershell
.\scripts\package-store.ps1 -OutputDirectory build/store/candidate
.\scripts\verify-store-package.ps1 -Directory build/store/candidate
```

The output directory must not already exist. A dirty local checkout is recorded in the report and cannot become a workflow release draft. Validation checks both architectures, identities, versions, language catalogs, runtime, XAML resources, absence of test harnesses/certificates, original attribution and hashes/source attribution of shipped DLL/EXE files. It is not Windows certification.

Run the [Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit) on a disposable Windows test installation. For an installation test, use a **separate copy**, signed with a temporary test certificate whose subject matches the Store publisher. Keep its private key outside the repository and trust it only in that test environment. Never replace the unsigned submission bundle with the signed test copy.

From an elevated terminal **inside that test VM**, with current Windows SDK/WACK installed:

```powershell
# TestCopy is a separately signed/trusted copy of the verified submission bundle.
$testCopy = 'C:\StoreTest\KanbanTasker-test.msixbundle'
$report = 'C:\StoreTest\wack-results.xml'
$wack = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
& $wack reset
& $wack test -appxpackagepath $testCopy -reportoutputpath $report
```

Review the XML/HTML results, investigate every failure and retain evidence with the source commit and versions. WACK can install/launch the package; do not run these commands against a personal profile. Do not regard missing WACK output as success. Microsoft also performs its own certification after submission.

## Store listing material

Use the reserved name **Kanban Tasker SoloSync**, category **Productivity**, and the actual supported x64/ARM64 architectures. Minimum supported Windows build is 19041; Windows 10 22H2 and Windows 11 are the acceptance targets. Confirm pricing, markets, availability, age-rating questionnaire, contact details and any required declarations in Partner Center; the workflow cannot set them.

**Short description (English):** A simple personal kanban board with local files, reminders and optional sync between your devices.

**Description (English):**

Organise your projects with boards, movable columns and task cards. Add descriptions, priorities, tags, dates and reminders. A calendar highlights due tasks. Optional board groups separate areas such as work and personal plans. Choose from five interface languages and system, light, dark or blue themes.

Your boards live in one local JSON file. Work offline, choose where the file is stored and optionally use Nextcloud or another file-sync service to take it between your own devices. Save and close the app, then wait for file transfer before switching devices. Simultaneous collaboration is not supported. No account is required.

This independent edition is based on Hunter Johnson's MIT-licensed Kanban Tasker. It does not import the original application's database. Required runtimes are included.

**Kurzbeschreibung (Deutsch):** Ein einfaches persönliches Kanban-Board mit lokaler Datei, Erinnerungen und optionalem Gerätewechsel per Dateisynchronisierung.

**Beschreibung (Deutsch):**

Organisiere Projekte mit Boards, verschiebbaren Spalten und Aufgabenkarten. Beschreibungen, Prioritäten, Tags, Termine und Erinnerungen halten die Details zusammen. Der Kalender hebt Tage mit fälligen Aufgaben hervor. Optionale Board-Gruppen trennen beispielsweise Arbeit und Privat. Fünf Oberflächensprachen sowie System-, helle, dunkle und blaue Designs stehen zur Auswahl.

Alle Boards liegen in einer frei wählbaren lokalen JSON-Datei. Du kannst offline arbeiten und die Datei optional mit Nextcloud oder einem anderen Dateisynchronisierungsdienst zwischen deinen Geräten übertragen. Vor dem Gerätewechsel speichern, die App schließen und die Übertragung abwarten. Gleichzeitige Zusammenarbeit wird nicht unterstützt. Ein Konto ist nicht erforderlich.

Diese unabhängige Ausgabe basiert auf Hunter Johnsons MIT-lizenziertem Kanban Tasker. Ein Import der Datenbank der ursprünglichen App ist nicht enthalten. Benötigte Laufzeiten werden mitgeliefert.

**Public links:**

- Privacy: https://github.com/Graphsto/Kanban-Tasker-Solosync/blob/main/PRIVACY.md
- Support: https://github.com/Graphsto/Kanban-Tasker-Solosync/issues
- Sensitive security reports: https://github.com/Graphsto/Kanban-Tasker-Solosync/security/advisories/new
- Product: https://apps.microsoft.com/detail/9P7HG1RBVBW6 (available after publication)

Use real, current screenshots with artificial data. [branding/screenshots](branding/screenshots) contains reproducible examples. Review them against the release candidate, refresh branding as needed and export full-window Store screenshots at Partner Center's accepted dimensions. Dialog-only README crops are supplementary documentation, not a substitute for full app screenshots. Add accurate captions and alt text. Do not imply that an external sync provider or simultaneous collaboration is included.

### Restricted capability explanation

`runFullTrust` is required because this is a WinUI 3 desktop application. It reads and writes the JSON file selected by the user, keeps local settings and recovery state, monitors local file changes and schedules Windows reminders. The Store edition does not install certificates, run a local installer, require an account or connect to an application backend.

### Notes for certification reviewers

No credentials, paid account, external server or Nextcloud installation are required.

1. Launch the app, choose **Create data file**, and save a JSON file in a writable local folder.
2. Create a board, add a task, edit its title/description/priority/tags and save. Drag cards and columns or use their context menus. Reopen the file to check persistence.
3. Give a task a future due date/time and reminder, then check the calendar. Allow Windows notifications and verify a reminder after closing the app.
4. In Settings, try the five languages, themes and optional board groups. Open the privacy link.
5. **Update in Microsoft Store** opens this product's Store page while retaining the editor draft. Until the product is live, the listing may be unavailable.
6. Cloud file transfer is optional, supplied by the user's separate sync client. This is a solo application.

## Release acceptance record

Keep an evidence record with the exact commit, product version, Store package version, OS builds and architecture. **Unchecked items remain release gates; compilation alone is not approval to submit.**

- [ ] CI, CodeQL and secret checks pass for the release's exact main commit; inspect findings, not only workflow completion.
- [ ] Verified x64/ARM64 Store bundle, runtime versions, attribution and SHA-256 retained.
- [ ] WACK passes, with its complete report retained and any exceptions resolved.
- [ ] Windows 10 22H2 and Windows 11: clean installation, upgrade to a higher Store package version, uninstall.
- [ ] Upgrade preserves JSON and preferences; uninstall leaves the user-owned JSON intact.
- [ ] From a local test installation, first Store launch opens the existing format-2 file; local settings/recovery are not migrated.
- [ ] Reminders fire with the installed app closed.
- [ ] Languages, themes, high contrast, keyboard access and relevant display scales.
- [ ] Real Store launch, missing/disabled Store, offline Store and browser fallback; unsaved draft retained.
- [ ] ARM64 execution on ARM64 Windows hardware or VM.
- [ ] Sequential file transfer with two devices and the actual sync client.
- [ ] Failed checks, occupied version/tag, incomplete artifact and repeated preparation handled safely.
- [ ] Public privacy/support/security links, listing, screenshots, age rating, pricing, availability and Microsoft component redistribution terms checked.
- [ ] Partner Center version history checked; Store certification and listing availability confirmed before GitHub publication.

Automated control tests simulate Store launcher success/failure and validate draft preservation. They do not exercise the real Store service, installation or Windows reminders. Windows 10/11 installation, WACK, ARM64 runtime and listing approval require separate test environments/Partner Center access and must remain open until evidence exists.

## Later direct GitHub distribution

A signed public EXE and GitHub updater are deferred. After production signing is approved, the proposed flow is: explicitly check the latest stable release, select the architecture, download and verify signature/origin/version, confirm installation, then close the app cleanly. Do not ship a private test trust certificate for public installations.

[SignPath Foundation](https://signpath.org/terms.html) can be evaluated for this derived open-source project after public availability; eligibility is not assured. GitHub Releases can host downloads without a separate web service, subject to its [asset limits](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases).

References: [Microsoft distribution paths](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path), [Store package versions](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements), [Store links](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app), [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).
