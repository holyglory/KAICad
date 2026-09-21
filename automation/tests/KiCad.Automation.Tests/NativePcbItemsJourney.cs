using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;
using Kiapi.Board.Commands;
using Kiapi.Board.Jobs;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativePcbItems(NativeClient client, DocumentSpecifier schematic,
        string evidence, string instanceId, CancellationToken token)
    {
        string boardPath = Path.Combine(schematic.Project.Path, schematic.Project.Name + ".kicad_pcb");
        var boardOpen = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token, toolName: "kicad_pcb_create");
        var board = boardOpen.Document;
        await SaveCheckedThroughMcp(client, board, evidence, token, verifyReconnect: false);
        var before = await ObserveLifecycleState(client, board, token);
        var track = new Track
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 10_000_000, YNm = 10_000_000 },
            End = new() { XNm = 20_000_000, YNm = 10_000_000 },
            Width = new() { ValueNm = 250_000 }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER_RAIL" }
        };
        var create = new CreateItems { Header = new() { Document = board } };
        create.Items.Add(Any.Pack(track));
        string stateDirectory = Directory.CreateTempSubdirectory("kicad-pcb-items-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                stateDirectory, Path.Combine(evidence, instanceId + "-pcb-items-mcp.stderr.log"), token);
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean());
            var createdReply = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            Assert.IsFalse(createdReply.TryGetProperty("isError", out var createError) && createError.GetBoolean(), createdReply.GetRawText());
            var createdData = createdReply.GetProperty("structuredContent");
            Assert.IsTrue(createdData.GetProperty("atomicTransaction").GetBoolean());
            var createdResponse = BoardJson.Parser.Parse<CreateItemsResponse>(createdData.GetProperty("response").GetRawText());
            Assert.AreEqual(ItemRequestStatus.IrsOk, createdResponse.Status);
            var createdTrack = createdResponse.CreatedItems.Single().Item.Unpack<Track>();
            Assert.AreEqual(track.Id, createdTrack.Id);
            string afterCreate = createdData.GetProperty("afterState").GetString()!;
            var stale = await mcp.Tool("kicad_pcb_items_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(create),
                expectedStateJson = SchematicJson.Formatter.Format(before)
            });
            Assert.IsTrue(stale.GetProperty("isError").GetBoolean());
            var moved = createdTrack.Clone(); moved.End.XNm += 5_000_000;
            var update = new UpdateItems { Header = new() { Document = board } };
            update.Items.Add(Any.Pack(moved));
            var updatedReply = await mcp.Tool("kicad_pcb_items_update", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(update), expectedStateJson = afterCreate
            });
            Assert.IsFalse(updatedReply.TryGetProperty("isError", out var updateError) && updateError.GetBoolean(), updatedReply.GetRawText());
            Assert.IsTrue(updatedReply.GetProperty("structuredContent").GetProperty("atomicTransaction").GetBoolean());
            var actual = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { createdTrack.Id } }, token);
            var actualTrack = actual.Items.Single().Unpack<Track>();
            Assert.AreEqual(moved.End, actualTrack.End);
            var guideShape = new BoardGraphicShape
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") }, Layer = BoardLayer.BlDwgsUser,
                Shape = new GraphicShape
                {
                    Attributes = new() { Stroke = new() { Width = new() { ValueNm = 100_000 } } },
                    Segment = new() { Start = new() { XNm = 10_000_000, YNm = 12_000_000 }, End = new() { XNm = 20_000_000, YNm = 12_000_000 } }
                }
            };
            var guideCreate = new CreateItems { Header = new() { Document = board } };
            guideCreate.Items.Add(Any.Pack(guideShape));
            string guideId = Guid.NewGuid().ToString("D");
            var guideReply = await mcp.Tool("kicad_pcb_guide_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(guideCreate),
                expectedStateJson = updatedReply.GetProperty("structuredContent").GetProperty("afterState").GetString(),
                guideId, sourceSha256 = new string('a', 64)
            });
            Assert.IsFalse(guideReply.TryGetProperty("isError", out var guideError) && guideError.GetBoolean(), guideReply.GetRawText());
            var guideData = guideReply.GetProperty("structuredContent");
            string guideState = guideData.GetProperty("afterState").GetString()!;
            var guideRead = await client.InvokeAsync<GetItemsById, GetItemsResponse>(new()
            { Header = new() { Document = board }, Items = { guideShape.Id } }, token);
            var persistedGuide = guideRead.Items.Single().Unpack<BoardGraphicShape>();
            Assert.AreEqual(BoardLayer.BlDwgsUser, persistedGuide.Layer);
            Assert.IsTrue(persistedGuide.CustomProperties.Any(p => p.Key == "kicad.ai.guide.role" && p.Value == "visual-underlay"));
            var candidateTrack = track.Clone(); candidateTrack.Id = new() { Value = Guid.NewGuid().ToString("D") };
            var candidateRequest = new CreateItems { Header = new() { Document = board } }; candidateRequest.Items.Add(Any.Pack(candidateTrack));
            var candidateReply = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, requestJson = BoardJson.Formatter.Format(candidateRequest),
                expectedStateJson = guideState, guideId,
                sourceSha256 = new string('a', 64)
            });
            // The guide ID is provenance supplied by the caller; the validator must
            // remain honest about not resolving it to a saved guide object yet.
            Assert.IsFalse(candidateReply.TryGetProperty("isError", out var candidateError) && candidateError.GetBoolean(), candidateReply.GetRawText());
            Assert.IsTrue(candidateReply.GetProperty("structuredContent").GetProperty("structuralValidation").GetBoolean());
            Assert.IsFalse(candidateReply.GetProperty("structuredContent").GetProperty("nativeCommit").GetBoolean());
            var invalidCandidate = candidateTrack.Clone(); invalidCandidate.Net = new();
            var invalidCandidateRequest = new CreateItems { Header = new() { Document = board } }; invalidCandidateRequest.Items.Add(Any.Pack(invalidCandidate));
            var rejectedCandidate = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, requestJson = BoardJson.Formatter.Format(invalidCandidateRequest),
                expectedStateJson = guideState, guideId, sourceSha256 = new string('a', 64)
            });
            Assert.IsTrue(rejectedCandidate.GetProperty("isError").GetBoolean());
            var copperGuide = guideShape.Clone(); copperGuide.Layer = BoardLayer.BlFCu;
            var copperRequest = new CreateItems { Header = new() { Document = board } }; copperRequest.Items.Add(Any.Pack(copperGuide));
            var rejectedGuide = await mcp.Tool("kicad_pcb_guide_create", new
            {
                instanceId, requestJson = BoardJson.Formatter.Format(copperRequest), expectedStateJson = guideState,
                guideId = Guid.NewGuid().ToString("D"), sourceSha256 = new string('b', 64)
            });
            Assert.IsTrue(rejectedGuide.GetProperty("isError").GetBoolean());
            const string svg = "<svg viewBox=\"0 0 10 10\"><polyline points=\"1,1 5,1 5,5\"/></svg>";
            string svgHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
            string svgGuideId = Guid.NewGuid().ToString("D");
            var svgReply = await mcp.Tool("kicad_pcb_guide_svg_create", new
            {
                instanceId, documentJson = SchematicJson.Formatter.Format(board), expectedStateJson = guideState,
                svg, repositoryRoot = board.Project.Path, sourceArchivePath = "guides/rf.svg", guideId = svgGuideId,
                sourceSha256 = svgHash, layer = "Dwgs.User", originXNm = "30000000", originYNm = "30000000",
                nanometersPerSvgUnit = "100000", strokeWidthNm = 100000
            });
            Assert.IsFalse(svgReply.TryGetProperty("isError", out var svgError) && svgError.GetBoolean(), svgReply.GetRawText());
            Assert.AreEqual(svg, await File.ReadAllTextAsync(Path.Combine(board.Project.Path, "guides/rf.svg"), token));
            string svgState = svgReply.GetProperty("structuredContent").GetProperty("afterState").GetString()!;
            var svgShapes = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
            { Header = new() { Document = board }, Types_ = { KiCadObjectType.KotPcbShape } }, token);
            Assert.IsTrue(svgShapes.Items.Any(item => item.Unpack<BoardGraphicShape>().CustomProperties.Any(p => p.Key == "kicad.ai.guide.source_format" && p.Value == "svg")));
            var renderRequest = new RunBoardJobExportRender
            {
                JobSettings = new() { Document = board }, Format = RenderFormat.RfPng,
                Quality = RenderQuality.RqBasic, BackgroundStyle = RenderBackgroundStyle.RbsOpaque,
                Width = 320, Height = 240, Side = RenderSide.RsTop, Zoom = 1, Perspective = false
            };
            var renderReply = await mcp.Tool("kicad_pcb_render_3d", new
            {
                instanceId, requestJson = JsonFormatter.Default.Format(renderRequest), expectedStateJson = svgState
            });
            Assert.IsFalse(renderReply.TryGetProperty("isError", out var renderError) && renderError.GetBoolean(), renderReply.GetRawText());
            var renderData = renderReply.GetProperty("structuredContent");
            Assert.IsTrue(renderData.GetProperty("nativeRender").GetBoolean());
            Assert.IsFalse(renderData.GetProperty("boardMutation").GetBoolean());
            Assert.IsTrue(renderData.GetProperty("image").GetProperty("bytes").GetInt32() > 0);
            var renderAfter = SchematicJson.Parser.Parse<DocumentLifecycleState>(renderData.GetProperty("afterState").GetString()!);
            Assert.AreEqual(svgState, SchematicJson.Formatter.Format(renderAfter));
            var generatedCandidate = await mcp.Tool("kicad_pcb_route_candidate_from_guide", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch, documentJson = SchematicJson.Formatter.Format(board),
                expectedStateJson = svgState, guideId = svgGuideId, sourceSha256 = svgHash,
                netName = "POWER_RAIL", layer = "F.Cu", widthNm = 250000L
            });
            Assert.IsFalse(generatedCandidate.TryGetProperty("isError", out var generatedError) && generatedError.GetBoolean(), generatedCandidate.GetRawText());
            var generatedData = generatedCandidate.GetProperty("structuredContent");
            Assert.IsTrue(generatedData.GetProperty("structuralValidation").GetBoolean());
            Assert.IsTrue(generatedData.GetProperty("electricalNetResolved").GetBoolean());
            Assert.IsFalse(generatedData.GetProperty("nativeCommit").GetBoolean());
            var generatedRequest = BoardJson.Parser.Parse<CreateItems>(generatedData.GetProperty("candidateRequestJson").GetString()!);
            Assert.AreEqual(2, generatedRequest.Items.Count);
            var generatedTrack = generatedRequest.Items[0].Unpack<Track>();
            Assert.AreEqual(BoardLayer.BlFCu, generatedTrack.Layer);
            Assert.AreEqual("POWER_RAIL", generatedTrack.Net.Name);
            Assert.AreEqual(30_100_000, generatedTrack.Start.XNm);
            var generatedValidation = await mcp.Tool("kicad_pcb_route_candidate_validate", new
            {
                instanceId, expectedInstanceEpoch = client.Epoch,
                requestJson = BoardJson.Formatter.Format(generatedRequest), expectedStateJson = svgState,
                guideId = svgGuideId, sourceSha256 = svgHash
            });
            Assert.IsFalse(generatedValidation.TryGetProperty("isError", out var generatedValidationError) && generatedValidationError.GetBoolean(), generatedValidation.GetRawText());
            Assert.IsTrue(generatedValidation.GetProperty("structuredContent").GetProperty("structuralValidation").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-pcb-items.json"), updatedReply.GetRawText(), token);
        }
        finally { Directory.Delete(stateDirectory, true); }
        await SaveCheckedThroughMcp(client, board, evidence, token, verifyReconnect: false);
    }
}
