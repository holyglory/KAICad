using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static void PrepareNativeCrossProbeBoard(string projectDirectory)
    {
        // This checked-in board contains real U1/J1/J3 footprints and named
        // nets, so the cross-probe requests resolve through PCBNEW rather than
        // a synthetic peer or an in-memory board fixture.
        string source = Path.Combine(RepositoryRoot(), "qa", "data", "cli", "basic_test", "basic_test.kicad_pcb");
        File.Copy(source, Path.Combine(projectDirectory, "fixture.kicad_pcb"), true);
    }

    private static async Task VerifyNativeCrossProbe(NativeClient client, DocumentSpecifier schematic,
        InstanceRegistry registry, IReadOnlyList<(string Id, string Endpoint, string Project, string RootId)> launched,
        string evidence, string instanceId, CancellationToken token)
    {
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
            toolName: "kicad_pcb_open");
        DocumentSpecifier board = opened.Document;
        DocumentLifecycleState before = await ObserveLifecycleState(client, board, token);
        string beforeJson = SchematicJson.Formatter.Format(before);

        string mcpState = Path.Combine(evidence, instanceId + "-cross-probe-mcp-state");
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            mcpState, Path.Combine(evidence, instanceId + "-cross-probe-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
        {
            endpoint = client.Endpoint,
            expectedInstanceId = instanceId
        }));

        var boardFootprints = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = board },
            Types_ = { KiCadObjectType.KotPcbFootprint }
        }, token);
        Assert.IsTrue(boardFootprints.Items.Any(item => item.Is(FootprintInstance.Descriptor)
            && item.Unpack<FootprintInstance>().ReferenceField.Text.Text.Text_ == "U1"),
            "The copied board must expose U1 through the live PCB API.");

        var selectionRequest = new SyncSelection
        {
            Context = SyncSelectionContext.SscExplicit,
            Mode = SyncSelectionMode.SsmItemsAndNets,
            Items =
            {
                new SelectionSpec
                {
                    Footprint = new FootprintSelectionSpec { Reference = "U1" }
                }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-selection-request.json"),
            SchematicJson.Formatter.Format(selectionRequest), token);
        JsonElement selectionReply = await mcp.Tool("kicad_pcb_sync_selection", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(selectionRequest),
            expectedStateJson = beforeJson,
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(selectionReply);
        JsonElement selectionData = selectionReply.GetProperty("structuredContent");
        Assert.IsTrue(selectionData.GetProperty("nativeGuiOnly").GetBoolean(), selectionReply.GetRawText());
        Assert.IsTrue(selectionData.GetProperty("selectionChanged").GetBoolean(), selectionReply.GetRawText());
        Assert.IsFalse(selectionData.GetProperty("boardMutation").GetBoolean(), selectionReply.GetRawText());
        Assert.AreEqual(beforeJson, selectionData.GetProperty("beforeState").GetString(), selectionReply.GetRawText());
        Assert.AreEqual(beforeJson, selectionData.GetProperty("afterState").GetString(), selectionReply.GetRawText());

        SelectionResponse selected = new();
        using (var selectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            selectionDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            while (true)
            {
                selected = await client.InvokeAsync<GetSelection, SelectionResponse>(new()
                {
                    Header = new() { Document = board }
                }, selectionDeadline.Token);
                if (selected.Items.Count > 0)
                    break;
                await Task.Delay(100, selectionDeadline.Token);
            }
        }
        Assert.IsTrue(selected.Items.Any(item => item.Is(FootprintInstance.Descriptor)
            && item.Unpack<FootprintInstance>().ReferenceField.Text.Text.Text_ == "U1"),
            "The live PCB editor must select the requested U1 footprint. Selection: "
            + string.Join(", ", selected.Items.Select(item => item.TypeUrl)));
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token),
            "Cross-probe selection must not advance the board lifecycle revision.");

        JsonElement highlightReply = await mcp.Tool("kicad_pcb_highlight_nets", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(new HighlightNets { NetName = { "/OUT" } }),
            expectedStateJson = beforeJson,
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(highlightReply);
        JsonElement highlightData = highlightReply.GetProperty("structuredContent");
        Assert.IsTrue(highlightData.GetProperty("nativeGuiOnly").GetBoolean(), highlightReply.GetRawText());
        Assert.IsTrue(highlightData.GetProperty("selectionChanged").GetBoolean(), highlightReply.GetRawText());
        Assert.IsFalse(highlightData.GetProperty("boardMutation").GetBoolean(), highlightReply.GetRawText());
        Assert.AreEqual(beforeJson, highlightData.GetProperty("beforeState").GetString(), highlightReply.GetRawText());
        Assert.AreEqual(beforeJson, highlightData.GetProperty("afterState").GetString(), highlightReply.GetRawText());
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token),
            "Net highlighting must not change the board lifecycle revision.");

        // Attach the sibling native instance to the same MCP process. Sending
        // the first board with the sibling instance proves that admission binds
        // the document checkpoint to the process identity before dispatch.
        var sibling = launched.Single(target => target.Id != instanceId);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
        {
            endpoint = sibling.Endpoint,
            expectedInstanceId = sibling.Id
        }));
        JsonElement wrongInstance = await mcp.Tool("kicad_pcb_highlight_nets", new
        {
            instanceId = sibling.Id,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(new HighlightNets { NetName = { "/OUT" } }),
            expectedStateJson = beforeJson,
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertToolError(wrongInstance, "pcb_instance_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

        var staleRevision = before.Clone();
        staleRevision.Revision.Sequence++;
        JsonElement stale = await mcp.Tool("kicad_pcb_sync_selection", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(selectionRequest),
            expectedStateJson = SchematicJson.Formatter.Format(staleRevision),
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertToolError(stale, "pcb_state_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

        var staleEpoch = before.Clone();
        staleEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
        JsonElement wrongEpoch = await mcp.Tool("kicad_pcb_sync_selection", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(selectionRequest),
            expectedStateJson = SchematicJson.Formatter.Format(staleEpoch),
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertToolError(wrongEpoch, "pcb_instance_changed");
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

        var wrongDocument = before.Clone();
        wrongDocument.Document.BoardFilename = "other.kicad_pcb";
        JsonElement mismatchedDocument = await mcp.Tool("kicad_pcb_sync_selection", new
        {
            instanceId,
            documentJson = SchematicJson.Formatter.Format(board),
            requestJson = SchematicJson.Formatter.Format(selectionRequest),
            expectedStateJson = SchematicJson.Formatter.Format(wrongDocument),
            operationId = Guid.NewGuid().ToString("D")
        });
        AssertToolError(mismatchedDocument, "invalid_pcb_operation_state");
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));

        // Keep the registry reference in the signature so this journey's
        // wrong-instance case is visibly tied to the live process registry.
        Assert.AreEqual(sibling.Id, registry.Get(sibling.Id).InstanceId);
    }

    private static void AssertToolError(JsonElement result, string expectedCode)
    {
        Assert.IsTrue(result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(),
            result.GetRawText());
        JsonElement structured = result.GetProperty("structuredContent");
        Assert.AreEqual(expectedCode, structured.GetProperty("errorCode").GetString(), result.GetRawText());
        Assert.IsFalse(structured.GetProperty("mutationSubmitted").GetBoolean(), result.GetRawText());
    }
}
