using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task PrepareRebindLibrary(string directory, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "replacement.kicad_sym"), """
            (kicad_symbol_lib (version 20250114) (generator kicad_symbol_editor)
              (symbol "ReplacedProbe" (pin_names (offset 0) hide) (in_bom yes) (on_board yes)
                (property "Reference" "TP" (at 0 3 0) (effects (font (size 1.27 1.27))))
                (property "Value" "ReplacedProbe" (at 0 5 0) (effects (font (size 1.27 1.27))))
                (symbol "ReplacedProbe_0_1" (circle (center 0 2.54) (radius 0.75)
                  (stroke (width 0) (type default)) (fill (type none))))
                (symbol "ReplacedProbe_1_1" (pin passive line (at 0 0 90) (length 2.54)
                  (name "1" (effects (font (size 1.27 1.27))))
                  (number "7" (effects (font (size 1.27 1.27))))))))
            """, token);
        await File.WriteAllTextAsync(Path.Combine(directory, "sym-lib-table"), """
            (sym_lib_table (version 7)
              (lib (name "RebindFixture") (type "KiCad") (uri "${KIPRJMOD}/replacement.kicad_sym") (options "") (descr "")))
            """, token);
    }

    private static async Task ChangeSymbolThroughDialog(NativeClient client, DocumentSpecifier document, Guid nativeId,
        string display, int processId, string evidence, CancellationToken token)
    {
        const string dialog = "Change Symbols";
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document.Clone() }, token);
        var header = new ItemHeader { Document = document.Clone() };
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header.Clone() }, token);
        var select = new AddToSelection { Header = header.Clone() }; select.Items.Add(new KIID { Value = nativeId.ToString("D") });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        var before = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        async Task Open()
        {
            NativeKeyboard.SchematicShortcut(display, processId, "e", controlKey: false, focusCanvas: false, altKey: true);
            NativeKeyboard.SchematicShortcut(display, processId, "End", controlKey: false, focusCanvas: false);
            NativeKeyboard.SchematicShortcut(display, processId, "Up", controlKey: false, focusCanvas: false);
            NativeKeyboard.SchematicShortcut(display, processId, "Up", controlKey: false, focusCanvas: false);
            NativeKeyboard.SchematicShortcut(display, processId, "Return", controlKey: false, focusCanvas: false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
            while (!NativeKeyboard.HasWindow(display, processId, dialog)) await Task.Delay(50, limit.Token);
            await NativeSetupUi.StableGeometry(display, processId, token, dialog);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "change-symbol-open.png"), token);
        }
        try
        {
            await Open();
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", dialog, false, false);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                while (NativeKeyboard.HasWindow(display, processId, dialog)) await Task.Delay(50, limit.Token);
            }
            var cancelled = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
            Assert.AreEqual(before.State.StateSha256, cancelled.State.StateSha256, "Cancelling Change Symbols must preserve the design.");
            await Open();
            (int X, int Y, int Width, int Height) geometry = default;
            NativeKeyboard.SchematicShortcut(display, processId, "", dialog, false, false, observeGeometry: g => geometry = g);
            await File.WriteAllTextAsync(Path.Combine(evidence, "change-symbol-geometry.json"),
                JsonSerializer.Serialize(new { geometry.X, geometry.Y, geometry.Width, geometry.Height }), token);
            // These coordinates are bound to the retained 1280x900 dialog captures.
            // Explicitly click the field; focusCanvas=false alone only focuses the window.
            NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
                clickFromLeft: 430, clickFromBottom: 565);
            NativeKeyboard.SchematicShortcut(display, processId, "a", dialog, true, false);
            foreach (char c in "RebindFixture:ReplacedProbe")
                NativeKeyboard.SchematicShortcut(display, processId, c.ToString(), dialog, false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "Tab", dialog, false, false);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "change-symbol-filled.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
                clickFromRight: 50, clickFromBottom: 22);
            await Task.Delay(250, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "change-symbol-result.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", dialog, false, false);
            using var close = CancellationTokenSource.CreateLinkedTokenSource(token); close.CancelAfter(TimeSpan.FromSeconds(10));
            while (NativeKeyboard.HasWindow(display, processId, dialog)) await Task.Delay(50, close.Token);
            var after = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
                new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
            var changed = after.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document))
                .Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Single(s => s.Id.Value == nativeId.ToString("D"));
            Assert.AreEqual("RebindFixture:ReplacedProbe", SchematicNativeAdditionProjection.LibraryKey(changed));
            Assert.AreEqual("7", changed.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                .Select(c => c.Item.Unpack<SchematicPin>()).Single(p => p.LibraryPinId is not null).Number);
        }
        catch
        {
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "change-symbol-failed.png"), CancellationToken.None);
            throw;
        }
    }

    private static async Task VerifyNativeComponentRebinding(NativeClient client, DocumentSpecifier root, int processId,
        string display, string evidence, string instanceId, bool shared, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(evidence, instanceId)).FullName;
        string PathOf(string name) => Path.Combine(directory, name);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token);
        var initial = await Capture();
        var baseline = RepeatedProbeDesign(initial.Electrical, includeUndrawn: false);
        var targetSheet = shared ? baseline.SheetBindings.First(s => s.NativePath.Count == 2) : baseline.SheetBindings.Single(s => s.NativePath.Count == 1);
        var targetDocument = root.Clone(); targetDocument.SheetPath.Path.Clear();
        targetDocument.SheetPath.Path.Add(targetSheet.NativePath.Select(p => new KIID { Value = p.ToString("D") }));
        var component = baseline.Engineering.Circuit.Components.First(c => c.SheetInstanceId == targetSheet.SheetInstanceId);
        var other = baseline.Engineering.Circuit.Components.Single(c => c.SheetInstanceId == targetSheet.SheetInstanceId && c.Id != component.Id);
        var occurrence = baseline.Engineering.Circuit.Symbols.Single(s => s.ComponentId == component.Id);
        var binding = baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence.Id);
        var note = new EngineeringStatement(Guid.NewGuid(), component.Id, EngineeringStatementRole.Intent,
            GuidanceStrength.Requirement, "Keep this component instruction through replacement.", null, [], []);
        var pinNote = note with { Id = Guid.NewGuid(), Text = "Retain this exact pin connection requirement.",
            Connection = new(new(component.Id, "1"), new(other.Id, "1")) };
        baseline = baseline with { Engineering = baseline.Engineering with { Structure = baseline.Engineering.Structure with
            { Statements = [note, pinNote] } } };
        string designPath = PathOf("design.xml");
        byte[] xml = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, []));
        await File.WriteAllBytesAsync(designPath, xml, token);
        var store = new DesignRecoveryStore(PathOf("recovery.json"));
        store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(initial.State.Revision.Epoch, initial.State.Revision.Sequence),
            initial.Electrical.Hierarchy.TrackingComplete, baseline, xml, baseline.Schematic.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);
        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            PathOf("mcp-state"), PathOf("mcp.log"), token);
        int step = 0;
        async Task<JsonElement> Call(string name, object arguments)
        {
            var result = await host.Tool(name, arguments);
            await File.WriteAllTextAsync(PathOf($"{++step:D2}-{name}.json"), RetainedToolEvidence(result), token);
            return result;
        }
        object Recovery() => new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = store.Read()!.RevisionToken };
        RequireToolSuccess(await Call("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var screen = initial.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(targetDocument));
        var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => s.Id.Value == binding.NativeObjectId.ToString("D")).Clone();
        string oldKey = string.IsNullOrEmpty(symbol.LibName) ? SchematicNativeAdditionProjection.LibraryKey(symbol) : symbol.LibName;
        var cache = screen.CachedSymbols.Single(c => c.CacheKey == oldKey).Clone();
        var replacementLibrary = symbol.LibraryId.Clone(); replacementLibrary.EntryName += "Replaced";
        symbol.LibraryId = replacementLibrary.Clone(); symbol.Definition.Id = replacementLibrary.Clone(); symbol.LibName = "";
        cache.CacheKey = replacementLibrary.LibraryNickname + ":" + replacementLibrary.EntryName;
        cache.Definition.Id = replacementLibrary.Clone();
        foreach (var definition in new[] { symbol.Definition, cache.Definition })
            foreach (var child in definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>(); Assert.AreEqual("1", pin.Number); pin.Number = "7";
                child.Item = Any.Pack(pin);
            }
        var replace = new CheckedSchematicBatch { ExpectedState = initial.State.Clone(), Batch = new()
        { Document = root.Clone(), DocumentEpoch = initial.State.Revision.Epoch, ExpectedRevision = initial.State.Revision.Clone(),
            OperationId = Guid.NewGuid().ToString("D"), Description = "Replace the bound probe definition and pin" } };
        replace.Batch.Operations.Add(new SchematicItemOperation { TargetDocument = targetDocument.Clone(), Update = Any.Pack(symbol) });
        replace.Batch.Operations.Add(new SchematicItemOperation { TargetDocument = targetDocument.Clone(), ReplaceLibraryCache = new()
        { ScreenId = screen.Metadata.ScreenId.Clone(), Definitions = { screen.CachedSymbols.Select(c => c.Clone()).Append(cache) } } });
        if (!shared)
            await ChangeSymbolThroughDialog(client, targetDocument, binding.NativeObjectId, display, processId, directory, token);
        else
        {
            var result = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(replace, token);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, result.Status, result.ErrorMessage);
        }
        var replaced = await Capture();
        RequireToolSuccess(await Call("kicad_design_recovery_refresh", Recovery()));
        var unanswered = await Call("kicad_design_sync_plan", Recovery());
        var unansweredData = unanswered.GetProperty("structuredContent");
        Assert.IsFalse(unansweredData.GetProperty("canPrepare").GetBoolean());
        Assert.AreEqual(SchematicNativeAdditionProjection.ResolutionRequired, unansweredData.GetProperty("errorCode").GetString());
        var requests = SchematicNetReconciliation.Plan(store.Read()!.State, token).ResolutionRequests!;
        Assert.HasCount(shared ? 2 : 1, requests);
        var request = requests.Single(r => r.FormerComponentId == component.Id);
        Assert.AreEqual(component.Id, request.FormerComponentId);
        Assert.AreEqual(binding.NativeObjectId, request.NativeObjectId);
        var answers = requests.Select(r => new SchematicOwnershipAnswer(r.NativeObjectId, r.CandidatePartIds.Single(), r.FormerComponentId)
        { NativePath = r.NativePath.Split('/').Select(Guid.Parse).ToArray(), PinMappings = [new("1", "7")] }).ToArray();
        object Answer(SchematicOwnershipAnswer[] values) => new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, designPath, answers = values };
        string unchanged = store.Read()!.RevisionToken;
        var refused = await Call("kicad_design_ownership_answer", Answer(answers.Select(a => a with { PinMappings = Array.Empty<SchematicPinRebindAnswer>() }).ToArray()));
        Assert.IsTrue(refused.GetProperty("isError").GetBoolean()); Assert.AreEqual(unchanged, store.Read()!.RevisionToken);
        if (shared)
        {
            var partial = await Call("kicad_design_ownership_answer", Answer(answers.Take(1).ToArray()));
            Assert.IsTrue(partial.GetProperty("isError").GetBoolean()); Assert.AreEqual(unchanged, store.Read()!.RevisionToken);
        }
        var selected = await Call("kicad_design_ownership_answer", Answer(answers)); RequireToolSuccess(selected);
        Assert.IsFalse(selected.GetProperty("structuredContent").GetProperty("designFileWritten").GetBoolean());
        CollectionAssert.AreEqual(xml, await File.ReadAllBytesAsync(designPath, token));
        Assert.AreEqual(replaced.State.StateSha256, (await Capture()).State.StateSha256);
        async Task<SchematicDesign> Publish(string phase)
        {
            RequireToolSuccess(await Call("kicad_design_recovery_refresh", Recovery()));
            var plan = await Call("kicad_design_sync_plan", Recovery()); RequireToolSuccess(plan);
            var applied = await Call("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath,
                expectedRevisionToken = store.Read()!.RevisionToken, designPath, operationId = Guid.NewGuid().ToString("D") });
            RequireToolSuccess(applied);
            Assert.IsFalse(applied.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean(), phase);
            var published = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            var actual = await Capture(); var match = SchematicElectricalComparison.Compare(published, actual.Electrical, [], token);
            Assert.IsTrue(match.PinBindingsComplete && match.ConnectivityEquivalent, phase);
            Assert.AreEqual(component, published.Engineering.Circuit.Components.Single(c => c.Id == component.Id));
            Assert.AreEqual(binding, published.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence.Id));
            foreach (var owner in baseline.Engineering.Circuit.Components)
                Assert.AreEqual(owner, published.Engineering.Circuit.Components.Single(c => c.Id == owner.Id));
            Assert.AreEqual(note, published.Engineering.Structure.Statements.Single(s => s.Id == note.Id));
            return published;
        }
        var appliedDesign = await Publish("replacement");
        Assert.AreEqual("7", appliedDesign.Engineering.Structure.Statements.Single(s => s.Id == pinNote.Id).Connection!.First.Pin);
        Assert.IsNull(store.Read()!.State.ComponentRebindResolution);
        foreach (var (key, pin) in new[] { ("z", "1"), ("y", "7") })
        {
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
            await FocusedSchematicShortcut(client, root, processId, display, key, token);
            var restored = await Publish(key);
            Assert.AreEqual(pin, restored.Engineering.Structure.Statements.Single(s => s.Id == pinNote.Id).Connection!.First.Pin);
        }
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token);
        var loaded = await Capture();
        RequireToolSuccess(await Call("kicad_design_recovery_reattach", new { instanceId, recoveryPath = store.StatePath,
            expectedRevisionToken = store.Read()!.RevisionToken, expectedDocumentEpoch = loaded.State.Revision.Epoch }));
        await Publish("reload");
        byte[] beforeRepeat = await File.ReadAllBytesAsync(designPath, token); await Publish("repeat");
        CollectionAssert.AreEqual(beforeRepeat, await File.ReadAllBytesAsync(designPath, token));
        await File.WriteAllTextAsync(PathOf("rebind-proof.json"), JsonSerializer.Serialize(new { sharedInstances = shared ? 2 : 1,
            manualChangeSymbolApplied = !shared, changeSymbolCancellationPreserved = !shared, nativeReplacement = true,
            explicitPinMapping = true, noMutationWhenAnswering = true, unchangedComponentAndOccurrence = true,
            instructionsPreserved = true, actualConnectivity = true, nativeUndoRedo = true, reload = true, repeatNoOp = true }), token);
    }
}
