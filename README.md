# Tasky

A Windows desktop task manager built with WPF (.NET 9). Tasky is a single-window, three-pane app
(sidebar / task list / editor) built around one idea: each task isn't just a title, it's a small
document — mix in notes, photos, links, files, and checklists, tag it, give it a due date, and
let the app take care of not losing your work.

## Features

### Task organization
- Sidebar sections: **All Tasks**, **Recurring**, **Completed**, **Trash**, plus a **Tags**
  section that fills in automatically from whatever tags are actually in use
- Pin tasks to keep them at the top of the list regardless of sort order
- Sort by name, due date, date modified, or date created
- Search by title, note text, or tag (`tag:work` searches tags only)
- Quick filters: Overdue, Due Today, No Due Date, Recurring, With Link, With Attachment
- Multi-select (Ctrl/Shift-click) with bulk actions: mark completed, pin/unpin, move to trash,
  restore, delete permanently

### Rich task notes
Each task's body is a free-form stream of blocks, added in any order, reordered by drag handle,
and mixed freely:
- **Text** — rich text with bold/italic/underline, font family/size, text color, and highlight
- **Photos** — paste from clipboard or drag-and-drop; click to view full-size
- **Links** — paste a bare URL and it's auto-converted into a link block
- **Files** — attach any file type, open it with its default app from inside Tasky
- **Checklists** — add/check off sub-items within a task, independently reorderable too

Spell check runs on every text field, with themed right-click suggestions.

Tasks also carry tags (picked from existing ones or typed fresh — always lowercased), a due
date, and an optional recurrence rule (daily/weekly/monthly). Completing a recurring task
automatically spawns the next occurrence with the due date advanced.

### Data safety
- Auto-save (debounced, so it doesn't hammer the disk while you type), with a small status
  indicator ("Saving…" / "Saved") and a clear failure message if a write doesn't go through
- Rolling backups taken before every save, with a **Restore from Backup** dialog to roll back to
  any recent snapshot
- Ctrl+Z undo for deletes, trashing, restoring, tag removal, block removal, and marking a task
  complete/incomplete
- Completed and trashed tasks lock from editing — you can still restore/reopen them, but their
  content can't be changed by accident

### Quick capture
- Global hotkey **Ctrl+Alt+T** and a system tray icon open a small always-on-top box to jot down
  a task from anywhere, without switching to the main window
- Ctrl+N / the toolbar "+" for a new task inline

### Multiple files
- New/Open/Save Data File As... to keep separate task files for separate purposes
- Each file's attachments and backups travel alongside it in sibling folders, so the whole thing
  stays portable (e.g. inside a synced OneDrive folder)

### Personalization
- Light and dark themes, including the window title bar
- Collapsible sidebar and a distraction-free Focus Mode (F11)
- Always-on-top toggle
- Window size, position, and last-open task are remembered between launches

### Accessibility
- Confirmation dialogs respond to Enter/Esc, not just mouse clicks
- Icon-only buttons carry screen-reader labels, not just tooltips

## Requirements

- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Run it

```bash
git clone https://github.com/stephenh678/Tasky.git
cd Tasky
dotnet run
```

## Build it

```bash
dotnet build
```

## Produce a standalone build

To get a build that doesn't require the .NET SDK on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The result lands in `publish\`. Despite `PublishSingleFile`, WPF's native rendering libraries
(`D3DCompiler_47_cor3.dll`, `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`,
`vcruntime140_cor3.dll`) can't be embedded and are published alongside `Tasky.exe` as separate
files — **the whole `publish\` folder is the deliverable, not the exe by itself.** Zip the folder
if you're distributing it; the exe won't launch on its own without those DLLs next to it.

## Data storage

Tasky stores its data as a `.tasky` file (plain JSON under the hood) at
`Documents\Tasky\Tasky.tasky` by default — use **File → New/Open/Save Data File As...** to work
with a different file or location. Rolling backups live alongside the data file in a `Backups`
folder; attachments live in an `Attachments` folder the same way.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New task |
| `Delete` | Delete selected task(s) |
| `Ctrl+Z` | Undo |
| `Ctrl+O` | Open data file |
| `Ctrl+Shift+S` | Save data file as |
| `F11` | Toggle Focus Mode |
| `Ctrl+Alt+T` | Global quick-add (works even when Tasky isn't focused) |
| `Ctrl+Shift+T` | Add text block to the selected task |
| `Ctrl+Shift+P` | Add photo block to the selected task |
| `Ctrl+Shift+L` | Add link block to the selected task |
| `Ctrl+Shift+F` | Add file block to the selected task |
| `Ctrl+Shift+C` | Add checklist block to the selected task |
