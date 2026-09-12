using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyBomSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        const string table = "Symbol Fields Table";
        string Prefix(string phase) => Path.Combine(evidence, processId + "-bom-" + phase);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        async Task<SchematicScreenDataSnapshot> Read()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            while (true)
            {
                try { return await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7)
                { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            }
        }
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicBomSettings settings)
        {
            var batch = new ApplySchematicItemBatch { Document = document, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                Description = "BOM settings XML round trip" };
            batch.Operations.Add(new SchematicItemOperation { SetBomSettings = settings.Clone() });
            return batch;
        }
        async Task Same(SchematicScreenData expected, SchematicScreenData actual, string phase)
        {
            if (!expected.Equals(actual))
            {
                await File.WriteAllTextAsync(Prefix(phase + "-expected.xml"), SchematicDataXml.Write(expected), token);
                await File.WriteAllTextAsync(Prefix(phase + "-actual.xml"), SchematicDataXml.Write(actual), token);
            }
            Assert.AreEqual(expected, actual, "BOM edit must preserve every unrelated native object and property.");
            Assert.AreEqual(expected, SchematicDataXml.Read(SchematicDataXml.Write(actual)));
        }
        async Task Capture(string phase)
        {
            await NativeKeyboard.CaptureAsync(display, Prefix(phase + ".png"), token);
            var observed = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
            Assert.AreEqual(observed.Preview.Revision, observed.Snapshot.Revision);
            await File.WriteAllTextAsync(Prefix(phase + ".xml"), SchematicDataXml.Write(observed.Snapshot.Data), token);
        }
        async Task Reopen(SchematicScreenData expected, string phase)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
            await Same(expected, (await Read()).Data, phase);
        }
        void Key(string key, string title = table, bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, title, control, false, altKey: alt);
        void Type(string text, string title = table)
        {
            Key("a", title, control: true);
            foreach (char character in text) Key(character.ToString(), title);
        }
        async Task Window(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, title) != visible)
                { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            }
            catch
            { await NativeKeyboard.CaptureAsync(display, Prefix("window-failure.png"), CancellationToken.None); throw; }
        }
        async Task OpenTable()
        {
            Key("t", "Schematic Editor", alt: true); Key("End", "Schematic Editor");
            // Generate BOM is five selectable entries above Reload Plugins.
            for (int index = 0; index < 5; ++index) Key("Up", "Schematic Editor");
            Key("Return", "Schematic Editor");
            await Window(table, true);
            await NativeSetupUi.StableGeometry(display, processId, token, table);
        }
        async Task CloseTable(bool accept)
        {
            if (accept) NativeKeyboard.SchematicShortcut(display, processId, "click", table, false,
                clickFromRight: 60, clickFromBottom: 25);
            else Key("Escape");
            await Window(table, false);
        }
        void Filename(string text)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", table, false,
                clickFromRight: 180, clickFromTop: 60);
            Type(text); Key("Tab");
        }
        async Task UndoRedo(SchematicScreenData original, SchematicScreenData changed)
        {
            foreach (var (key, expected) in new[] { ("z", original), ("y", changed) })
            {
                var before = await Read();
                NativeKeyboard.SchematicShortcut(display, processId, key, focusCanvas: false);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                int delay = 25;
                SchematicScreenDataSnapshot after;
                do
                {
                    limit.Token.ThrowIfCancellationRequested();
                    after = await Read();
                    if (after.Revision.Equals(before.Revision))
                    { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
                } while (after.Revision.Equals(before.Revision));
                await Same(expected, after.Data, "history-" + key);
            }
        }

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var original = await Read();
        Assert.IsNotNull(original.Data.Metadata.BomSettings);
        var desired = original.Data.Clone(); desired.Metadata.BomSettings = SchematicBomSettingsTests.Settings();
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(original, desired.Metadata.BomSettings);
        foreach (int kind in Enumerable.Range(0, 4))
        {
            var invalid = batch.Clone(); invalid.OperationId = Guid.NewGuid().ToString("D");
            var bom = invalid.Operations[0].SetBomSettings;
            if (kind == 0) bom.CurrentView = null;
            if (kind == 1) bom.CurrentFormat = null;
            if (kind == 2) bom.SavedViews[0].FilterScope = (SchematicBomFilterScope)99;
            if (kind == 3) bom.SavedFormats[0] = SchematicBomFormat.Parser.ParseFrom(
                bom.SavedFormats[0].ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
            Assert.AreEqual(original, await Read());
        }
        var failed = batch.Clone(); failed.OperationId = Guid.NewGuid().ToString("D");
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(original, await Read(), "A later failed operation must restore BOM settings and revision.");
        var appliedResult = await Apply(batch);
        Assert.IsTrue(appliedResult.BomSettingsChanged);
        var applied = await Read();
        Assert.AreEqual(original.Revision.Sequence + 1, applied.Revision.Sequence);
        await Same(desired, applied.Data, "applied");
        Assert.AreEqual(appliedResult, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(applied, await Read());
        var noop = await Apply(Batch(applied, desired.Metadata.BomSettings));
        Assert.IsFalse(noop.BomSettingsChanged); Assert.AreEqual(applied, await Read());
        await Capture("api-applied");
        await UndoRedo(original.Data, desired);
        await Reopen(desired, "reopened");
        var empty = desired.Clone();
        empty.Metadata.BomSettings.CurrentView.Fields.Clear();
        empty.Metadata.BomSettings.SavedViews.Clear(); empty.Metadata.BomSettings.SavedFormats.Clear();
        empty.Metadata.BomSettings.ExportFilename = "";
        await Apply(Batch(await Read(), empty.Metadata.BomSettings));
        await Reopen(empty, "empty-reopened");
        await Apply(Batch(await Read(), original.Data.Metadata.BomSettings));
        await Reopen(original.Data, "original-restored");

        // Normalize the native table's first-use field inventory, then require
        // subsequent no-op/cancel operations to preserve that exact baseline.
        await OpenTable(); await Capture("initial-table"); await CloseTable(false);
        var baseline = await Read(); await Reopen(baseline.Data, "table-baseline");
        baseline = await Read();
        await OpenTable();
        var locked = Batch(baseline, desired.Metadata.BomSettings);
        var comparison = baseline.Data.Metadata.SymbolComparison.Clone(); comparison.MissingFields = !comparison.MissingFields;
        locked.Operations.Insert(0, new SchematicItemOperation { SetSymbolComparison = comparison });
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(locked))).Status);
        Assert.AreEqual(baseline, await Read(), "Busy table rejection must roll back preceding operations.");
        Filename("CancelledBom.csv"); await Capture("filename-pending-cancel");
        await CloseTable(false);
        Assert.AreEqual(baseline, await Read(), "Cancel must discard an unaccepted filename without a revision.");

        await OpenTable(); Filename("DiscardedWithPreset.csv");
        // The filename row follows the format selector in native tab order.
        Key("ISO_Left_Tab"); Key("ISO_Left_Tab");
        Key("space"); await NativeSetupUi.WaitForPopup(display, processId, true, token, table);
        Key("End"); Key("Up"); Key("Return");
        await Window("Save BOM Preset", true);
        Type("AutomationBom", "Save BOM Preset"); Key("Return", "Save BOM Preset");
        await Window("Save BOM Preset", false);
        await Capture("preset-pending");
        Assert.AreEqual(baseline, await Read(), "A pending preset is not a committed project edit.");
        await CloseTable(false);
        var changed = await Read();
        var expected = baseline.Data.Clone();
        var savedFormat = expected.Metadata.BomSettings.CurrentFormat.Clone(); savedFormat.Name = "AutomationBom";
        expected.Metadata.BomSettings.CurrentFormat = savedFormat.Clone();
        expected.Metadata.BomSettings.SavedFormats.Add(savedFormat);
        Assert.AreEqual(baseline.Revision.Sequence + 1, changed.Revision.Sequence);
        await Same(expected, changed.Data, "manual-preset");
        await UndoRedo(baseline.Data, expected);
        await Reopen(expected, "manual-preset-reopened");

        var beforeFilename = await Read();
        await OpenTable(); Filename("ManualBom.csv"); await Capture("filename-pending-ok");
        await CloseTable(true);
        var filenameExpected = beforeFilename.Data.Clone(); filenameExpected.Metadata.BomSettings.ExportFilename = "ManualBom.csv";
        var filenameChanged = await Read();
        Assert.AreEqual(beforeFilename.Revision.Sequence + 1, filenameChanged.Revision.Sequence);
        await Same(filenameExpected, filenameChanged.Data, "manual-filename");
        await UndoRedo(beforeFilename.Data, filenameExpected);
        await Reopen(filenameExpected, "manual-filename-reopened");
        // The closed table no longer owns pending preferences: a fresh request
        // must recover from AS_BUSY, with no relaxation of stale revision checks.
        await Apply(Batch(await Read(), original.Data.Metadata.BomSettings));
        await Reopen(original.Data, "final-restored");
    }
}
