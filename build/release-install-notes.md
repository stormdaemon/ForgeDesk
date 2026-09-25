## Install

| File | What it is |
|---|---|
| **`ForgeDesk-win-Setup.exe`** | Recommended. Installs ForgeDesk for your user (no admin rights needed), adds Start menu and desktop shortcuts, and keeps ForgeDesk up to date automatically. |
| `ForgeDesk-win-Portable.zip` | Portable version: extract anywhere and run `ForgeDesk.exe`. |
| `SHA256SUMS.txt` | Checksums to verify your download. |

**Requirements:** Windows 10 (1809) or Windows 11, x64. [Git for Windows](https://git-scm.com/download/win) is required for Git features (it also provides Git Credential Manager for GitHub sign-in). No .NET installation is needed.

> **Windows SmartScreen:** ForgeDesk builds are not code-signed yet, so Windows may show "Windows protected your PC" the first time. Click **More info → Run anyway**. You can verify the file against `SHA256SUMS.txt`.

**First launch:** ForgeDesk walks you through adding your first project and (optionally) connecting GitHub. Your data stays on your machine in `%LOCALAPPDATA%\ForgeDesk`; your GitHub token is stored in Windows Credential Manager.

**Uninstall:** Settings → Apps → Installed apps → ForgeDesk → Uninstall. Your projects' files are never touched.
