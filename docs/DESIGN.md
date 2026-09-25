# ForgeDesk design language

ForgeDesk should feel like a first-class Windows 11 tool built by a product team: calm,
dense, precise, fast. It builds on Fluent (WPF-UI) and adds its own identity — **Forge**:
dark steel surfaces lit by a warm ember accent.

## Identity

| Token | Value | Use |
|---|---|---|
| Ember (accent) | `#F2762E` · light `#FF9A57` · dark `#C9551A` | primary buttons, selection, focus, active tab indicator, running state |
| Steel | Fluent neutral surfaces (Mica backdrop) | backgrounds, cards |
| Success | `#3FB950` | passing CI, succeeded runs, clean trees |
| Warning | `#D29922` | behind remote, stalled runs, missing files |
| Danger | `#F85149` | failures, conflicts, destructive actions |
| Info | `#58A6FF` | links, informational badges |
| Added / Removed (diff) | `#2EA04326` / `#F8514926` backgrounds, `#3FB950` / `#F85149` gutters | diffs |

Brand resources live in `src/ForgeDesk.App/Themes/Brand.xaml` (`ForgeAccentBrush`,
`ForgeSuccessBrush`, `ForgeWarningBrush`, `ForgeDangerBrush`, `ForgeInfoBrush`,
`ForgeDiffAddedBackgroundBrush`, …). Always reference colors through `DynamicResource` —
light and dark themes must both work. Use WPF-UI theme brushes for surfaces and text
(`ApplicationBackgroundBrush`, `CardBackgroundFillColorDefaultBrush`,
`TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `ControlStrokeColorDefaultBrush`…).

The logo is an anvil-shaped spark in ember on steel; the app icon is `Assets/ForgeDesk.ico`.

## Typography

* UI: Segoe UI Variable (system). Code, paths, hashes, logs: **Cascadia Mono** → Consolas fallback
  (`ForgeMonoFont` resource).
* Scale: Caption 12 · Body 13 (default, dense) · Body Strong 13 semibold · Subtitle 15 semibold ·
  Title 20 semibold · Display 28 semibold. Styles: `ForgeCaption`, `ForgeBody`, `ForgeBodyStrong`,
  `ForgeSubtitle`, `ForgeTitle`, `ForgeDisplay`, `ForgeMono`.
* Secondary information uses `TextFillColorSecondaryBrush`, never a smaller font alone.

## Layout and spacing

* 4 px grid: 4 · 8 · 12 · 16 · 24 · 32. Page padding 24, card padding 16, gaps between cards 12.
* Corner radius: 8 for cards/panels, 4 for controls and pills.
* List rows: 32 px (comfortable dense), tables 28 px. Everything long is virtualized.
* Window: custom title bar (logo, breadcrumb, centered search box "Search or run a command
  Ctrl+K", GitHub account, caption buttons), a left sidebar (Home, Activity, pinned & recent
  projects with status dots, Add project, Settings), the page, and a 26 px status bar
  (branch & changes, running commands, background operations, GitHub status).
* Project workspace: header (avatar, name, path, branch selector, sync buttons with ahead/behind,
  quick commands Dev/Build/Test, "Open in…" menu) above a tab strip: Overview · Git · Files ·
  Terminal · Commands · Tasks · GitHub · Releases · Insights · Activity. Tabs show badges
  (changes count, open tasks, CI state dot).
* Master–detail views (Git changes, history, issues, runs) use resizable `GridSplitter` panes
  with sensible minimum widths.

## States — every data view has all four

1. **Loading** — skeleton rows (`SkeletonList`) for lists, `ProgressRing` + message for panels;
   show only after ~250 ms to avoid flicker.
2. **Empty** — `EmptyState`: Fluent icon, one-line title, one sentence explaining why it is
   empty, and the primary action that fixes it ("Initialize repository", "Sign in to GitHub",
   "Create your first task").
3. **Error** — `ErrorPanel` bound to `ViewModelBase.Error`: title, message, hint, collapsible
   technical details, **Retry**. Errors never vanish silently.
4. **Content**.

## Feedback and interaction

* Buttons running an operation show an inline `ProgressRing` and are disabled while running.
* Success of an explicit action → short snackbar ("Pushed 3 commits to origin/main").
  Failure → error snackbar that stays until dismissed and offers details.
* Destructive actions (discard changes, delete branch, force push, remove project) ask for
  confirmation in a dialog whose confirm button is red and names the action.
* Every list item has a context menu with the relevant actions; double-click opens / activates.
* Hover and pressed states come from WPF-UI; selection uses the accent.
* Motion: 120–180 ms fades/slides for page and panel changes only. No decorative animation.
* Tooltips on every icon-only button, including the keyboard shortcut.

## Keyboard

| Shortcut | Action |
|---|---|
| `Ctrl+K` / `Ctrl+Shift+P` | Command palette |
| `Ctrl+P` | Go to file (current project) |
| `Ctrl+Shift+F` | Search in files |
| `Ctrl+O` | Add local project |
| `Ctrl+Shift+O` | Clone repository |
| `Ctrl+1` … `Ctrl+0` | Workspace tabs |
| `Ctrl+Enter` | Commit (Git tab) |
| `Ctrl+Shift+Enter` | Commit and push |
| ``Ctrl+` `` | Terminal tab / new terminal |
| `Ctrl+N` | New task (Tasks tab) |
| `F5` | Refresh current view |
| `Ctrl+,` | Settings |
| `Alt+Left` | Back |
| `Esc` | Close palette / dialog / detail pane |

## Shared controls (`src/ForgeDesk.App/Controls`)

| Control | Purpose |
|---|---|
| `EmptyState` | `Icon`, `Title`, `Description`, `ActionText`/`ActionCommand`, `SecondaryActionText`/`SecondaryActionCommand` |
| `ErrorPanel` | `Error` (`ErrorInfo`), `RetryCommand`; shows hint and expandable details |
| `LoadingState` | `Message`, delayed `ProgressRing` |
| `SkeletonList` | shimmering placeholder rows, `RowCount` |
| `StatusDot` | `Status` (`Neutral`, `Success`, `Warning`, `Danger`, `Info`, `Running`) |
| `ProjectAvatar` | `ProjectName`, `Color`, `Size` — rounded square with initials |
| `SectionHeader` | `Title`, `Subtitle`, right-aligned `Actions` content |
| `Pill` | `Text`, `Kind` (same palette as `StatusDot`) |
| `KeyHint` | renders a shortcut as keycaps (`Keys="Ctrl+K"`) |
| `CiBadge` | `State` (`CiState`) icon + label |
| `CodeView` | read-only AvalonEdit with syntax highlighting, themed |
| `DiffView` | unified diff with line numbers, hunk headers and per-hunk actions |

Converters (`Themes/Converters.xaml`): `BoolToVisibility`, `InverseBoolToVisibility`,
`NullToCollapsed`, `NotNullToVisible`, `ZeroToCollapsed`, `RelativeTime`, `Duration`,
`SymbolFromName`, `StatusToBrush`, `BytesToText`, `UpperCase`.

## Writing

* English, sentence case, concise. Buttons are verbs ("Commit 3 files", "Sign in", "Retry").
* Explain *why* and *what next* in empty and error states. Avoid jargon when a plain word works.
* Numbers are formatted (1 234 files, 12.4 MB, 3 min ago).
