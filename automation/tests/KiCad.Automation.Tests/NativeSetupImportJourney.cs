using System.Text.Json.Nodes;
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
    private static async Task VerifySetupImport(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Snapshot() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        string project = (await client.HandshakeAsync(token)).ProjectPath;
        byte[] originalFile = await File.ReadAllBytesAsync(project, token);
        var baseline = await Snapshot();
        var changed = baseline.Data.Clone();
        changed.Metadata.DrawingRatios.OverbarHeightRatio = 1.31;
        var format = changed.Metadata.Formatting;
        format.JunctionSizeChoice = 2;
        format.HopOverSizeChoice = 1;
        format.ShortReferenceFormat = !format.ShortReferenceFormat;
        format.OperatingPoint.VoltagePrecision = 4;
        format.OperatingPoint.CurrentPrecision = 5;
        changed.Metadata.TextVariables["SETUP_IMPORT_NOTE"] = "retained pending import";
        var pin = changed.Metadata.ErcSettings.PinMap.Single(x => (int)x.First == 1 && (int)x.Second == 1);
        pin.Conflict = (SchematicErcPinConflict)((int)pin.Conflict % 3 + 1);
        JsonNode imported = JsonNode.Parse(originalFile)!;
        var drawing = imported["schematic"]!["drawing"]!;
        drawing["overbar_offset_ratio"] = 1.31;
        drawing["junction_size_choice"] = 2;
        drawing["hop_over_size_choice"] = 1;
        drawing["intersheets_ref_short"] = format.ShortReferenceFormat;
        drawing["operating_point_overlay_v_precision"] = 4;
        drawing["operating_point_overlay_i_precision"] = 5;
        imported["text_variables"]!["SETUP_IMPORT_NOTE"] = "retained pending import";
        imported["erc"]!["pin_map"]![0]![0] = (int)pin.Conflict - 1;
        var schematic = imported["schematic"]!;
        schematic["reuse_designators"] = !(schematic["reuse_designators"]?.GetValue<bool>() ?? false);
        changed.Metadata.Annotation.ReuseDesignators = schematic["reuse_designators"]!.GetValue<bool>();
        schematic["annotate_start_num"] = 421;
        schematic["annotation"]!["sort_order"] = 1;
        schematic["annotation"]!["method"] = 1;
        changed.Metadata.Annotation.StartAfter = 421;
        changed.Metadata.Annotation.Order = SchematicAnnotationOrder.SaoYPosition;
        changed.Metadata.Annotation.Method = SchematicAnnotationMethod.SamSheetTimes100;
        schematic["compare_symbols"]!["missing_fields"] = !schematic["compare_symbols"]!["missing_fields"]!.GetValue<bool>();
        string scratch = Directory.CreateTempSubdirectory("setup-import-").FullName;
        string sourceProject = Path.Combine(scratch, "import.kicad_pro");
        await File.WriteAllTextAsync(sourceProject, imported.ToJsonString(), token);
        byte[] sourceFile = await File.ReadAllBytesAsync(sourceProject, token);
        try
        {
            foreach (bool accept in new[] { false, true })
            {
                await NativeSetupUi.Open(client, document, display, processId, token);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromLeft: 400, clickFromBottom: 25);
                await Window("Import Settings", true);
                await NativeSetupUi.StableGeometry(display, processId, token, "Import Settings");
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Import Settings", true,
                    clickFromLeft: 150, clickFromTop: 23);
                foreach (char c in sourceProject)
                    NativeKeyboard.SchematicShortcut(display, processId, c.ToString(), "Import Settings", false, false);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Import Settings", false,
                    clickFromLeft: 65, clickFromBottom: 25);
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + $"-setup-import-{accept}-selected.png"), token);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Import Settings", false,
                    clickFromRight: 65, clickFromBottom: 25);
                await Window("Import Settings", false);
                await NativeSetupUi.SelectPage(display, processId, 286, token);
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + $"-setup-import-{accept}-text-variables.png"), token);
                await NativeSetupUi.SelectPage(display, processId, 34, token);
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + $"-setup-import-{accept}-formatting.png"), token);
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
                await Window("Schematic Setup", false);
                var result = await Snapshot();
                await Same(accept ? changed : baseline.Data, result.Data, $"import-{accept}");
                Assert.AreEqual(baseline.Revision.Sequence + (accept ? 1UL : 0UL), result.Revision.Sequence);
                byte[] unchangedSource = await File.ReadAllBytesAsync(sourceProject, token);
                Assert.AreSequenceEqual(sourceFile, unchangedSource);
                if (!accept)
                {
                    byte[] cancelledProject = await File.ReadAllBytesAsync(project, token);
                    Assert.AreSequenceEqual(originalFile, cancelledProject);
                }
            }
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            var saved = JsonNode.Parse(await File.ReadAllBytesAsync(project, token))!;
            Assert.IsTrue(JsonNode.DeepEquals(imported["schematic"]!["reuse_designators"], saved["schematic"]!["reuse_designators"]),
                "Select All must include annotation preferences.");
            Assert.IsTrue(JsonNode.DeepEquals(imported["schematic"]!["compare_symbols"], saved["schematic"]!["compare_symbols"]),
                "Select All must include symbol comparison settings.");
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            await WaitFor(baseline.Data);
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
                clickFromLeft: 280, clickFromTop: 43);
            await WaitFor(changed);
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            await WaitFor(baseline.Data);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            var restored = JsonNode.Parse(await File.ReadAllBytesAsync(project, token))!;
            var original = JsonNode.Parse(originalFile)!;
            Assert.IsTrue(JsonNode.DeepEquals(original["schematic"], restored["schematic"]),
                "Native Undo must restore all imported schematic settings, including fields outside snapshot coverage.");
        }
        finally { Directory.Delete(scratch, true); }

        async Task Window(string name, bool present)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, name) != present)
            { await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500); }
        }
        async Task Same(SchematicScreenData expected, SchematicScreenData actual, string stage)
        {
            if (!expected.Equals(actual))
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-{stage}-expected.json"), expected.ToString(), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + $"-{stage}-actual.json"), actual.ToString(), token);
            }
            Assert.IsTrue(expected.Equals(actual), $"{stage}: exact native data differ; details retained in evidence.");
        }
        async Task WaitFor(SchematicScreenData expected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (true)
            {
                var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, wait.Token);
                if (state.Data.Equals(expected)) return;
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
            }
        }
    }
}
