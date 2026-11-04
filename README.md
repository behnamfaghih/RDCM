# RDCM

RDCM is a Windows WPF manager for saved Remote Desktop (RDP) connection profiles. Connect directly from RDCM and view each remote desktop in its own in-app workspace tab, powered by the Windows Remote Desktop ActiveX control. RDCM does not launch the separate `mstsc.exe` client.

## Install

Download `RDCM-<version>-win-x64.msi` and open it. RDCM installs for your Windows account only, without administrator rights, into `%LOCALAPPDATA%\Programs\RDCM`, and adds **RDCM - Remote Connection Manager** to the Start menu (no desktop shortcut) and to **Settings > Apps > Installed apps**. Nothing else on the computer changes: no services, firewall rules, saved credentials, or policies.

Requirements: 64-bit Windows 10 or 11 with the built-in Remote Desktop client (`mstscax.dll`), and the **.NET 10 Desktop Runtime (x64)** from <https://dotnet.microsoft.com/download/dotnet/10.0>. The installer checks for the runtime first and stops with a message if it's missing.

- **Upgrade**: open the newer MSI. If RDCM is running, the installer asks you to close it; **Ignore** asks RDCM to close itself (it asks before disconnecting open sessions and is never forcibly terminated). Installing an older version over a newer one is refused with an explanation. If an upgrade fails or is cancelled, the previous version stays installed and working. Rarely, a failure at the very end leaves an extra entry for the version that failed in **Installed apps**, because Windows Installer can't remove a per-user registration without administrator rights; uninstall that entry, and the working version stays in place.
- **Repair**: in Control Panel > **Programs and Features**, right-click RDCM and choose **Repair** to restore missing or damaged program files.
- **Uninstall**: **Settings > Apps > Installed apps > RDCM - Remote Connection Manager > Uninstall** removes the program files, the shortcut, and the installer's own registry key (`HKCU\Software\RDCM\Installer`).

Your connection profiles and preferences (`%LOCALAPPDATA%\RDCM`: `connections.json`, its `.bak` backup, and `settings.json`) are never created, changed, or deleted by installing, upgrading, repairing, or uninstalling, so a reinstall picks them up again. Delete that folder yourself to remove them.

A build that isn't code-signed shows a Windows SmartScreen warning when opened. Only choose **More info > Run anyway** for an MSI you got from a source you trust.

## Build and run from source

Open `RDCM.csproj` in Visual Studio, or run:

```powershell
dotnet run --project .\RDCM.csproj
```

Requires Windows, the .NET 10 SDK, and the registered Windows Remote Desktop ActiveX component (`mstscax.dll`). To build the installer (WiX Toolset v7 is restored from NuGet; see [Releasing](#releasing)):

```powershell
dotnet build .\installer\RDCM.Installer.wixproj -c Release
```

The MSI is written to `installer\bin\x64\Release\RDCM-<version>-win-x64.msi`.

## Profiles and credentials

Profiles are stored as JSON in `%LOCALAPPDATA%\RDCM\connections.json`. They include the host, port, username, group, and notes. Passwords are not stored; the embedded Remote Desktop control prompts for credentials as needed.

Each workspace tab owns a separate embedded RDP control and session. A session keeps the endpoint snapshot used when it was started even if its saved profile is edited or removed. Editing an active profile offers the choice to keep the existing endpoint or explicitly reconnect with the new profile. Removing a profile can preserve active sessions or disconnect them with explicit confirmation. Exiting with active sessions also requires confirmation.

The profile editor has distinct **Save** and **Save & connect** actions; Save never starts a connection. The group field suggests existing groups. Closing the editor with unsaved changes asks before discarding them. **Duplicate** (context menu or command palette) opens the editor prefilled with a copy under a distinct name, such as "Web (copy)". Profiles are grouped in the navigation, and search covers name, address (including a non-default port), username, group, and notes; several words narrow the results, so `web prod` finds web servers noted as production. The list virtualizes its rows, so search stays responsive with thousands of profiles. Profile import/export is JSON; import files larger than 10 MB are refused. The last valid profile file is backed up before replacement. Saves write a temporary file and then replace the original; if another program (for example an antivirus scanner or the Windows search indexer) briefly holds the file open, RDCM retries for a fraction of a second, and if the file stays locked the save is reported as failed and the previous file is kept. If the primary JSON is malformed, RDCM attempts recovery from that backup, preserves the corrupt source, and tells you at startup. Load and save problems are reported in themed dialogs with optional technical details; diagnostics are written to the debug trace output.

Only one RDCM window manages your connections at a time: starting RDCM again switches to the open window, so two windows can't overwrite each other's changes. If the open RDCM runs as administrator and is minimized, Windows doesn't let a normally started RDCM bring it forward; RDCM then tells you that it is already running so you can switch to it from the taskbar.

Use the **Resolution** and **DPI scale** dropdowns in the toolbar above the remote desktop in each tab. Resolution can fit the tab or be set from 1024 × 768 through 2560 × 1440; DPI scaling ranges from 100% to 250%. DPI scaling affects the remote session, not the RDCM window. Display preferences are per session and reset to Fit to tab / 100% on a newly opened tab. Fit-to-tab resolution follows the tab size; fixed resolutions and DPI scaling use the RDP dynamic display update protocol. The remote host must support dynamic display updates for live changes. Rejected updates revert the controls to the last confirmed settings and report the failure in the status area.

The window is organised as a command bar (profile commands, import/export, **Go to…**, keyboard shortcuts, appearance), the connections sidebar, the session workspace, and a status bar. Each connection row and session tab shows a session indicator: green when connected, amber while connecting, red when the connection failed, and hollow when disconnected. The session toolbar collapses its labels to icons when the workspace is narrow.

The native Remote Desktop surface is shown only once a session is connected. While connecting, disconnecting, or after a session ends, the tab shows a status panel instead (WPF cannot draw over the native window). **Cancel** or **Disconnect** is always safe: cancelling an attempt, including clicking **Cancel** in the Windows Security sign-in window, is reported as "Connection cancelled", not as an error. Genuine failures are classified from the control's documented disconnect codes (for example "Couldn't find the remote computer", "Sign-in failed", or "Security check failed"), show the reason reported by Remote Desktop with a suggested next step, and offer **Try again**, **Close tab**, and **Copy details**. Copy details puts the error codes, RDCM and Windows versions, and the message on the clipboard for a support request, with connection, computer, and user names replaced by placeholders and a non-default port number left out. When a session in a background tab fails or is ended by the remote computer, the status bar says so. The connection lifecycle is implemented in `RdpConnectionController`, driven by the control's native events with a polling watchdog as a fallback.

When Remote Desktop needs credentials it shows its own Windows Security sign-in window; Windows disables RDCM's window until that sign-in window closes, and the tab says it is waiting for you. RDCM never releases a session while that window is open (doing so blocks inside the Remote Desktop control): if a tab or RDCM itself is closed during sign-in — for example by assistive technology or when Windows signs out — RDCM cancels the sign-in and finishes closing once the sign-in window has gone. Remote Desktop asks for credentials again on every new connection, including a reconnect from the same tab; RDCM never stores passwords.

RDCM asks Remote Desktop to warn when it can't verify the remote computer's identity. A Windows policy ("Configure server authentication for client") can override that request; when the setting in effect is weaker than requested, for example a policy that always connects, the session toolbar shows **Server identity not verified** with an explanation, the change is announced once, and **Copy details** includes a "Server authentication" line.

If the network drops, Remote Desktop restores the session by itself (by default up to five attempts). The tab keeps the remote desktop in place and its status says "Connection interrupted · reconnecting (attempt n of m)"; no new sign-in is needed and no Recent entry is added. If the attempts run out, the tab reports **Connection lost** with the number of attempts. While Remote Desktop is reconnecting it ignores requests to disconnect, so **Disconnect** (or **Reconnect**) during that time replaces the tab's Remote Desktop control: the reconnecting control is released at once (as closing the tab does) and a fresh, disconnected one takes its place in the same tab, usually within a quarter of a second. Nothing from the released control can change the session afterwards, the tab and its display settings stay as they were, keyboard focus moves back to RDCM, and **Reconnect** connects again with the fresh control (Remote Desktop asks for credentials again). The control is never released while its Windows Security window is open; in that case RDCM waits for the window to close first. If no fresh control can be created, the tab says so and offers **Try again** and **Close tab**.

Internally, each tab's `EmbeddedRdpControl` is a container that owns the logical session (`RdpConnectionController`) and holds one native control "generation" at a time in an `RdpControlSlot`; native events carry their generation and are delivered only while it is current.

The connection list can show **All** connections (grouped), **Favourites**, or **Recent** connections (most recent first). Mark a favourite with the star on a row, the context menu, or `Ctrl+D`. A connection is added to Recent only when it actually connects; up to 10 are kept, and the most recent also appear on the empty workspace for quick access. The density button beside the filters switches to a compact single-line list.

The **Appearance** selector supports System, Light, and Dark modes, including a matching title bar; Windows High Contrast system colors take precedence. The UI uses the built-in Segoe Fluent Icons font (Segoe MDL2 Assets on Windows 10), so no extra icon packages are needed. Shared styles and colour tokens live in `Themes/`. The connections sidebar can be resized with its splitter or collapsed. Preferences are stored in `%LOCALAPPDATA%\RDCM\settings.json`: sidebar width and visibility, window size, position and maximized state (restored only onto a currently connected monitor), collapsed groups, list density, and favourite and recent connections. Favourites and recents store only profile ids and timestamps, never credentials. An unreadable settings file is kept as `settings.json.invalid` and defaults are used. RDCM is currently system DPI aware: it renders sharply at the primary display's scaling, and Windows scales it on monitors with a different scaling (for example a 100% monitor next to a 175% laptop screen), so text and the remote desktop look softer there. The WPF controls remain outside the native RDP surface because of WinFormsHost/ActiveX HWND airspace.

The **command palette** (`Ctrl+Shift+P`, or **Go to…** in the command bar) is the fastest way to reach anything: type part of a connection, open session, or command and press `Enter`. With nothing typed it lists open sessions, recent and favourite connections, and commands; start with `>` to list only commands. It is a separate window, so it also appears above a connected remote desktop.

Keyboard shortcuts (press `F1` for the full list, generated from the same command registry that handles the keys):

- `Ctrl+Shift+P`: Command palette
- `Ctrl+N`: New profile
- `Ctrl+F`: Focus profile search (shows the sidebar if hidden); `Down` moves from search into the list
- `Escape`: Clear search
- `Enter` (in the list) or `Ctrl+Enter`: Connect selected profile
- `Ctrl+Enter` (in the profile editor): Save & connect
- `Ctrl+B`: Show or hide the connections sidebar
- `Ctrl+W` or middle-click a tab: Close selected session tab (with confirmation when active)
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: Next / previous session tab
- `Ctrl+1` … `Ctrl+9`: Go to session tab 1–9
- `F2`: Edit selected profile
- `Ctrl+D`: Add or remove the selected profile from favourites
- `Delete`: Remove selected profile (with confirmation)
- `F1`: Keyboard shortcuts

RDCM's shortcuts never act on keys typed into a remote desktop; those keys belong to the remote computer. (Live testing showed that the Remote Desktop control handles a connected session's keyboard input on its own thread, so those keys never reach RDCM; RDCM also ignores any key that WPF reports from the session area.) Windows key combinations stay on this computer unless the session is full screen, which is the control's default.

The **Release keyboard** button (keyboard icon) in a session toolbar returns keyboard focus from the remote desktop to RDCM. Selecting a tab does not automatically move focus into the remote session; use **Focus remote desktop** from its context menu when deliberate keyboard interaction with the remote desktop is needed.

## Verification

Run the built-in, dependency-free model and persistence checks with:

```powershell
dotnet run --project .\RDCM.Tests\RDCM.Tests.csproj
```

These tests do not contact an RDP host. They cover profile validation, immutable session identity, state transitions, the connection lifecycle (using a scripted fake of the Remote Desktop control: cancellation, retries, reconnects, stale events, failures, shutdown), automatic reconnection (progress, recovery, exhaustion, the safety net, and the event sink's answer), replacing the control on Disconnect or Reconnect during auto-reconnect (release order, exactly one fresh control, stale events from released controls, sign-in windows, close and exit during the replacement, creation and release failures, and 100 replace-and-reconnect cycles), server-authentication evaluation and reporting, retried saves while another program holds a file, settings normalization and migration, recent-connection bookkeeping, window placement validation, legacy profile loading, corrupt-file backup recovery, multi-word search and ranking, the command palette filter, shortcut routing rules, failure classification, diagnostic redaction, duplicate naming, the single-instance guard, the sign-in-dialog sequences observed against a real server (connect, cancel in the Windows Security window, cancel during sign-in, disconnect and reconnect), key routing from the session area, keyboard focus leaving a session as it is hidden, the real Remote Desktop control's window being shown when a session appears (the control is created while hidden), and the absence of password or credential fields in saved data. The runner reports every failing check (and any unhandled exception with the check that was running) and exits with a non-zero code on failure.

## Releasing

1. **Version.** Set `<Version>` in `Directory.Build.props`, the only place it is defined; the executable, **Copy details**, the MSI, and Add/Remove Programs all use it. The product name, publisher, and copyright are there too. Every release needs a higher `major.minor.patch` version. Never change only a fourth part: Windows Installer ignores it, so `1.0.0.1` and `1.0.0` count as the same version and either one silently replaces the other (same-version rebuilds are allowed), with no downgrade protection in either direction.
2. **Build** the MSI (see above). The build always republishes RDCM from clean for win-x64 (framework-dependent), fails if the publish output gains or loses a file compared with the reviewed list (`RdcmShippedFile` in `installer\RDCM.Installer.wixproj`), and runs Windows Installer validation. Two builds of the same commit contain identical program files; the MSI files differ only in their generated product and package codes and timestamps.
3. **Sign** (when a code-signing certificate is available). No certificate, key, or password belongs in this repository.

   ```powershell
   dotnet build .\installer\RDCM.Installer.wixproj -c Release -p:SigningCertificateThumbprint=<SHA-1 thumbprint> -p:TimestampUrl=<RFC 3161 timestamp URL>
   ```

   `RDCM.exe` and `RDCM.dll` are signed before they are packaged, then the MSI is signed, each with SHA-256 and a timestamp (so signatures stay valid after the certificate expires), and each signature is checked with `signtool verify /pa`. `signtool.exe` comes from the Windows SDK; pass `-p:SignToolPath=<path>` if it isn't on `PATH`. Publicly trusted code-signing keys must be kept on a hardware token or in a cloud signing service (for example Azure Artifact Signing); the certificate then appears in the current user's certificate store through its provider. New certificates, including EV, start without SmartScreen reputation; it builds up as signed releases are downloaded.
4. **Test the MSI** before publishing it: a clean install, an upgrade from the previous release with profiles present, uninstall (profiles kept) and reinstall, an older version being refused, an upgrade while RDCM is running, and a connection to a real server from the installed build.
5. **Publish** the MSI with its SHA-256 hash.

Rules for `installer\Package.wxs`:

- Never change the `UpgradeCode`; it identifies RDCM across versions.
- Upgrades install the new version first and remove the previous one afterwards (`afterInstallFinalize`). A per-user install has no administrator rights, and Windows Installer can't roll back product registration in that mode, so removing the previous version first would leave it orphaned (files without an Add/Remove Programs entry) whenever an upgrade failed.
- Because both versions are briefly installed together, every file has its own component. Never change a component's GUID while its file keeps its name and folder; give a new file a new component with a new GUID; when a file is dropped, delete its component, and the file is removed with the previous version.
- During an upgrade, files of an equal version are replaced too (`REINSTALLMODE` `emus`), so a rebuilt package of the same version installs its own binaries.
- Keep user data out of the package: nothing in `%LOCALAPPDATA%\RDCM` may be authored, so installs, upgrades, and uninstalls never touch it.

The installer is built with WiX Toolset v7: `WixToolset.Sdk` (its version is fixed by the `Sdk="WixToolset.Sdk/7.0.1"` attribute in the project file; NuGet lock files don't cover MSBuild SDKs, so its hash isn't verified) and `WixToolset.Util.wixext` and `WixToolset.Netfx.wixext` (versions and hashes pinned in `installer\packages.lock.json`). Their binaries are provided under the Open Source Maintenance Fee EULA, accepted in the project file (`AcceptEula`). That fee applies to revenue-generating use by organisations with an annual gross revenue of US$10,000 or more; check whether it applies before distributing commercially. The MSI includes WiX's own custom-action binaries (closing a running RDCM and checking for the .NET runtime), which the licence allows to be redistributed with its notices. RDCM itself has no NuGet runtime dependencies.
