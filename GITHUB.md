# GitHub Projects (optional)

GitHub boards appear alongside local boards, with a GitHub icon. Local boards
continue to use your chosen JSON file; GitHub links and cached cards use a
separate directory in the app's local profile. GitHub also works without an open
JSON file. Changing that file does not change GitHub links.

This feature is being tested on `codex/github-projects`. Builds use the registered
[Kanban Tasker Solosync GitHub App](https://github.com/apps/kanban-tasker-solosync),
whose public Client ID is configured for both distribution channels. Real
two-client acceptance still needs to be completed before production integration.
Builds without a Client ID explain the missing setup when you select
**Link GitHub project**.

## Link a project

1. Select **Boards → Link GitHub project**, or the corresponding action under
   **Settings → General → GitHub**.
2. Open the GitHub sign-in link, enter the displayed device code and authorize
   the registered application. Your GitHub password stays in GitHub's browser UI.
3. Choose an organization where the GitHub App is installed, then a project.
4. Select its supported views, or link the entire project as a group. With several
   views, the dialog offers to enable groups. A project group automatically adds
   new supported views on a later refresh.

Only Kanban views whose column field is **Status** are supported. Tables,
roadmaps and boards grouped by another field are listed as unsupported.
Saved view filters are applied by GitHub. A filtered-out card still belongs to
the project. Cards without a status appear under **No status**. Saved automatic
sorting disables manual reordering in that view.

Disabling groups only hides the group selector. Unlinking a board removes its
local link, without deleting anything on GitHub. Removing a GitHub group keeps
its existing links under **Ungrouped** and stops adding future views. Explicitly
unlinked views stay excluded from automatic discovery until you link them again.
Links are per device and per GitHub account; they do not travel with your JSON.

## Changes and connectivity

| Item | Allowed in the app |
| --- | --- |
| Draft | Create, edit title/description, change status/order, remove from the project after confirmation |
| Existing issue | Read contents, open its GitHub link, change project status/order |
| Pull request or inaccessible content | Hidden |
| Status option | Create or rename, preserving existing option IDs, colors and descriptions |

Issue contents and open/closed state, column deletion and other project settings
are locked. Tags, dates, reminders and other GitHub fields are outside this
version. The app never creates repository issues.

The active project normally refreshes every five seconds; other linked projects
every 60 seconds. Window activation, the refresh action and completed changes
also trigger reads. GitHub rate limits can lengthen these intervals or pause
requests. This is frequent polling, with no atomic lock against another client
or someone changing the project on GitHub.

When GitHub is unreachable, cached boards remain readable and all writes are
disabled. There is no offline queue. Input already entered in an editor remains
in memory and is locked until fresh data and permissions can be read. Closing
the app discards unsaved input. An externally removed card cannot be restored by
saving an old editor.

Before writing, the app reads the project again. Changes to different fields are
preserved; competing edits to the same field require choosing your version or
GitHub's version. Writes are serialized within each project and checked afterward.
If a response is lost or only part of a change succeeds, editing is locked and a
local journal retains the operation. Use **Review GitHub write**, inspect the
project, then explicitly unlock it. The app does not retry that operation. For a
lost draft-creation response, check for the new draft before creating another.

## Register the GitHub App

This is an API application registration, separate from GitHub Desktop or the
account settings of an editor. A maintainer or organization owner performs this
setup once; each user subsequently installs/authorizes the application as needed.

1. Open [New GitHub App](https://github.com/settings/apps/new), under GitHub
   **Settings → Developer settings → GitHub Apps**. Choose a unique name and use
   the repository URL as the homepage if there is no separate product website.
2. Enable **Device Flow** and keep **Expire user authorization tokens** enabled.
   For an existing registration, check **Optional Features → User-to-server token
   expiration**. The app accepts only expiring access/refresh tokens.
3. Leave **Request user authorization (OAuth) during installation** off. Sign-in
   happens through the device code. A callback URL is not used by this flow.
   Disable **Webhook → Active**; this client has no webhook server.
4. Set **Organization permissions → Projects: Read & write** and **Repository
   permissions → Issues: Read-only**. GitHub also requires repository Metadata
   read access. Leave other optional permissions at **No access**.
5. Choose the intended installation audience. Install the App in the test
   organization and grant access to repositories whose issue contents should be
   visible. An organization owner may need to approve the installation. The user
   also needs access to the project and write rights for editing. With SAML SSO,
   start an active organization SAML session before reauthorization.
6. Copy the **Client ID** shown on the registration's settings page. This is public
   configuration and differs from the numeric App ID. The default registration
   uses `Iv23liMgxY35gcqrfMJy` in `packaging/Distribution.props`. For a different
   registration, override `KanbanGitHubClientId` as an MSBuild property:

   ```powershell
   dotnet build src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 -p:KanbanGitHubClientId=YOUR_PUBLIC_CLIENT_ID
   ```

Do not supply a Client Secret, private key or access token to the build. Device
flow and its token refresh require neither a private key nor a Client Secret.
GitHub's private-key setup notice concerns authentication as the application
itself, such as signing a JWT for an installation access token; this desktop
client uses user access tokens instead. No private key needs to be generated for
this flow. If one has already been downloaded as a `.pem` file, keep it in secure
storage outside the repository and app. Never upload it to a chat or include it
in a build. See GitHub's [private-key documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps).

Credentials are protected
with Windows DPAPI for the current user; cached contents are ordinary local JSON.
Signing out removes local credentials and locks cached boards. Revoke the App's
authorization on GitHub as well if you want to remove its server-side access.
See [Privacy](PRIVACY.md) for retained files and data sent to GitHub.

GitHub's authoritative setup references:
[registration](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app),
[device flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app),
[refresh tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens).

## Acceptance before integration

CI uses simulated API responses and does not need tokens. It covers cache safety,
pagination, filters, hidden PRs, allowed mutations, issue restrictions, conflicts,
offline operation, uncertain creation and the mixed WinUI selectors/editor.

Real acceptance still requires a disposable organization project and two clients
authorized through the registered App. Check login/rotation and organization
rights, filtered/sorted views, new-view discovery, draft creation/edit/removal,
issue moves, status-option preservation, simultaneous edits to different/same
fields, external removals, offline/reconnect and rate limits. Test with artificial
data; record the results before merging the feature PR. Cross-builds do not
replace installed Windows 10/11 and ARM64 acceptance.
