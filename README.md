# Tasky

A Windows desktop task manager built with WPF (.NET 9) — tasks with rich notes (text, photos,
links, file attachments, checklists), tags, due dates, recurring tasks, multi-select bulk
actions, rolling backups with restore, undo, and light/dark themes.

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

## Produce a standalone .exe

To get a single, double-click-able executable that doesn't require the .NET SDK on the target
machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The result lands in `publish\TodoApp.exe`.

## Data storage

Tasky stores its data as a `.tasky` file (plain JSON under the hood) at
`Documents\Tasky\Tasky.tasky` by default — use **File → New/Open/Save Data File As...** to work
with a different file or location. Rolling backups live alongside the data file in a `Backups`
folder; attachments live in an `Attachments` folder the same way.
