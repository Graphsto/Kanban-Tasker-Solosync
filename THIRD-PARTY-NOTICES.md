# Third-party notices

This edition is based on Kanban Tasker / KanbanBoardUWP by Hunter Johnson.
The original notice, `Copyright (c) 2019 hjohnson12`, and complete MIT license are
preserved in `LICENSE` (installed as `LICENSE.txt`). Attribution does not imply
endorsement by the original author. Third-party dependencies retain their own terms.

The application uses .NET, Windows App SDK / WinUI, the Windows SDK projection,
System.Security.Cryptography.Pkcs and transitive Microsoft components. Local test
Setup also bundles the .NET Windows Desktop runtime. MIT covers the project's
source; it does not relicense these dependencies.

Run `scripts/export-notices.ps1` after restore to collect package-supplied license
texts, third-party notices, original NuGet specifications and a machine-readable
`dependencies.json`. The inventory includes build tools, reference packs and runtime
packs for both architectures. It deliberately covers more than the shipped binaries;
it is a build-input inventory, not a complete binary SBOM or a license clearance.

## Actual shipped binaries

Store packaging also supplies the published payload to `export-notices.ps1`.
`Notices/shipped-binaries.json` identifies every DLL/EXE by relative path and SHA-256,
matching it to its source NuGet packages. Generated project assemblies and the
generated .NET apphost are identified explicitly. Unknown binary sources or shipped
packages without license text fail packaging. Verification compares this inventory
against the actual bytes inside each MSIX.

The 2.4.1.8 Store payload includes these component families:

| Component | Accompanying terms/notices |
| --- | --- |
| .NET runtime/apphost, System.Security.Cryptography.Pkcs, System.Numerics.Tensors | Upstream MIT license and applicable third-party notices. |
| Windows App SDK Foundation, WinUI, DWrite, InteractiveExperiences, Search, Widgets and AI | Microsoft Windows App SDK license supplied by each package, plus additional notices where supplied. |
| Microsoft.Windows.AI.MachineLearning | Microsoft package license and ThirdPartyNotices.txt. The app does not invoke AI or download models. |
| WebView2 SDK loader | Package LICENSE.txt and NOTICE.txt. This is the SDK loader, not an included Evergreen browser runtime; the app does not embed a browser. |
| Windows SDK .NET projection | Microsoft Windows SDK terms; the referenced upstream license is preserved in [packaging/licenses](packaging/licenses/README.md). |

Windows App SDK and MachineLearning package terms address files placed alongside
the app by their NuGet packages, including self-contained deployment. Their
distribution requirements and restrictions still apply, including applicable
end-user terms and avoiding Microsoft endorsement claims. The SDK license also
contains distribution requirements. Do not describe all components as MIT. The
full accompanying terms control; this table is an inventory aid.

The Store packager preserves the package-supplied licenses, notices and NuGet
specifications unchanged, and resolves the Windows SDK license URL to a pinned
local copy. Review this evidence whenever dependencies change, including native
components and any new required notice locations. Binary matching verifies origin
and notice coverage, not every legal condition, non-PE asset or the publisher's
compliance with its distribution agreement. Review Partner Center's license terms
together with these Microsoft redistribution requirements before submission.
Retain the original project license and upstream notices in packages and forks.
