# RDCM

RDCM is a Windows WPF manager for saved Remote Desktop (RDP) connection profiles. Connect directly from RDCM and view each remote desktop in its own in-app workspace tab, powered by the Windows Remote Desktop ActiveX control. RDCM does not launch the separate `mstsc.exe` client.

## Run

Open `RDCM.csproj` in Visual Studio, or run:

```powershell
dotnet run --project .\RDCM.csproj
```

Requires Windows, the .NET 10 SDK, and the registered Windows Remote Desktop ActiveX component (`mstscax.dll`).

## Profiles and credentials

Profiles are stored as JSON in `%LOCALAPPDATA%\RDCM\connections.json`. They include the host, port, username, group, and notes. Passwords are not stored; the embedded Remote Desktop control prompts for credentials as needed.

Each workspace tab owns a separate embedded RDP control and session. A session keeps the endpoint snapshot used when it was started even if its saved profile is edited or removed. Editing an active profile offers the choice to keep the existing endpoint or explicitly reconnect with the new profile. Removing a profile can preserve active sessions or disconnect them with explicit confirmation. Exiting with active sessions also requires confirmation.

The profile editor has distinct **Save** and **Save & connect** actions; Save never starts a connection. The group field suggests existing groups. Closing the editor with unsaved changes asks before discarding them. **Duplicate** (context menu or command palette) opens the editor prefilled with a copy under a distinct name, such as "Web (copy)". Profiles are grouped in the navigation, and search covers name, address (including a non-default port), username, group, and notes; several words narrow the results, so `web prod` finds web servers noted as production. The list virtualizes its rows, so search stays responsive with thousands of profiles. Profile import/export is JSON; import files larger than 10 MB are refused. The last valid profile file is backed up before replacement. If the primary JSON is malformed, RDCM attempts recovery from that backup, preserves the corrupt source, and tells you at startup. Load and save problems are reported in themed dialogs with optional technical details; diagnostics are written to the debug trace output.

Only one RDCM window manages your connections at a time: starting RDCM again switches to the open window, so two windows can't overwrite each other's changes.

Use the **Resolution** and **DPI scale** dropdowns in the toolbar above the remote desktop in each tab. Resolution can fit the tab or be set from 1024 × 768 through 2560 × 1440; DPI scaling ranges from 100% to 250%. DPI scaling affects the remote session, not the RDCM window. Display preferences are per session and reset to Fit to tab / 100% on a newly opened tab. Fit-to-tab resolution follows the tab size; fixed resolutions and DPI scaling use the RDP dynamic display update protocol. The remote host must support dynamic display updates for live changes. Rejected updates revert the controls to the last confirmed settings and report the failure in the status area.

The window is organised as a command bar (profile commands, import/export, **Go to…**, keyboard shortcuts, appearance), the connections sidebar, the session workspace, and a status bar. Each connection row and session tab shows a session indicator: green when connected, amber while connecting, red when the connection failed, and hollow when disconnected. The session toolbar collapses its labels to icons when the workspace is narrow.

The native Remote Desktop surface is shown only once a session is connected. While connecting, disconnecting, or after a session ends, the tab shows a status panel instead (WPF cannot draw over the native window). **Cancel** or **Disconnect** is always safe: cancelling an attempt is reported as "Connection cancelled", not as an error. Genuine failures are classified from the control's documented disconnect codes (for example "Couldn't find the remote computer", "Sign-in failed", or "Security check failed"), show the reason reported by Remote Desktop with a suggested next step, and offer **Try again**, **Close tab**, and **Copy details**. Copy details puts the error codes, RDCM and Windows versions, and the message on the clipboard for a support request, with connection, computer, and user names replaced by placeholders and a non-default port number left out. When a session in a background tab fails or is ended by the remote computer, the status bar says so. The connection lifecycle is implemented in `RdpConnectionController`, driven by the control's native events with a polling watchdog as a fallback.

When Remote Desktop needs credentials it shows its own Windows Security sign-in window; Windows disables RDCM's window until that sign-in window closes, and the tab says it is waiting for you. RDCM never releases a session while that window is open (doing so blocks inside the Remote Desktop control): if a tab or RDCM itself is closed during sign-in — for example by assistive technology or when Windows signs out — RDCM cancels the sign-in and finishes closing once the sign-in window has gone. Remote Desktop asks for credentials again on every new connection, including a reconnect from the same tab; RDCM never stores passwords.

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

These tests do not contact an RDP host. They cover profile validation, immutable session identity, state transitions, the connection lifecycle (using a scripted fake of the Remote Desktop control: cancellation, retries, reconnects, stale events, failures, shutdown), settings normalization and migration, recent-connection bookkeeping, window placement validation, legacy profile loading, corrupt-file backup recovery, multi-word search and ranking, the command palette filter, shortcut routing rules, failure classification, diagnostic redaction, duplicate naming, the single-instance guard, the sign-in-dialog sequences observed against a real server (connect, cancel during sign-in, disconnect and reconnect), key routing from the session area, and the absence of password or credential fields in saved data.
