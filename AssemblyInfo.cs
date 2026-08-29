using System.Windows;
using System.Runtime.CompilerServices;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Lets TodoApp.Tests exercise internal-but-pure helper methods (e.g. GoogleDriveService's
// EscapeDriveQueryValue) directly, instead of forcing them public just to be testable or leaving
// them untested behind a private modifier.
[assembly: InternalsVisibleTo("TodoApp.Tests")]
