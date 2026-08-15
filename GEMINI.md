# Tasky Development & Engineering Standards

## 🎯 Core Operating Philosophy
When handling any feature request, edit, bug fix, or UI change:

### 1. Interpret the "What" and "Why" First
* **Never apply narrow, blind, or superficial edits.**
* Always deconstruct the underlying user goal and design intent:
  - *What problem is the user trying to solve?*
  - *Why is this interaction model or workflow needed?*
  - *How does this change affect the rest of the document, note lifecycle, and overall system architecture?*

### 2. Follow Industry Standards & Application Best Practices
* Every feature and UI interaction should follow proven standards from world-class productivity applications (e.g., Notion, Linear, Slack, Apple Notes, Obsidian).
* Benchmark against established UI/UX patterns:
  - **Inline Media & Attachments:** Floating contextual actions (top-right badge on hover/visible), double-click to open, lightboxes for images, full right-click context menus (`Open`, `Open in Explorer`, `Copy`, `Save Copy As...`, `Delete`), and drag-and-drop ingestion.
  - **Interaction Feedback:** Clear hover states, distinct tooltips, warning accents for destructive actions (`#D63638`), and proper cursor affordances (`Hand`).
  - **Keyboard & Mouse Accessibility:** Double-click conventions, standard shortcuts (`Ctrl+V`, `Delete`, `Escape`), and smooth hit-testing suppression.

### 3. Holistic & Edge-Case Engineering
* When adding or modifying a capability (such as opening, closing, deleting, or resizing an item), think through all related elements (files, photos, tables, links, checklists) to maintain system-wide consistency.
* **Storage & Lifecycle Management:**
  - Sandboxed dedicated per-task folder (`%USERPROFILE%\Documents\Tasky\Attachments\<TaskId>\`).
  - Deleting an attachment or photo must immediately remove the physical file from disk.
  - Deleting a task or emptying trash must purge the task's storage directory.
  - Serializing/deserializing via XAML must clean interactive handles to avoid crashes and re-hook seamlessly on load.

### 4. Transparent Communication & Verification
* Explain the reasoning behind architectural decisions and how the implementation adheres to industry best practices.
* Always verify that `dotnet build` succeeds with `0 Warning(s), 0 Error(s)` before concluding.
