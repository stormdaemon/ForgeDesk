# Security policy

## Supported versions

Security fixes are released for the latest minor version of ForgeDesk.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Instead, use
[GitHub private vulnerability reporting](https://github.com/stormdaemon/ForgeDesk/security/advisories/new)
with a description of the issue, the affected version and steps to reproduce.

You can expect an acknowledgement within a few days and a fix or mitigation plan as soon as
the issue is confirmed.

## How ForgeDesk handles sensitive data

* Your GitHub token is stored in **Windows Credential Manager** (entry `ForgeDesk:github.com`),
  never in ForgeDesk's database, settings or logs.
* When ForgeDesk authenticates git operations with that token, it passes it to git through
  environment-based configuration, never on the command line.
* All ForgeDesk data stays on your machine (`%LOCALAPPDATA%\ForgeDesk`). ForgeDesk talks only to
  `api.github.com`/`github.com` (when you sign in) and to GitHub Releases to check for updates.
* ForgeDesk never executes project commands on its own: detected commands only run when you start them.
