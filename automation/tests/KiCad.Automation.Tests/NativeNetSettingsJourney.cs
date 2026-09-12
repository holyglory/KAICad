using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        string Prefix(string phase) => Path.Combine(evidence, processId + "-net-settings-" + phase);
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicNetSettings settings)
        {
            var value = settings.Clone(); value.LabelAssignments.Clear();
            var batch = new ApplySchematicItemBatch { Document = document, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                Description = "Project net settings XML round trip" };
            batch.Operations.Add(new SchematicItemOperation { SetNetSettings = value });
            return batch;
        }
        async Task Same(SchematicScreenData expected, SchematicScreenData actual, string phase)
        {
            if (!expected.Equals(actual))
            {
                await File.WriteAllTextAsync(Prefix(phase + "-expected.xml"), SchematicDataXml.Write(expected), token);
                await File.WriteAllTextAsync(Prefix(phase + "-actual.xml"), SchematicDataXml.Write(actual), token);
                Assert.Fail(NativeSnapshotDifference.Describe(expected, actual));
            }
            Assert.AreEqual(expected, SchematicDataXml.Read(SchematicDataXml.Write(actual)));
        }
        async Task Capture(string phase)
        {
            await NativeKeyboard.CaptureAsync(display, Prefix(phase + ".png"), token);
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(observation.Preview.Revision, observation.Snapshot.Revision);
            await File.WriteAllTextAsync(Prefix(phase + ".xml"), SchematicDataXml.Write(observation.Snapshot.Data), token);
        }
        async Task Reopen(SchematicScreenData expected, string phase)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
            await Same(expected, (await Read()).Data, phase);
        }
        async Task UndoRedo(SchematicScreenData original, SchematicScreenData changed)
        {
            foreach (var (key, expected) in new[] { ("z", original), ("y", changed) })
            {
                var before = await Read();
                await FocusedSchematicShortcut(client, document, processId, display, key, token);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                int delay = 25;
                SchematicScreenDataSnapshot after;
                do
                {
                    after = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                        new() { Document = document }, limit.Token);
                    if (after.Revision.Equals(before.Revision))
                    { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
                } while (after.Revision.Equals(before.Revision));
                await Same(expected, after.Data, "history-" + key);
            }
        }

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var original = await Read();
        Assert.IsNotNull(original.Data.Metadata.NetSettings);
        var desired = original.Data.Clone(); var settings = desired.Metadata.NetSettings;
        settings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 50800 };
        settings.DefaultClass.Board.TrackWidth = new() { ValueNm = 450000 };
        var power = settings.DefaultClass.Clone(); power.Name = "AutomationPower"; power.Priority = 2;
        power.Board.Clearance = null; power.Board.TuningProfile = "power";
        power.Schematic.Color = new() { R = 1, A = 1 };
        settings.Classes.Add(power);
        settings.Patterns.Add(new SchematicNetClassPattern { Pattern = "POWER*", NetClass = power.Name });
        settings.NetColors.Add("/POWER", new() { R = 1, A = 1 });
        settings.ChainNetclasses.Add("unassigned-power-chain", power.Name);
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(original, desired.Metadata.NetSettings);
        foreach (int kind in Enumerable.Range(0, 6))
        {
            var invalid = batch.Clone(); invalid.OperationId = Guid.NewGuid().ToString("D");
            var value = invalid.Operations[0].SetNetSettings;
            if (kind == 0) value.DefaultClass = null;
            if (kind == 1) value.Classes.Add(value.Classes[0].Clone());
            if (kind == 2) value.DefaultClass.Schematic.LineStyle = (StrokeLineStyle)99;
            if (kind == 3) value.NetColors["/POWER"].R = double.NaN;
            if (kind == 4) value.LabelAssignments.Add("foreign-label", new() { Names = { power.Name } });
            if (kind == 5) invalid.Operations[0].SetNetSettings = SchematicNetSettings.Parser.ParseFrom(
                value.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
            Assert.AreEqual(original, await Read(), "Rejected settings must preserve the native revision and all owners.");
        }
        var failed = batch.Clone(); failed.OperationId = Guid.NewGuid().ToString("D");
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(original, await Read(), "A failed later operation must roll back project settings.");
        var result = await Apply(batch); Assert.IsTrue(result.NetSettingsChanged);
        var applied = await Read(); Assert.AreEqual(original.Revision.Sequence + 1, applied.Revision.Sequence);
        await Same(desired, applied.Data, "applied");
        Assert.AreEqual(result, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(applied, await Read());
        Assert.IsFalse((await Apply(Batch(applied, desired.Metadata.NetSettings))).NetSettingsChanged);
        Assert.AreEqual(applied, await Read());
        await Capture("api-applied"); await UndoRedo(original.Data, desired);
        await Reopen(desired, "reopened");

        var twiceExpected = desired.Clone();
        twiceExpected.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 76200 };
        var twice = Batch(await Read(), original.Data.Metadata.NetSettings);
        twice.Operations.Add(new SchematicItemOperation { SetNetSettings = twiceExpected.Metadata.NetSettings.Clone() });
        twice.Operations[^1].SetNetSettings.LabelAssignments.Clear();
        await Apply(twice); await Same(twiceExpected, (await Read()).Data, "two-operations");
        await UndoRedo(desired, twiceExpected);
        await Apply(Batch(await Read(), original.Data.Metadata.NetSettings));
        await Reopen(original.Data, "restored");

        foreach (bool accept in new[] { false, true })
        {
            var before = await Read(); var expected = before.Data.Clone();
            expected.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 50800 };
            await NativeSetupUi.Open(client, document, display, processId, token);
            await NativeSetupUi.SelectPage(display, processId, 223, token);
            await NativeKeyboard.CaptureAsync(display, Prefix($"manual-before-{accept}.png"), token);
            // The schematic panel shows name, wire width, bus width, color and
            // line style. This targets the Default row's wire-width cell.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 580, clickFromTop: 65);
            NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true, false);
            foreach (char character in "2 mil")
                NativeKeyboard.SchematicShortcut(display, processId, character.ToString(), "Schematic Setup", false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "Tab", "Schematic Setup", false, false);
            await NativeKeyboard.CaptureAsync(display, Prefix($"manual-edited-{accept}.png"), token);
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            var observed = await Read();
            Assert.AreEqual(before.Revision.Sequence + (accept ? 1UL : 0UL), observed.Revision.Sequence);
            await Same(accept ? expected : before.Data, observed.Data, "manual-" + accept);
            if (!accept) continue;
            await UndoRedo(before.Data, expected); await Reopen(expected, "manual-reopened");
        }
        await Apply(Batch(await Read(), original.Data.Metadata.NetSettings));
        await Reopen(original.Data, "final-restored");
    }
}
