using System;

namespace TodoApp.Models;

// A saved view is just a named, persisted search-box query string - not a second filter system.
// Mirrors Tasky Web's {id, label, query} localStorage record (docs/js/app.js), except this one
// travels in AppState so it syncs across devices via Google Drive like tasks/tags already do,
// instead of sitting per-browser in localStorage the way Web's copy does.
public class SavedView
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
}
