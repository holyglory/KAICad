using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeKeyboardTests
{
    [TestMethod]
    public void PopulatedCanvasGuardRejectsBlankLightAndDarkFramesWithoutTreatingMinorNoiseAsDrawing()
    {
        foreach (byte background in new byte[] { 45, 255 })
        {
            byte[] blank = Enumerable.Repeat(background, 32 * 32 * 3).ToArray();
            Assert.AreEqual(0, NativeSessionTests.CountCanvasInk(blank));
            byte[] noise = (byte[])blank.Clone(); noise[9] = (byte)(background - 1);
            Assert.AreEqual(0, NativeSessionTests.CountCanvasInk(noise));
            byte[] drawing = (byte[])blank.Clone();
            for (int pixel = 100; pixel < 500; ++pixel)
                for (int channel = 0; channel < 3; ++channel) drawing[pixel * 3 + channel] = background == 255 ? (byte)0 : (byte)255;
            Assert.AreEqual(400, NativeSessionTests.CountCanvasInk(drawing));
        }
        Assert.ThrowsExactly<ArgumentException>(() => NativeSessionTests.CountCanvasInk([]));
        Assert.ThrowsExactly<ArgumentException>(() => NativeSessionTests.CountCanvasInk([0, 1]));
    }

    [TestMethod]
    public void DragCoordinatesCannotBeSilentlyIgnoredByAClickCommand()
    {
        NativeKeyboard.ValidateDragArguments("drag", 10, 20);
        NativeKeyboard.ValidateDragArguments("click", null, null);
        NativeKeyboard.ValidateDragArguments("Return", null, null);
        Assert.ThrowsExactly<ArgumentException>(() => NativeKeyboard.ValidateDragArguments("click", 10, 20));
        Assert.ThrowsExactly<ArgumentException>(() => NativeKeyboard.ValidateDragArguments("motion", 10, 20));
        Assert.ThrowsExactly<ArgumentException>(() => NativeKeyboard.ValidateDragArguments("drag", null, 20));
        Assert.ThrowsExactly<ArgumentException>(() => NativeKeyboard.ValidateDragArguments("drag", 10, null));
    }

    [TestMethod]
    public void ExplicitPointerCommandsDoNotDependOnTheKeyboardFocusPreclick()
    {
        foreach (string key in new[] { "click", "right-click", "motion", "drag" })
            foreach (bool focus in new[] { false, true })
                Assert.IsTrue(NativeKeyboard.RequestsPointerInput(key, focus));
        Assert.IsFalse(NativeKeyboard.RequestsPointerInput("Return", false));
        Assert.IsFalse(NativeKeyboard.RequestsPointerInput("z", false));
        Assert.IsTrue(NativeKeyboard.RequestsPointerInput("z", true));
        Assert.IsFalse(NativeKeyboard.RequestsPointerInput("", false));
        Assert.IsFalse(NativeKeyboard.RequestsPointerInput("", true));
    }

    [TestMethod]
    public void RequestedLetterCaseAndPunctuationChooseTheActualKeymapLevel()
    {
        Assert.AreEqual((nuint)'A', NativeKeyboard.LiteralKeysym("A"));
        Assert.AreEqual((nuint)'_', NativeKeyboard.LiteralKeysym("_"));
        Assert.AreEqual((nuint)' ', NativeKeyboard.LiteralKeysym(" "));
        Assert.IsNull(NativeKeyboard.LiteralKeysym("Return"));
        Assert.IsNull(NativeKeyboard.LiteralKeysym(""));
        Assert.IsFalse(NativeKeyboard.RequiresShift('a', 'a', 'A'));
        Assert.IsTrue(NativeKeyboard.RequiresShift('A', 'a', 'A'));
        Assert.IsFalse(NativeKeyboard.RequiresShift('-', '-', '_'));
        Assert.IsTrue(NativeKeyboard.RequiresShift('_', '-', '_'));
        Assert.IsFalse(NativeKeyboard.RequiresShift(0xff0d, 0xff0d, 0xff0d)); // Return, not text case
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeKeyboard.RequiresShift('x', 'a', 'A'));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeKeyboard.RequiresShift(0, 0, 0));
    }

    [TestMethod]
    public void OnlyMissingWindowsDuringReadOnlyEnumerationAreRecoverable()
    {
        foreach (byte request in new byte[] { 3, 14, 15, 20 })
            Assert.IsTrue(NativeKeyboard.IsWindowEnumerationRace(3, request));
        foreach (byte error in new byte[] { 1, 2, 8, 9, 10 })
            Assert.IsFalse(NativeKeyboard.IsWindowEnumerationRace(error, 20));
        foreach (byte request in new byte[] { 4, 8, 12, 42, 132 })
            Assert.IsFalse(NativeKeyboard.IsWindowEnumerationRace(3, request));
    }

    [TestMethod]
    public void PopupReadinessExcludesHiddenMenusDialogsAndGrabWindows()
    {
        Assert.IsTrue(NativeKeyboard.IsVisiblePopup(2, 1, 334, 589));
        Assert.IsTrue(NativeKeyboard.IsVisiblePopup(2, 1, 223, 133));
        Assert.IsFalse(NativeKeyboard.IsVisiblePopup(0, 1, 334, 589));
        Assert.IsFalse(NativeKeyboard.IsVisiblePopup(1, 1, 334, 589));
        Assert.IsFalse(NativeKeyboard.IsVisiblePopup(2, 0, 334, 589));
        Assert.IsFalse(NativeKeyboard.IsVisiblePopup(2, 1, 10, 10));
        Assert.IsFalse(NativeKeyboard.IsVisiblePopup(2, 1, 334, 10));
    }
}
