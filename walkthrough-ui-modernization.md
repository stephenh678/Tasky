# Tasky UI Modernization (Pre-Release) Walkthrough

## Summary of Completed Work

We modernized both the **Tasky Desktop (WPF / .NET 9)** and **Tasky Web & Mobile (PWA)** applications following modern design systems (Linear, Raycast, Apple HIG, and Fluent 2). All changes have been committed cleanly to the `pre-release` branch.

---

## 1. Tasky Desktop (WPF) UI Modernization

### Color System & Themes
- **[LightTheme.xaml](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/Themes/LightTheme.xaml)**:
  - Canvas background transitioned from pure white to a modern neutral slate (`#F8FAFC`).
  - Sidebar background styled to soft slate (`#F1F5F9`).
  - Content panes styled with clean white cards (`#FFFFFF`).
  - Typography upgraded to deep slate (`#0F172A`) for high contrast readability and secondary slate (`#475569`).
  - Accent color upgraded to high-vibrancy modern sapphire (`#2563EB`).
  - All color pairings audited against WCAG AA/AAA guidelines (contrast ratios range from 4.8:1 to 15.6:1).
- **[DarkTheme.xaml](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/Themes/DarkTheme.xaml)**:
  - Migrated from washed-out grays to a rich obsidian & slate palette (`#0B0F17` canvas, `#111827` sidebar, `#161F30` content cards).
  - Borders upgraded to refined slate (`#1E293B`).
  - Text upgraded to crisp white (`#F8FAFC`) and muted slate (`#94A3B8`).
  - Accent color upgraded to vibrant sapphire (`#3B82F6`).

### Controls & Components
- **[ControlStyles.xaml](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/Themes/ControlStyles.xaml)**:
  - **`TaskCheckBox`**: Updated to modern 6px corner radius with smooth SVG checkmark path and AccentBrush fill.
  - **`FilterCheckBox`**: Updated with matching 6px corner radius and smooth hover states.
  - **`IconButton`**: Standardized to 28×28px with 6px corner radius, subtle hover feedback, and clear focus cues.
  - **`ThemedListBoxItem` & `SidebarListBoxItem`**: Modern card-like items with 8px corner radius, left accent pill indicator on selection, and comfortable padding.
  - **`Button`**: Modernized with 6px corner radius, subtle border highlights, and tactile active/pressed feedback.
  - **`ComboBox`**: Enhanced with 6px radius, smooth drop shadow (`BlurRadius="16"`), and refined item highlights.
- **[MainWindow.xaml](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/MainWindow.xaml)**:
  - Upgraded the search box into a modern 16px rounded pill container with an interior search icon and smooth focus ring.
  - Filter and Tag dropdown popups upgraded with 8px corner radius and modern elevation drop shadows.

---

## 2. Tasky Web & Mobile (PWA) UI Modernization

### Design Tokens & Theme Parity
- **[docs/css/styles.css](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/docs/css/styles.css)**:
  - Fully synchronized CSS custom properties across `:root` (light theme), `@media (prefers-color-scheme: dark)`, and `:root[data-theme="dark"]`.
  - Added [check-dark-palette.ps1](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/check-dark-palette.ps1) to guarantee 100% parity across all 16 theme variables.

### Header & Navigation
- **Frosted Glass Header (`.app-header`)**: Implemented `backdrop-filter: blur(16px)` with semi-transparent background and clean border line.
- **Brand Identity**: Modern gradient logo tile with subtle drop shadow and refined typography.
- **Modern Action Pills**: Toolbar groups styled as compact pills with unified icon sizing.

### Task List & Detail View
- **Card-Style Tasks (`.task-list li`)**: Refined to 12px rounded cards with hover elevation, smooth selection rings, and priority indicators.
- **Property Pills (`.editor-field`, `.tag-chip`)**: Styled like modern Notion/Linear property badges with clean borders, hover highlights, and 6px rounded checkbox corners matching Desktop.
- **Dashboard & Empty States (`.editor-empty-dashboard`)**: Stat counter cards with hover elevation and crisp typographic hierarchy.

### Mobile Experience (Viewports < 768px)
- **Frosted Glass Mobile Tab Bar (`.mobile-tabbar`)**: Docked bottom navigation bar with frosted glass blur (`backdrop-filter: blur(16px)`), enlarged tap targets, and animated active tab indicators.
- **Floating Action Button (`#list-pane > .new-task-btn`)**: Vibrant circular 56px action button with colored glow shadow and tactile scale transition.
- **Bottom Sheets**: Smooth slide-up bottom sheets with handle bars on narrow screens (< 640px).

---

## 3. How to Test Web, Mobile, and Desktop

### A. Testing Web & Mobile Simultaneously (Interactive Test Lab)

We created a test harness [docs/test-preview.html](file:///C:/Users/steph/Documents/Claude%20Code/Tasky/docs/test-preview.html) and a local preview server:

1. In PowerShell or Command Prompt, run:
   ```powershell
   .\serve-web.ps1
   ```
   *(Or double-click `serve-web.bat` in File Explorer).*

2. This will launch **`http://localhost:5500/test-preview.html`** in your default browser:
   - **Dual View**: Test both the Desktop Web view (3-pane layout) and the Mobile view (iPhone / Pixel frame) side-by-side.
   - **Device Presets**: Switch between iPhone 15, Pixel 8, Galaxy S23, and iPad Mini.
   - **Theme Toggle**: Switch between Light and Dark mode instantly.
   - **Direct App Link**: Click "Open App in Full Tab" to test the web app directly at full screen (`http://localhost:5500/index.html`).
   - **On-Phone Wi-Fi Testing**: The server outputs your machine's Wi-Fi IP address (e.g. `http://192.168.x.x:5500`), allowing you to test directly on your physical smartphone.

### B. Testing Tasky Desktop (WPF)

To run the updated WPF Desktop application:
```powershell
dotnet run --project TodoApp.csproj
```

### C. Automated Test Suite

All 254 existing unit and viewmodel tests pass:
```powershell
dotnet test TodoApp.Tests/TodoApp.Tests.csproj
```
All dark-palette variables pass verification:
```powershell
.\check-dark-palette.ps1
```
