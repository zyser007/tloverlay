using TLOverlay.Core.Input;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// A gesture is what ends up in settings.json, so what matters is that anything
/// written can be read back. A binding that does not survive the round trip is a
/// hotkey the player set and then silently lost.
/// </summary>
public class HotKeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Alt+1")]
    [InlineData("Win+Space")]
    [InlineData("Ctrl+Alt+Shift+Win+K")]
    public void WhatIsWrittenCanBeReadBack(string text)
    {
        Assert.True(HotKeyGesture.TryParse(text, out HotKeyGesture parsed));
        Assert.Equal(text, parsed.ToString());

        Assert.True(HotKeyGesture.TryParse(parsed.ToString(), out HotKeyGesture again));
        Assert.Equal(parsed, again);
    }

    [Fact]
    public void ModifiersAreAlwaysWrittenInTheSameOrder()
    {
        // So two gestures can be compared as strings, which is how a duplicate
        // binding is detected.
        Assert.True(HotKeyGesture.TryParse("Alt+Ctrl+T", out HotKeyGesture parsed));
        Assert.Equal("Ctrl+Alt+T", parsed.ToString());
    }

    [Theory]
    [InlineData("control+alt+t")]
    [InlineData("CTRL + ALT + T")]
    [InlineData("ctl+alt+t")]
    public void TheSpellingsSomeoneMightTypeByHandAreAccepted(string text)
    {
        // settings.json is meant to be readable, which means it is also editable.
        Assert.True(HotKeyGesture.TryParse(text, out HotKeyGesture parsed));
        Assert.Equal("Ctrl+Alt+T", parsed.ToString());
    }

    [Fact]
    public void TheNumberRowReadsAsADigit()
    {
        // The Key enum spells it D1, which no player would recognise.
        Assert.True(HotKeyGesture.TryParse("Ctrl+1", out HotKeyGesture parsed));

        Assert.Equal("D1", parsed.KeyName);
        Assert.Equal("Ctrl+1", parsed.ToString());
    }

    [Theory]
    [InlineData("T")]
    [InlineData("F5")]
    public void AKeyWithNoModifierIsRefused(string text)
    {
        // A global hotkey with no modifier is swallowed everywhere, which would
        // take the key away from the game this overlay sits on.
        Assert.False(HotKeyGesture.TryParse(text, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    public void NonsenseIsRefusedRatherThanGuessedAt(string? text)
    {
        Assert.False(HotKeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void KeyNamesKeepTheCasingTheKeyEnumUses()
    {
        Assert.True(HotKeyGesture.TryParse("ctrl+alt+f12", out HotKeyGesture parsed));
        Assert.Equal("F12", parsed.KeyName);
    }
}
