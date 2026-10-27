# Footage Vault

A Windows 11 app that backs up folders of raw news footage to your YouTube channel, with consistent titles such as
`Collector Visit 1 (26.10.26)` and `Collector Visit - Phone 1 - 1 (26.10.26)`.

## Install

1. Open the **[latest release](../../releases/latest)** and download **FootageVault-Setup.exe**.
2. Run it. If Windows says *"Windows protected your PC"*, select **More info → Run anyway** (the installer isn't
   code-signed; a paid certificate removes this).
3. Footage Vault is now in the **Start menu** and in **Windows search** (type "Footage"). It installs for your user
   only, needs no admin rights, and can be removed from **Settings → Apps**.

Each push to `main` builds a new installer automatically; the newest is always the latest release.

## First run: connect to YouTube

1. On **Home**, select **Choose file** and pick the `client_secret.json` you downloaded from Google Cloud
   (OAuth client of type *Desktop app*). It is stored in `%LOCALAPPDATA%\FootageVault` and never uploaded anywhere.
2. Select **Sign in with Google**. Your browser opens; sign in with the account that owns the channel.
   "Google hasn't verified this app" is expected for an internal tool: **Advanced → Go to …**.
3. While the Google Cloud consent screen is in **Testing**, Google asks you to sign in again every 7 days.

## Daily use

1. **Home → Add folders** (pick several with Ctrl+click) or drag folders from File Explorer onto the drop area.
   Each chosen folder is a story; sub-folders become `Story - Sub-folder` stories.
2. **Preview** shows every folder, sub-folder and clip with its YouTube title.
   - Rename a folder or sub-folder by typing in its name box. Titles update straight away.
   - Change a date by typing over the grey `dd.mm.yy` next to a folder. Every clip in that folder changes.
   - Drag clips to change their order. Untick clips you don't want.
   - Clips already uploaded (even renamed copies) are unticked automatically.
3. **Upload** (one folder) or **Upload all**. With *Rename folders and files on disk* on (Settings), renamed folders
   and every uploaded video file are renamed to match their titles. If Windows refuses a rename (a file is open or
   still copying), nothing is changed and the app says why.
4. **Upload queue** shows progress, speed and time left. You can close the app; uploads continue from where they
   stopped next time. At the daily limit uploads pause and resume by themselves after the reset (12:30 or 1:30 PM IST).
5. Videos are **Private** until Google approves the API audit (then turn on *Settings → Google API audit approved*).

Records: `Documents\FootageVault\upload_log.csv` (opens in Excel) and the **History** page.

## How it's built

| Part | What it is |
|---|---|
| `src/FootageVault.Core` | All logic, plain .NET 8 with no extra packages: scanning, nested stories, titles and numbering, on-disk renames with undo, fingerprints for duplicates, quota, Google sign-in (PKCE), resumable YouTube upload with a stall watchdog, the upload queue. |
| `src/FootageVault.App` | WinUI 3 interface (Windows App SDK, Mica, NavigationView), MVVM with CommunityToolkit.Mvvm. Windows-only services: Credential Manager for the sign-in token, Windows property system for recording dates, the multi-folder picker. |
| `tests/FootageVault.Core.Tests` | 46 tests, including a fake YouTube server that stalls mid-upload. |
| `installer/` | Inno Setup script: per-user install, Start menu entry, uninstaller. |
| `.github/workflows/build.yml` | On every push: tests, build, installer, a smoke test that installs the app on a Windows machine and opens every page with sample folders, then publishes the release. |

## Where things are stored

- Settings, queue (`state.json`), technical logs: `%LOCALAPPDATA%\FootageVault`
- Upload log: `Documents\FootageVault\upload_log.csv` (changeable in Settings)
- Google sign-in token: Windows Credential Manager (`FootageVault`)

## Testing status

**Tested on a real PC (27 Oct 2026, version 1.0.11):** the app installs and runs, and nested folders work
(sub-folders become `Story - Sub-folder` stories with the right titles).

**Checked automatically on every build:** 46 core tests, plus a clean install on a Windows machine that opens every
page with sample folders and checks for crashes, the Start menu entry and the Settings > Apps entry.

**Not yet confirmed on a real PC:** recording dates read from the cameras' own files, drag-and-drop from File
Explorer, renaming on disk with a real footage folder, a full day's volume against the daily limit.
