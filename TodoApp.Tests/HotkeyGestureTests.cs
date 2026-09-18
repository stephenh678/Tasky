using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", ModifierKeys.Control | ModifierKeys.Alt, Key.T)]
    [InlineData("ctrl + shift + f9", ModifierKeys.Control | ModifierKeys.Shift, Key.F9)]
    [InlineData("Win+Alt+N", ModifierKeys.Windows | ModifierKeys.Alt, Key.N)]
    [InlineData("Alt+1", ModifierKeys.Alt, Key.D1)]
    [InlineData("Control+Space", ModifierKeys.Control, Key.Space)]
    public void TryParse_ReadsModifiersAndKey(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("T")]            // no modifier: would swallow every "t" typed anywhere
    [InlineData("Shift+T")]      // Shift alone is just a capital letter
    [InlineData("Ctrl+Alt")]     // no key
    [InlineData("Ctrl+T+Y")]     // two keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+123")]     // Enum.TryParse would happily accept a raw number
    public void TryParse_RejectsUnusableCombinations(string? text)
        => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Alt+Win+N")]
    [InlineData("Alt+1")]
    public void ToString_RoundTrips(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Fact]
    public void ParseOrDefault_FallsBackToCtrlAltT()
    {
        Assert.Equal(HotkeyGesture.Default, HotkeyGesture.ParseOrDefault("garbage").ToString());
        Assert.Equal(HotkeyGesture.Default, HotkeyGesture.ParseOrDefault(null).ToString());
    }

    [Fact]
    public void Win32Modifiers_MapToRegisterHotKeyFlags()
    {
        var gesture = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.T);
        Assert.Equal(0x0001u | 0x0002u, gesture.Win32Modifiers);
        Assert.Equal((uint)'T', gesture.VirtualKey);
    }

    [Fact]
    public void ViewModel_QuickAddHotkey_NormalisesPersistsAndIgnoresInvalidValues()
    {
        var dir = TestViewModels.NewDirectory();
        var vm = TestViewModels.Create(dir);
        var changes = 0;
        vm.QuickAddHotkeyChanged += () => changes++;

        Assert.Equal("Ctrl+Alt+T", vm.QuickAddHotkey);
        vm.QuickAddHotkey = "ctrl+shift+q";
        vm.QuickAddHotkey = "Shift+Q"; // invalid - ignored

        Assert.Equal("Ctrl+Shift+Q", vm.QuickAddHotkey);
        Assert.Equal(1, changes);
        Assert.Equal("Ctrl+Shift+Q", TestViewModels.Create(dir).QuickAddHotkey); // survived a "restart"
    }
}
