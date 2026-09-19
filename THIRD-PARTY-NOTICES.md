# Third-party notices

This edition is based on Kanban Tasker / KanbanBoardUWP by Hunter Johnson.
The original notice, `Copyright (c) 2019 hjohnson12`, and complete MIT license are
preserved in `LICENSE` (installed as `LICENSE.txt`). Attribution does not imply
endorsement by the original author. Third-party dependencies retain their own terms.

The application uses .NET, Windows App SDK / WinUI, the Windows SDK projection,
and System.Security.Cryptography.Pkcs. The installer also bundles the .NET Windows
Desktop runtime. Windows App SDK restores additional Microsoft components; these
must not all be described as MIT solely because this application is MIT licensed.

Run `scripts/export-notices.ps1` after restore to collect package-supplied license
texts, third-party notices, original NuGet specifications and a machine-readable
`dependencies.json`. The inventory includes build tools, reference packs and runtime
packs for both architectures. It deliberately covers more than the shipped binaries;
it is a build-input inventory, not a complete binary SBOM or a license clearance.

Before public distribution, compare the final app and installer payloads with this
inventory, resolve packages which only supply a license URL/expression, and verify
redistribution conditions for bundled native files. Preserve upstream notices
unchanged. The packaging script includes the collected notices in the app package;
the same app package is embedded in Setup.
