# ForgeDesk architecture

ForgeDesk is a native Windows desktop application written in C# on .NET 10 with WPF.
This document explains how the code is organized, which rules every layer follows, and
where to add new features.

## Solution layout

```
ForgeDesk.slnx
├─ src/
│  ├─ ForgeDesk.Core/           net10.0 — domain models + services (no UI, no Windows APIs)
│  ├─ ForgeDesk.Presentation/   net10.0 — view models (MVVM, CommunityToolkit.Mvvm), UI abstractions
│  └─ ForgeDesk.App/            net10.0-windows — WPF views, Windows integration, composition root
├─ tests/
│  ├─ ForgeDesk.Core.Tests/          xUnit v3 — services against real git repos / SQLite
│  ├─ ForgeDesk.Presentation.Tests/  xUnit v3 — view models with fakes
│  └─ ForgeDesk.UITests/             FlaUI — end-to-end smoke tests of the packaged app (Windows only)
├─ build/                       packaging scripts and installer assets
└─ docs/
```

Dependencies only point downwards: `App → Presentation → Core`. Core and Presentation are
platform-neutral, which keeps ~all logic testable on any OS and on CI runners.

## Core (`ForgeDesk.Core`)

One folder per domain. Each domain exposes **interfaces + immutable records** (the contract)
and internal implementations, registered in its `<Domain>ServiceCollectionExtensions.cs`.
`DependencyInjection/CoreServiceCollectionExtensions.AddForgeDeskCore()` wires them all.

| Domain | Responsibility | Main contracts |
|---|---|---|
| `Common` | errors, paths, clock, ids | `ForgeException`, `ErrorKind`, `ErrorInfo`, `IAppPaths`, `PathUtil` |
| `Processes` | running external programs | `IProcessRunner`, `ProcessSpec`, `ShellCommand`, `ExecutableLocator` |
| `Storage` | SQLite database, migrations, backups, corruption recovery | `Database`, `Schema` |
| `Settings` | user preferences | `AppSettings`, `ISettingsService` |
| `Projects` | registry, dashboard snapshots, cloning | `IProjectRegistry`, `IProjectStatusService`, `IProjectCloneService` |
| `Detection` | understanding a project (languages, tools, commands, tests, workflows) | `IProjectDetector`, `IEcosystemDetector`, `ICustomCommandStore` |
| `Git` | every git operation, via the git CLI | `IGitService` |
| `GitHub` | GitHub REST API (Octokit) and sign-in | `IGitHubService`, `IGitHubAccountService` |
| `Runs` | executing commands with logs, progress, cancellation | `IRunService`, `IRunSession` |
| `WorkItems` | the per-project task board | `IWorkItemService` |
| `Activity` | local journal of what happened | `IActivityLog` |
| `Files` | directory listing, file reading, file index, content search, watchers | `IFileService`, `IFileIndex`, `IContentSearchService`, `IProjectWatcher` |
| `Analysis` | project health report | `IProjectAnalyzer` |
| `Releases` | preparing and publishing GitHub releases | `IReleaseService` |
| `Terminal` | discovering installed shells | `IShellDiscovery` |
| `Security` | secret storage abstraction | `ISecretStore` |

### Rules

* **Errors.** Expected failures throw `ForgeException(ErrorKind, message, hint, detail)`.
  The message is written for the user ("Your branch is behind 'origin/main'."), the hint says
  what to do, `detail` holds raw technical output (git stderr, HTTP body). Never surface raw
  exception text as the only explanation. Unexpected exceptions are caught at the view-model
  boundary and turned into `ErrorInfo`.
* **Async + cancellation.** Every I/O method is `async`, takes a `CancellationToken`, and uses
  `ConfigureAwait(false)`.
* **External processes** go through `IProcessRunner` (timeouts, tree-kill, UTF-8, no window).
  Git is invoked with `GIT_TERMINAL_PROMPT=0`, `LC_ALL=C`, `core.quotepath=false` and
  machine-readable formats (`--porcelain=v2 -z`, custom `--format` with NUL separators).
* **Paths.** Project paths are absolute and normalized (`PathUtil.Normalize`). Paths inside a
  project are relative with forward slashes (git style); convert with `PathUtil.ResolveUnder`,
  which also forbids escaping the project root.
* **Persistence.** One SQLite database (`%LOCALAPPDATA%\ForgeDesk\forgedesk.db`, WAL mode)
  accessed through `Database.UseAsync` with Dapper. Timestamps are stored as UTC ISO-8601.
  Schema changes are **appended** migrations in `Storage/Schema.cs`. At startup the database is
  integrity-checked; a corrupted file is quarantined and the newest healthy backup restored.
  Large payloads (run logs) live in files under `%LOCALAPPDATA%\ForgeDesk\runs`.
* **Secrets** (GitHub token) are stored in Windows Credential Manager through `ISecretStore`,
  never in the database, settings or logs.
* **Events** raised by services may fire on any thread; subscribers marshal to the UI thread.
  Use `SafeEvent.Raise` so one faulty subscriber cannot break a service.
* **Bounded work.** Scans cap the number of files, file reads cap their size, outputs cap their
  memory. Heavy folders (`node_modules`, `bin`, `obj`, `target`, `dist`, `.git`, `.venv`…) are
  never indexed.

## Presentation (`ForgeDesk.Presentation`)

MVVM with CommunityToolkit.Mvvm source generators (`[ObservableProperty]` partial properties,
`[RelayCommand]`). View models never reference WPF types; they talk to the UI through
abstractions implemented by the app:

* `IUiDispatcher` — marshal to the UI thread.
* `IDialogService` — confirmations, prompts, folder/file pickers, custom dialogs (`IDialogViewModel`).
* `INotificationService` — in-app notifications and Windows toasts.
* `IShellIntegration` — Explorer, browser, code editor, clipboard.
* `INavigationService` — pages (dashboard, project workspace, activity, settings, onboarding).
* `IBackgroundOperations` — long operations shown in the status bar.

`ViewModelBase.RunAsync` is the one way to run async work from a command: it sets `IsBusy`,
turns exceptions into `Error` (inline error panel) or an error notification, and treats
cancellation as silent.

### Project workspace

Opening a project creates a `ProjectWorkspaceViewModel` holding a `ProjectContext`: the shared,
observable state of that project (record, detected profile, live git status). All tabs read git
state from the context so the header, the Git tab and the file tree always agree. A file
watcher calls `ProjectContext.NotifyFilesChanged`, which debounces a status refresh.

Tabs are `IWorkspaceSectionViewModel`s (`WorkspaceSection` enum), created lazily by
`IWorkspaceSectionFactory` with the context injected (`AddWorkspaceSection<TViewModel>(section)`),
and kept alive while the project is open — terminals keep running when switching tabs.
A section can ask the workspace to show another tab with `ProjectContext.RequestNavigation`.

### Command palette

`Ctrl+K` opens a palette fed by every registered `IPaletteSource` (projects, actions, files,
branches, commands, tasks, settings). Sources must be fast and never throw.

## App (`ForgeDesk.App`)

* `Program.Main` → Velopack bootstrap → single-instance check (named mutex + pipe to forward
  arguments to the running instance) → `App`.
* `App` builds the DI container (`AddForgeDeskCore`, `AddForgeDeskPresentation`, Windows services),
  initializes the database, applies the theme, restores the GitHub session and shows `MainWindow`.
* Views are `UserControl`s under `Features/<Feature>/`, mapped to view models by implicit
  `DataTemplate`s in `Features/<Feature>/<Feature>Templates.xaml` (merged by `App.xaml`).
  Code-behind is limited to view-only behavior (focus, scrolling, drag-and-drop adorners).
* Windows services: `WindowsCredentialSecretStore` (Credential Manager), Job Object so child
  processes die with ForgeDesk, toast notifications, taskbar progress, jump list of recent
  projects, `Microsoft.Win32.OpenFolderDialog`.
* The integrated terminal hosts the Windows Terminal control (`Microsoft.Terminal.Wpf`) connected
  to a ConPTY pseudo console. Because it is an HWND, WPF content cannot overlap it: dialogs are
  separate windows and the palette is a `Popup`.

## Data locations

| What | Where |
|---|---|
| Database (projects, tasks, activity, run index, settings) | `%LOCALAPPDATA%\ForgeDesk\forgedesk.db` |
| Run logs | `%LOCALAPPDATA%\ForgeDesk\runs\` |
| Automatic backups (daily, 5 kept) | `%LOCALAPPDATA%\ForgeDesk\backups\` |
| Application logs (7 days) | `%LOCALAPPDATA%\ForgeDesk\logs\` |
| GitHub token | Windows Credential Manager, entry `ForgeDesk:github.com` |

`FORGEDESK_DATA_DIR` overrides the data folder (used by UI tests and portable setups).

## Adding a feature

1. Contract in Core (`I<Service>` + records) and implementation, registered in the domain's
   `ServiceCollectionExtensions`; tests in `ForgeDesk.Core.Tests`.
2. View model(s) in Presentation, registered in `<Feature>PresentationRegistration`; tests with fakes.
3. View in `ForgeDesk.App/Features/<Feature>/`, DataTemplate in the feature's templates dictionary.
4. Palette entries through an `IPaletteSource` when the feature has quick actions.
5. Follow [DESIGN.md](DESIGN.md) for layout, states and interactions.
