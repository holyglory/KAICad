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
        const long padAX = 35_000_000, padBX = 65_000_000, padY = 50_000_000,
            cornerX = 50_000_000, cornerY = 52_000_000;
        FootprintInstance padA = CloneInlineDragFootprint(template, templatePad, "GUI_DRAG_A", padAX, padY);
        FootprintInstance padB = CloneInlineDragFootprint(template, templatePad, "GUI_DRAG_B", padBX, padY);
        string trackId = Guid.NewGuid().ToString("D");
        string tailTrackId = Guid.NewGuid().ToString("D");
        var seededTrack = new Track
        {
            Id = new() { Value = trackId },
            Start = new() { XNm = padAX, YNm = padY },
            End = new() { XNm = cornerX, YNm = cornerY },
            Width = new() { ValueNm = 250_000 },
            Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER_RAIL" }
        };
        var seededTail = new Track
        {
            Id = new() { Value = tailTrackId },
            Start = new() { XNm = cornerX, YNm = cornerY },
            End = new() { XNm = padBX, YNm = padY },
            Width = new() { ValueNm = 250_000 },
            Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER_RAIL" }
        };
        var seed = new CreateItems { Header = new() { Document = board } };
        seed.Items.Add(Any.Pack(padA));
        seed.Items.Add(Any.Pack(padB));
        seed.Items.Add(Any.Pack(seededTrack));
        seed.Items.Add(Any.Pack(seededTail));
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

        async Task<Track[]> PowerRoutes(CancellationToken local)
        {
            var rows = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            {
                Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbTrace }
            }, local);
            return rows.Items.Select(item => item.Unpack<Track>())
                .Where(track => track.Net?.Name == "POWER_RAIL")
                .ToArray();
        }
        static string RouteSetShape(IEnumerable<Track> routes) => string.Join("|",
            routes.Select(track => RouteShape(Any.Pack(track))).Order(StringComparer.Ordinal));
        Track[] seededRoutes = await PowerRoutes(token);
        Assert.IsGreaterThanOrEqualTo(2, seededRoutes.Length,
            "The seeded connected route must contain both segments before the GUI drag.");
        string seededRouteShape = RouteSetShape(seededRoutes);

        async Task<GetItemsResponse> Connected(KIID item)
            => await client.InvokeAsync<GetConnectedItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { item } }, token);

        string ConnectedIdentity(Any item) => item.Is(Pad.Descriptor)
            ? item.Unpack<Pad>().Id.Value : RouteIdentity(item);

        string[] beforeConnected = (await Connected(seededPadIds[0])).Items
            .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value }, beforeConnected,
            "The seeded route must connect both pads before the GUI drag.");
        Assert.IsTrue(beforeConnected.Any(id => seededRoutes.Any(route => route.Id.Value == id)),
            "The seeded pad must be connected to a native route segment before the GUI drag.");

        try
        {
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = new() { Document = board } }, token);

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
            int centerY = (int)Math.Round(window.Height * 392d / 720d);
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
            // Focus the actual canvas on an empty point first. InlineDrag's
            // native neighboring-segment filter then resolves the route from
            // the cursor itself, which is the same path used by the GUI.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor",
                controlKey: false, focusCanvas: true, clickFromLeft: 550, clickFromTop: 300);
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = new() { Document = board } }, token);
            NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor",
                controlKey: false, focusCanvas: false, clickFromLeft: centerX, clickFromTop: centerY);
            await Task.Delay(250, token);
            NativeKeyboard.SchematicShortcut(display, processId, "g", "PCB Editor",
                controlKey: false, focusCanvas: false);
            await Task.Delay(250, token);

            // The read-only board API remains callable while the GUI router is
            // active, so AS_BUSY is not an admission signal. The real proof is
            // the subsequent motion/click changing this exact native item.
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"),
                "The PCB editor must remain alive after entering inline drag.");
            foreach ((int x, int y) in new[] { (centerX + 8, centerY + 8), (centerX + 16, centerY + 16) })
            {
                NativeKeyboard.SchematicShortcut(display, processId, "motion", "PCB Editor",
                    controlKey: false, focusCanvas: false, clickFromLeft: x, clickFromTop: y);
                await Task.Delay(100, token);
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", "PCB Editor",
                controlKey: false, focusCanvas: false, clickFromLeft: centerX + 16, clickFromTop: centerY + 16);

            async Task<(Track[] Routes, string Shape)?> WaitForChanged(TimeSpan timeout)
            {
                using var committed = CancellationTokenSource.CreateLinkedTokenSource(token);
                committed.CancelAfter(timeout);
                try
                {
                    while (true)
                    {
                        Track[] current = await PowerRoutes(committed.Token);
                        string shape = RouteSetShape(current);
                        if (!string.Equals(seededRouteShape, shape, StringComparison.Ordinal))
                            return (current, shape);
                        await Task.Delay(75, committed.Token);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            }
            var changed = await WaitForChanged(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(changed, "A native PCB drag action must change the selected route geometry.");
            Track[] committedRoutes = changed!.Value.Routes;
            string committedRouteShape = changed.Value.Shape;
            Track committedTrack = committedRoutes.FirstOrDefault(route => route.Id.Value == trackId)
                ?? committedRoutes[0];
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"),
                "The PCB editor must remain alive after committing the GUI drag.");
            string[] changedConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value }, changedConnected,
                "The committed inline drag must preserve both pad connections.");
            Assert.IsTrue(changedConnected.Any(id => committedRoutes.Any(route => route.Id.Value == id)),
                "The committed pad connection must point to a native route segment.");

            NativeKeyboard.SchematicShortcut(display, processId, "z", "PCB Editor",
                controlKey: true, focusCanvas: false);
            using (var undo = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                undo.CancelAfter(TimeSpan.FromSeconds(10));
                Track[] restored;
                do
                {
                    restored = await PowerRoutes(undo.Token);
                    if (RouteSetShape(restored).Equals(seededRouteShape, StringComparison.Ordinal))
                        break;
                    await Task.Delay(75, undo.Token);
                } while (true);
                Assert.AreEqual(seededRouteShape, RouteSetShape(restored),
                    "Undo must restore the exact pre-drag geometry.");
            }
            string[] undoConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value }, undoConnected,
                "Undo must restore both pad connections.");

            NativeKeyboard.SchematicShortcut(display, processId, "y", "PCB Editor",
                controlKey: true, focusCanvas: false);
            using (var redo = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                redo.CancelAfter(TimeSpan.FromSeconds(10));
                Track[] restored;
                do
                {
                    restored = await PowerRoutes(redo.Token);
                    if (RouteSetShape(restored).Equals(committedRouteShape, StringComparison.Ordinal))
                        break;
                    await Task.Delay(75, redo.Token);
                } while (true);
                Assert.AreEqual(committedRouteShape, RouteSetShape(restored),
                    "Redo must restore the exact committed geometry.");
            }
            string[] redoConnected = (await Connected(seededPadIds[0])).Items
                .Select(ConnectedIdentity).Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value }, redoConnected,
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

            Track[] persistedRoutes = await PowerRoutes(token);
            Assert.AreEqual(committedRouteShape, RouteSetShape(persistedRoutes),
                "Save/reopen must preserve the complete committed route geometry.");
            var persistedConnected = await client.InvokeAsync<GetConnectedItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { seededPadIds[0] } }, token);
            string[] persistedConnectedIds = persistedConnected.Items.Select(ConnectedIdentity)
                .Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.IsSubsetOf(new[] { seededPadIds[0].Value, seededPadIds[1].Value }, persistedConnectedIds,
                "Save/reopen must preserve both pad connections after the GUI drag.");
            Assert.IsTrue(persistedConnectedIds.Any(id => persistedRoutes.Any(route => route.Id.Value == id)),
                "Save/reopen must preserve a native route connection for both pads.");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-inline-drag.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    trackId, tailTrackId, padIds = seededPadIds.Select(id => id.Value).ToArray(),
                    before = DescribeRouteItem(Any.Pack(seededRoute)),
                    after = DescribeRouteItem(Any.Pack(committedTrack)),
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
