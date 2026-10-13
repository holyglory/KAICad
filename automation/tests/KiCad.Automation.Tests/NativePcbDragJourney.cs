using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<DocumentSpecifier> VerifyNativePcbInlineDrag(NativeClient client,
        DocumentSpecifier board, StdioMcpFixture mcp, string evidence, string instanceId,
        int processId, string display, CancellationToken token)
    {
        string boardPath = Path.Combine(board.Project.Path, board.BoardFilename);
        var beforeCreate = await ObserveLifecycleState(client, board, token);

        // Reuse a loaded board footprint as the source definition.  The two
        // test pads are board-local copies with fresh identities, so this
        // journey does not depend on an installed footprint library.
        var footprints = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbFootprint }
        }, token);
        var template = footprints.Items.Select(item => item.Unpack<FootprintInstance>())
            .FirstOrDefault(item => item.Definition.Items.Where(child => child.Is(Pad.Descriptor))
                .Select(child => child.Unpack<Pad>())
                .Any(pad => pad.PadStack.Layers.Contains(BoardLayer.BlFCu)))
            ?? throw new AssertFailedException("The inline-drag board has no footprint pad template.");
        var templatePad = template.Definition.Items.Where(item => item.Is(Pad.Descriptor))
            .Select(item => item.Unpack<Pad>())
            .Single(pad => pad.PadStack.Layers.Contains(BoardLayer.BlFCu));
        // Keep the seeded route at the board's visual centre so the existing
        // fixture canvas focus point lands on copper after fit-to-board.
        const long padAX = 35_000_000, padBX = 65_000_000, padY = 50_000_000;
        FootprintInstance padA = CloneInlineDragFootprint(template, templatePad, "GUI_DRAG_A", padAX, padY);
        FootprintInstance padB = CloneInlineDragFootprint(template, templatePad, "GUI_DRAG_B", padBX, padY);
        string trackId = Guid.NewGuid().ToString("D");
        var seededTrack = new Track
        {
            Id = new() { Value = trackId },
            Start = new() { XNm = padAX, YNm = padY },
            End = new() { XNm = padBX, YNm = padY },
            Width = new() { ValueNm = 250_000 },
            Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER_RAIL" }
        };
        var seed = new CreateItems { Header = new() { Document = board } };
        seed.Items.Add(Any.Pack(padA));
        seed.Items.Add(Any.Pack(padB));
        seed.Items.Add(Any.Pack(seededTrack));
        var seedReply = await mcp.Tool("kicad_pcb_items_create", new
        {
            instanceId,
            requestJson = BoardJson.Formatter.Format(seed),
            expectedStateJson = SchematicJson.Formatter.Format(beforeCreate),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(seedReply);

        var seededRows = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbPad }
        }, token);
        var dragPads = seededRows.Items.Select(item => item.Unpack<Pad>())
            .Where(pad => pad.Net.Name == "POWER_RAIL"
                && ((pad.Position.XNm == padAX && pad.Position.YNm == padY)
                    || (pad.Position.XNm == padBX && pad.Position.YNm == padY)))
            .OrderBy(pad => pad.Position.XNm).ToArray();
        Assert.HasCount(2, dragPads, "The inline-drag seed must create both board-local pads.");
        var seededRoute = (await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbTrace }
        }, token)).Items.Select(item => item.Unpack<Track>())
            .Single(track => track.Id.Value == trackId);
        var seededPadIds = dragPads.Select(pad => pad.Id.Clone()).ToArray();

        async Task<GetItemsResponse> Connected(KIID item)
            => await client.InvokeAsync<GetConnectedItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { item } }, token);

        string ConnectedIdentity(Any item) => item.Is(Pad.Descriptor)
            ? item.Unpack<Pad>().Id.Value : RouteIdentity(item);

        string[] beforeConnected = (await Connected(seededPadIds[0])).Items
            .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value, trackId }, beforeConnected,
            "The seeded route must connect both pads before the GUI drag.");

        try
        {
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = new() { Document = board } }, token);
            var selection = new AddToSelection { Header = new() { Document = board } };
            selection.Items.Add(seededRoute.Id.Clone());
            var selected = await client.InvokeAsync<AddToSelection, SelectionResponse>(selection, token);
            Assert.IsTrue(selected.Items.Any(item => item.Is(Track.Descriptor)
                && item.Unpack<Track>().Id.Value == trackId),
                "The native editor must select the exact seeded track before G.");

            // Refit the live PCB canvas before deriving the pointer location.
            // Query the actual window dimensions rather than assuming the
            // fixture's current Xvfb size; the calibrated target below keeps
            // the pointer tied to the rendered native window.
            (int Width, int Height) window = default;
            NativeKeyboard.SchematicShortcut(display, processId, "", "PCB Editor",
                controlKey: false, focusCanvas: false,
                observeGeometry: geometry => window = (geometry.Width, geometry.Height));
            Assert.IsGreaterThan(0, window.Width, "The PCB editor window must expose a positive width.");
            Assert.IsGreaterThan(0, window.Height, "The PCB editor window must expose a positive height.");
            NativeKeyboard.SchematicShortcut(display, processId, "Home", "PCB Editor",
                controlKey: false, focusCanvas: false);
            // The fitted PCB view keeps the selected route inside the board
            // canvas, but the side panels and toolbar shift its board-space
            // midpoint away from the top-level window center.  The fixture's
            // rendered calibration is (671,383) at 1280x720; scale that
            // measured target with the live window so G is sent over the
            // actual seeded segment rather than an empty canvas pixel.
            int centerX = (int)Math.Round(window.Width * 671d / 1280d);
            int centerY = (int)Math.Round(window.Height * 383d / 720d);
            string preDragCapture = Path.Combine(evidence, instanceId + "-pcb-inline-drag-pre-g.png");
            string? captureFailure = null;
            try { await NativeKeyboard.CaptureAsync(display, preDragCapture, token); }
            catch (InvalidOperationException error)
            {
                // This screenshot diagnoses pointer calibration; the actual
                // acceptance proof below is native geometry, connectivity,
                // history and persistence. Retain an unavailable capture
                // without masking that independent functional proof.
                captureFailure = error.Message;
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-inline-drag-capture-failure.txt"),
                    captureFailure, token);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-inline-drag-pre-g.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    fitShortcut = "Home",
                    window = new { width = window.Width, height = window.Height },
                    pointer = new { x = centerX, y = centerY },
                    route = new
                    {
                        start = new { xNm = seededRoute.Start.XNm, yNm = seededRoute.Start.YNm },
                        end = new { xNm = seededRoute.End.XNm, yNm = seededRoute.End.YNm },
                        midpoint = new
                        {
                            xNm = (seededRoute.Start.XNm + seededRoute.End.XNm) / 2,
                            yNm = (seededRoute.Start.YNm + seededRoute.End.YNm) / 2
                        },
                        layer = seededRoute.Layer.ToString(), widthNm = seededRoute.Width.ValueNm
                    },
                    capture = File.Exists(preDragCapture) ? Path.GetFileName(preDragCapture) : null,
                    captureFailure
                }), token);
            // Focus the actual canvas on an empty point first. Reapply the
            // native selection afterward because GTK background clicks clear
            // the editor selection; G then sees both a focused canvas and the
            // exact selected route under the pointer.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor",
                controlKey: false, focusCanvas: true, clickFromLeft: 550, clickFromTop: 300);
            var refocusedSelection = await client.InvokeAsync<AddToSelection, SelectionResponse>(selection, token);
            Assert.IsTrue(refocusedSelection.Items.Any(item => item.Is(Track.Descriptor)
                && item.Unpack<Track>().Id.Value == trackId),
                "The native editor must retain the exact seeded track after canvas focus.");
            NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor",
                controlKey: false, focusCanvas: false, clickFromLeft: centerX, clickFromTop: centerY);
            NativeKeyboard.SchematicShortcut(display, processId, "G", "PCB Editor",
                controlKey: false, focusCanvas: false);

            // The read-only board API remains callable while the GUI router is
            // active, so AS_BUSY is not an admission signal. The real proof is
            // the subsequent motion/click changing this exact native item.
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"),
                "The PCB editor must remain alive after entering inline drag.");
            foreach ((int x, int y) in new[] { (centerX + 8, centerY + 2), (centerX + 16, centerY + 6) })
            {
                NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor",
                    controlKey: false, focusCanvas: false, clickFromLeft: x, clickFromTop: y);
                await Task.Delay(100, token);
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor",
                controlKey: false, focusCanvas: false, clickFromLeft: centerX + 16, clickFromTop: centerY + 6);

            Track changedTrack;
            using (var committed = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                committed.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var rows = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                    { Header = new() { Document = board }, Items = { seededRoute.Id } }, committed.Token);
                    if (rows.Items.Count == 1 && rows.Items[0].Is(Track.Descriptor))
                    {
                        changedTrack = rows.Items[0].Unpack<Track>();
                        if (!RouteShape(Any.Pack(seededRoute)).Equals(RouteShape(Any.Pack(changedTrack)), StringComparison.Ordinal))
                            break;
                    }
                    await Task.Delay(75, committed.Token);
                }
            }
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"),
                "The PCB editor must remain alive after committing the GUI drag.");
            string[] changedConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value, trackId }, changedConnected,
                "The committed inline drag must preserve both pad connections.");

            async Task<Track> ReadRoute(CancellationToken local)
            {
                var rows = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
                { Header = new() { Document = board }, Items = { seededRoute.Id } }, local);
                Assert.AreEqual(1, rows.Items.Count, "The original track identity must survive inline drag history.");
                Assert.IsTrue(rows.Items[0].Is(Track.Descriptor));
                return rows.Items[0].Unpack<Track>();
            }

            NativeKeyboard.SchematicShortcut(display, processId, "z", "PCB Editor",
                controlKey: true, focusCanvas: false);
            using (var undo = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                undo.CancelAfter(TimeSpan.FromSeconds(10));
                Track restored;
                do
                {
                    restored = await ReadRoute(undo.Token);
                    if (RouteShape(Any.Pack(restored)).Equals(RouteShape(Any.Pack(seededRoute)), StringComparison.Ordinal))
                        break;
                    await Task.Delay(75, undo.Token);
                } while (true);
                Assert.AreEqual(RouteShape(Any.Pack(seededRoute)), RouteShape(Any.Pack(restored)),
                    "Undo must restore the exact pre-drag geometry.");
            }
            string[] undoConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value, trackId }, undoConnected,
                "Undo must restore both pad connections.");

            NativeKeyboard.SchematicShortcut(display, processId, "y", "PCB Editor",
                controlKey: true, focusCanvas: false);
            using (var redo = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                redo.CancelAfter(TimeSpan.FromSeconds(10));
                Track restored;
                do
                {
                    restored = await ReadRoute(redo.Token);
                    if (RouteShape(Any.Pack(restored)).Equals(RouteShape(Any.Pack(changedTrack)), StringComparison.Ordinal))
                        break;
                    await Task.Delay(75, redo.Token);
                } while (true);
                Assert.AreEqual(RouteShape(Any.Pack(changedTrack)), RouteShape(Any.Pack(restored)),
                    "Redo must restore the exact committed geometry.");
            }
            string[] redoConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value, trackId }, redoConnected,
                "Redo must restore both pad connections.");

            var redoneState = await ObserveLifecycleState(client, board, token);
            var save = await mcp.Tool("kicad_document_save", new
            {
                instanceId,
                expectedStateJson = SchematicJson.Formatter.Format(redoneState),
                operationId = Guid.NewGuid().ToString("D")
            });
            RequireToolSuccess(save);
            var close = await mcp.Tool("kicad_document_close", new
            {
                instanceId,
                expectedStateJson = save.GetProperty("structuredContent").GetProperty("observedState").GetRawText(),
                operationId = Guid.NewGuid().ToString("D")
            });
            RequireToolSuccess(close);
            board = (await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                toolName: "kicad_pcb_open")).Document;

            var persistedRows = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { seededRoute.Id } }, token);
            Assert.AreEqual(1, persistedRows.Items.Count, "The dragged track must persist with its original identity.");
            Assert.IsTrue(persistedRows.Items[0].Is(Track.Descriptor));
            AssertPersistedRouteState(Any.Pack(changedTrack), persistedRows.Items[0]);
            var persistedConnected = await client.InvokeAsync<GetConnectedItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { seededPadIds[0] } }, token);
            string[] persistedConnectedIds = persistedConnected.Items.Select(ConnectedIdentity)
                .Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value, trackId }, persistedConnectedIds,
                "Save/reopen must preserve both pad connections after the GUI drag.");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-inline-drag.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    trackId, padIds = seededPadIds.Select(id => id.Value).ToArray(),
                    before = DescribeRouteItem(Any.Pack(seededRoute)),
                    after = DescribeRouteItem(Any.Pack(changedTrack)),
                    beforeConnected, changedConnected, undoConnected, redoConnected, persistedConnectedIds,
                    guiRouter = true, undoRedo = true, saveReopen = true
                }), token);
            return board;
        }
        finally
        {
            try { NativeKeyboard.SchematicShortcut(display, processId, "Escape", "PCB Editor", false, false); }
            catch (Exception) { }
            try { await client.InvokeAsync<ClearSelection, Empty>(new() { Header = new() { Document = board } }, CancellationToken.None); }
            catch (Exception) { }
        }
    }

    private static FootprintInstance CloneInlineDragFootprint(FootprintInstance template, Pad templatePad,
        string reference, long xNm, long yNm)
    {
        var footprint = template.Clone();
        footprint.Id = new() { Value = Guid.NewGuid().ToString("D") };
        footprint.Parent = null;
        footprint.CustomProperties.Clear();
        footprint.SymbolPath = null;
        footprint.Position = new() { XNm = xNm, YNm = yNm };
        footprint.Definition.Id = template.Definition.Id.Clone();
        footprint.Definition.Items.Clear();
        var pad = templatePad.Clone();
        pad.Id = new() { Value = Guid.NewGuid().ToString("D") };
        pad.Parent = null;
        pad.CustomProperties.Clear();
        pad.SymbolPin = null;
        pad.Net = new() { Name = "POWER_RAIL" };
        pad.Position = footprint.Position.Clone();
        footprint.Definition.Items.Add(Any.Pack(pad));
        foreach (var field in new[] { footprint.ReferenceField, footprint.ValueField, footprint.DatasheetField,
            footprint.DescriptionField, footprint.Definition.ReferenceField, footprint.Definition.ValueField,
            footprint.Definition.DatasheetField, footprint.Definition.DescriptionField })
        {
            if (field?.Text is null) continue;
            field.CustomProperties.Clear();
            field.Text.CustomProperties.Clear();
            field.Text.Parent = null;
            field.Text.Id = new() { Value = Guid.NewGuid().ToString("D") };
            field.Text.Text.Position = footprint.Position.Clone();
        }
        footprint.ReferenceField.Text.Text.Text_ = reference;
        footprint.ReferenceField.Text.Text.Position = new() { XNm = xNm, YNm = yNm - 2_000_000 };
        footprint.ValueField.Text.Text.Position = new() { XNm = xNm, YNm = yNm + 2_000_000 };
        return footprint;
    }
}
