# Contributing to Tasky

Thank you for your interest in contributing to Tasky! This document provides guidelines for contributing code, reporting issues, and improving the application.

## Code Style & Standards

### C# & .NET Conventions
- **Naming**: Follow standard C# naming conventions
  - `PascalCase` for classes, properties, methods, and public members
  - `_camelCase` for private fields
  - `camelCase` for local variables
  - `CONSTANT_CASE` or `PascalCase` for constants
  - `IInterfaceName` for interfaces (prefix with `I`)
- **Nullable Reference Types**: Enabled project-wide. Use `?` to indicate nullable types.
- **Async/Await**: Use `async`/`await` pattern. Avoid `Task.Wait()` and `.GetAwaiter().GetResult()` on UI thread.
- **Exception Handling**: Catch specific exceptions, not bare `catch { }`. Always log caught exceptions.
- **Documentation**: Add XML documentation comments (`///`) to all public types and methods.

### EditorConfig
The `.editorconfig` file enforces consistent formatting across the codebase. Your IDE should automatically apply these rules. Ensure no EditorConfig violations before submitting PRs.

## Architecture & Design Patterns

### MVVM Architecture
Tasky uses Model-View-ViewModel (MVVM) pattern:
- **Models** (`Models/`): Data objects with validation logic
- **ViewModels** (`ViewModels/`): Business logic, state management, command handling
- **Views** (`*.xaml`/`*.xaml.cs`): UI layer, minimal code-behind
- **Services** (`Services/`): Data access, configuration, system integration

### Dependency Injection
Not yet in place - this is a known gap, not a followed convention. `MainViewModel` currently takes
no constructor and instantiates its services directly (`new SettingsStore()`, `new
GoogleDriveService()`, etc.); there's no `ITodoStore` or other service interfaces, and no DI
container wired up. Introducing constructor-injected service interfaces is tracked as future work
(service interfaces, then a DI container) - see the project roadmap. Until that lands, new code
should follow the existing direct-instantiation pattern rather than inventing DI piecemeal in one
corner of the codebase.

### Data Persistence
- Use `TodoStore` for task/app state persistence
- Use `SettingsStore` for user preferences
- Implement proper async save patterns; avoid blocking UI thread
- Always handle `IOException` and `UnauthorizedAccessException` gracefully

### Threading
- UI thread operations must be responsive (avoid `Thread.Sleep`)
- Use `await Task.Delay()` for non-blocking delays
- Serialize file access with `SemaphoreSlim` to prevent concurrent writes
- Use `DispatcherTimer` for periodic UI updates

## Submitting Changes

### Before You Start
1. Check existing issues to avoid duplicate work
2. For major changes, open an issue first to discuss approach
3. Keep changes focused on a single concern

### Pull Request Checklist
- [ ] Code follows C# naming and style conventions
- [ ] No bare `catch { }` blocks — catch specific exceptions
- [ ] No `Thread.Sleep()` — use `await Task.Delay()` instead
- [ ] All public methods have XML documentation comments
- [ ] New models/services have validation logic
- [ ] Async operations properly handle cancellation tokens
- [ ] UI operations remain responsive
- [ ] Tests pass (if applicable)
- [ ] EditorConfig violations resolved

### Commit Messages
Use clear, descriptive commit messages:
```
Fix event handler memory leaks in RichTextBoxBehavior

- Store handler references to properly unsubscribe
- Prevent duplicate saves on checkbox toggle
- Reduces memory footprint over extended sessions

Fixes #123
```

## Bug Reports

Include:
1. Steps to reproduce
2. Expected behavior
3. Actual behavior
4. Environment (Windows version, .NET version, Tasky version)
5. Screenshots/crash logs if applicable

## Performance & Testing

- Profile changes with large task counts (1000+)
- Test with network drives (OneDrive, SharePoint)
- Verify autosave doesn't block UI
- Check for memory leaks in long sessions
- Test edge cases (empty searches, special characters in tags, etc.)

## Questions?

Feel free to open an issue or reach out. We're here to help!
