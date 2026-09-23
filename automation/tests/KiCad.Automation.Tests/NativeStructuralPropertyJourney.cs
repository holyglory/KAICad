using KiCad.Automation.Model;
using KiCad.Automation.Native;
using P = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyStructuralProperties(NativeClient native, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        string project = Path.GetDirectoryName((await native.HandshakeAsync(token)).ProjectPath)!;
        string source = Path.Combine(project, "Controller.xml");
        var original = EngineeringDesignXml.Read(await File.ReadAllTextAsync(source, token), []);
        string owner = original.Structure.Blocks.Single(b => b.Name == "Control unit").Id.ToString("D");
        Task<P.StructuralEditorState> Read() => native.InvokeAsync<P.ReadStructuralEditor, P.StructuralEditorState>(
            new() { DocumentId = original.Structure.Id.ToString("D") }, token);
        async Task<P.StructuralEditorState> Wait(Func<P.StructuralEditorState, bool> predicate)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var current = await Read(); if (predicate(current)) return current;
                try { await Task.Delay(50, deadline.Token); }
                catch (OperationCanceledException)
                {
                    await CaptureStructural(display, Path.Combine(evidence, instanceId + "-quantity-timeout.png"), token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-quantity-timeout.json"), SchematicJson.Formatter.Format(current), token);
                    throw;
                }
            }
        }
        void Key(string key, string title = "Structure", bool control = false, int? x = null, int? y = null) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, title, control, x.HasValue, clickFromLeft: x, clickFromTop: y);
        void Type(string value, string title) { foreach (char c in value) Key(c.ToString(), title); }
        async Task Window(string title, bool visible = true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (NativeKeyboard.HasWindow(display, processId, title) != visible) await Task.Delay(50, deadline.Token);
        }
        async Task Save()
        {
            ulong before = (await Read()).CompletedSaveCount; Key("s", control: true);
            var saved = await Wait(s => s.CompletedSaveCount > before && !s.Saving);
            Assert.AreEqual("", saved.LastSaveError); Assert.IsFalse(saved.Dirty);
        }
        void Ok(string title) => NativeKeyboard.SchematicShortcut(display, processId, "click", title, false, false,
            clickFromRight: 60, clickFromBottom: 25);

        Key("click", x: 640, y: 400);
        NativeKeyboard.SchematicShortcut(display, processId, "a", "Structure", false, false, altKey: true);
        const string add = "Add custom property";
        await Window(add);
        Type("Supply", add); Key("Tab", add); Type("Operating supply voltage.", add);
        Key("Tab", add); Key("Down", add); Key("Down", add); // Requirement, not an inferred fact.
        Key("Tab", add); Type("At rated load", add);
        Key("Tab", add); Key(" ", add); // Reveal the optional numeric fields.
        Key("Tab", add); Key("Down", add); Key("Down", add); // Operating limit.
        Key("Tab", add); Type("V", add);
        Key("Tab", add); Type("not-a-number", add);
        Ok(add); await Window("Invalid number"); Key("Return", "Invalid number"); await Window("Invalid number", false);
        Assert.AreEqual(0, (await Read()).Document.Diagram.Properties.Count);
        // The rejected value stays in its focused field so it can be fixed.
        Key("a", add, true); Type("3.3", add);
        Key("Tab", add); Type("3", add);
        Key("Tab", add); Type("3.6", add);
        Key("Tab", add); // Unknown reason remains absent for these supplied fixture values.
        Key("Tab", add); Key("Down", add); // Percent tolerance.
        Key("Tab", add); Type("5", add); Key("Tab", add); Type("5", add);
        NativeKeyboard.SchematicShortcut(display, processId, "", add, false, false, observeGeometry: bounds =>
            Assert.IsTrue(bounds.X >= 0 && bounds.Y >= 0 && bounds.X + bounds.Width <= 1600 && bounds.Y + bounds.Height <= 1150,
                "The complete property form and its buttons must remain within the fixture display."));
        await CaptureStructural(display, Path.Combine(evidence, instanceId + "-quantity-dialog.png"), token);
        Ok(add); await Window(add, false);
        var created = await Wait(s => s.Document.Diagram.Properties.Any(p => p.OwnerId == owner && p.Key == "Supply"));
        var property = created.Document.Diagram.Properties.Single(p => p.OwnerId == owner && p.Key == "Supply").Clone();
        await Save();
        var persisted = EngineeringDesignXml.Read(await File.ReadAllTextAsync(source, token), []);
        var statement = persisted.Structure.Properties!.Single(p => p.Statement.Id.ToString("D") == property.Id).Statement;
        Assert.AreEqual(GuidanceStrength.Requirement, statement.Strength);
        Assert.AreEqual("At rated load", statement.Applicability);
        Assert.AreEqual(new GuidanceQuantity(ParameterKind.OperatingLimit, "V", 3.3m, 3m, 3.6m,
            new ParameterTolerance(ToleranceKind.Percent, 5m, 5m)), statement.Quantity);
        Assert.AreEqual(CircuitXml.Write(original.Circuit), CircuitXml.Write(persisted.Circuit));

        // Select the actual list row and use the visible Edit action through
        // native focus traversal, rather than invoking its C++ handler.
        Key("click", x: 180, y: 160);
        for (int i = 0; i < 4; i++) Key("Tab");
        Key("Down"); Key("Tab"); Key("Return");
        const string edit = "Edit custom property";
        await Window(edit); Key("Tab", edit); Key("a", edit, true); Type("Keep the supply close to the regulator.", edit);
        Ok(edit); await Window(edit, false);
        var edited = await Wait(s => s.Document.Diagram.Properties.Single(p => p.Id == property.Id).Text == "Keep the supply close to the regulator.");
        Assert.AreEqual(property.Quantity, edited.Document.Diagram.Properties.Single(p => p.Id == property.Id).Quantity);
        await Save();
        Key("click", x: 1050, y: 960); Key("z", control: true);
        await Wait(s => s.Document.Diagram.Properties.Single(p => p.Id == property.Id).Text == property.Text);
        Key("y", control: true);
        await Wait(s => s.Document.Diagram.Properties.Single(p => p.Id == property.Id).Text == "Keep the supply close to the regulator.");
        await Save();
        await CaptureStructural(display, Path.Combine(evidence, instanceId + "-quantity-inspector.png"), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-quantity-state.json"), SchematicJson.Formatter.Format(await Read()), token);
    }
}
