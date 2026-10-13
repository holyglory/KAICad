using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbFootprintTypedMcpProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TypedPlacementPublishesSchemaAndExactDeterministicCandidate()
    {
        await Run(async mcp =>
        {
            JsonElement listing = await mcp.ListTools(null);
            JsonElement tools = listing.GetProperty("tools");
            foreach (string name in new[] { "kicad_pcb_footprint_place_plan_typed", "kicad_pcb_footprint_place_apply_typed" })
            {
                JsonElement tool = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name);
                JsonElement input = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("input");
                foreach (string property in new[] { "document", "expectedState", "footprint", "operationId", "expectedPlanId" })
                    Assert.IsTrue(input.GetProperty("properties").TryGetProperty(property, out _), property);
                JsonElement geometry = input.GetProperty("properties").GetProperty("footprint").GetProperty("properties");
                foreach (string property in new[] { "library", "xNm", "yNm", "layer", "referenceField", "valueField", "pads", "graphics" })
                    Assert.IsTrue(geometry.TryGetProperty(property, out _), property);
                Assert.IsFalse(input.GetProperty("properties").TryGetProperty("footprintJson", out _));
            }

            JsonObject inputJson = PcbFootprintTypedFixture.Json();
            JsonElement result = await mcp.Tool("kicad_pcb_footprint_place_plan_typed", new { input = inputJson });
            Assert.IsFalse(result.GetProperty("isError").GetBoolean(), result.GetRawText());
            JsonElement data = result.GetProperty("structuredContent");
            Assert.IsTrue(data.GetProperty("proposalOnly").GetBoolean());
            Assert.IsFalse(data.GetProperty("nativeCommit").GetBoolean());
            Assert.IsFalse(data.GetProperty("drcValidated").GetBoolean());
            Assert.AreEqual("U4", data.GetProperty("reference").GetString());

            CreateItems candidate = BoardJson.Parser.Parse<CreateItems>(data.GetProperty("candidateRequestJson").GetString()!);
            FootprintInstance footprint = candidate.Items.Single().Unpack<FootprintInstance>();
            Assert.AreEqual("placement.kicad_pcb", candidate.Header.Document.BoardFilename);
            Assert.AreEqual("Generated", footprint.Definition.Id.LibraryNickname);
            Assert.AreEqual(12_000_000, footprint.Position.XNm);
            Assert.AreEqual(3, footprint.Definition.Items.Count);
            Pad pad = footprint.Definition.Items[0].Unpack<Pad>();
            Assert.AreEqual("GND", pad.Net.Name);
            Assert.AreEqual(PadStackShape.PssRoundrect, pad.PadStack.CopperLayers.Single().Shape);
            Assert.AreEqual(0.25, pad.PadStack.CopperLayers.Single().CornerRoundingRatio);
            Assert.AreEqual(GraphicShape.GeometryOneofCase.Rectangle,
                footprint.Definition.Items[2].Unpack<BoardGraphicShape>().Shape.GeometryCase);

            JsonElement repeated = await mcp.Tool("kicad_pcb_footprint_place_plan_typed", new { input = inputJson });
            Assert.AreEqual(data.GetProperty("planId").GetString(), repeated.GetProperty("structuredContent").GetProperty("planId").GetString());
            Assert.AreEqual(data.GetProperty("candidateRequestJson").GetString(),
                repeated.GetProperty("structuredContent").GetProperty("candidateRequestJson").GetString());
        });
    }

    [TestMethod]
    public async Task TypedPlacementRejectsChangedPlanAndMissingOperationBeforeNativeContact()
    {
        await Run(async mcp =>
        {
            string missingInstance = Guid.NewGuid().ToString("D");
            JsonObject input = PcbFootprintTypedFixture.Json();
            JsonElement preview = await mcp.Tool("kicad_pcb_footprint_place_plan_typed", new { input });
            string planId = preview.GetProperty("structuredContent").GetProperty("planId").GetString()!;
            input["operationId"] = "dddddddd-dddd-dddd-dddd-dddddddddddd";
            input["expectedPlanId"] = planId;
            input["footprint"]!["xNm"] = 12_500_000;
            JsonElement changed = await mcp.Tool("kicad_pcb_footprint_place_apply_typed", new { instanceId = missingInstance, input });
            AssertFailure(changed, "stale_pcb_footprint_plan");

            input["footprint"]!["xNm"] = 12_000_000;
            input.Remove("operationId");
            JsonElement missingOperation = await mcp.Tool("kicad_pcb_footprint_place_apply_typed", new { instanceId = missingInstance, input });
            AssertFailure(missingOperation, "invalid_operation_id");

            JsonElement incomplete = await mcp.Tool("kicad_pcb_footprint_place_plan_typed", new { input = new { } });
            AssertFailure(incomplete, "invalid_pcb_footprint_typed_input");

            input["operationId"] = "dddddddd-dddd-dddd-dddd-dddddddddddd";
            input["expectedState"]!["completeChangeTracking"] = false;
            JsonElement partial = await mcp.Tool("kicad_pcb_footprint_place_plan_typed", new { input });
            AssertFailure(partial, "invalid_pcb_footprint_typed_input");
        });
    }

    private static void AssertFailure(JsonElement result, string code)
    {
        Assert.IsTrue(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        Assert.AreEqual(code, result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), result.GetRawText());
    }

    private async Task Run(Func<StdioMcpFixture, Task> check)
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-footprint-typed-").FullName;
        string stderr = Path.Combine(root, "stderr.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), stderr, timeout.Token);
            await check(mcp);
        }
        finally
        {
            if (File.Exists(stderr))
            {
                string retained = Path.Combine(TestContext.TestResultsDirectory!, TestContext.TestName + "-mcp.log");
                File.Copy(stderr, retained, true);
                TestContext.AddResultFile(retained);
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

internal static class PcbFootprintTypedFixture
{
    internal static JsonObject Json()
    {
        var input = JsonNode.Parse("""
            {
              "document": { "type": "DOCTYPE_PCB", "project": { "name": "placement", "path": "/tmp/placement" }, "boardFilename": "placement.kicad_pcb" },
              "expectedState": {
                "document": { "type": "DOCTYPE_PCB", "project": { "name": "placement", "path": "/tmp/placement" }, "boardFilename": "placement.kicad_pcb" },
                "revision": { "epoch": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "sequence": 7 },
                "nativeIdentity": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "stateSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "processEpoch": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "scope": "DLS_PCB",
                "projectSettingsIncluded": true, "completeChangeTracking": true, "diskBaselineChecked": true,
                "nativeFiles": ["/tmp/placement/placement.kicad_pcb"],
                "fileBaselines": [{ "path": "/tmp/placement/placement.kicad_pcb", "baselinePath": "/tmp/placement/placement.kicad_pcb", "baselineKnown": true,
                  "baselineExists": true, "baselineSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "baselineBytes": 100,
                  "currentKnown": true, "currentExists": true, "currentSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "currentBytes": 100, "status": "NFBS_UNCHANGED" }]
              },
              "footprint": {
                "id": "11111111-1111-1111-1111-111111111111", "library": { "libraryNickname": "Generated", "entryName": "TwoPad" },
                "xNm": 12000000, "yNm": 23000000, "rotationDegrees": 90, "layer": "F.Cu", "mounting": "Smd",
                "referenceField": { "id": "22222222-2222-2222-2222-222222222222", "text": "U4", "layer": "F.SilkS", "yNm": -2000000 },
                "valueField": { "id": "33333333-3333-3333-3333-333333333333", "text": "TwoPad", "layer": "F.Fab", "yNm": 2000000 },
                "pads": [
                  { "id": "44444444-4444-4444-4444-444444444444", "number": "1", "netName": "GND", "type": "Smd", "shape": "RoundRectangle",
                    "xNm": -1000000, "widthNm": 1200000, "heightNm": 800000, "cornerRoundingRatio": 0.25, "layers": ["F.Cu", "F.Paste", "F.Mask"] },
                  { "id": "55555555-5555-5555-5555-555555555555", "number": "2", "netName": "VCC", "type": "Smd", "shape": "Rectangle",
                    "xNm": 1000000, "widthNm": 1200000, "heightNm": 800000, "layers": ["F.Cu", "F.Paste", "F.Mask"] }
                ],
                "graphics": [{ "id": "66666666-6666-6666-6666-666666666666", "kind": "Rectangle", "layer": "F.SilkS", "strokeWidthNm": 150000,
                  "points": [{ "xNm": -2000000, "yNm": -1000000 }, { "xNm": 2000000, "yNm": 1000000 }] }]
              }
            }
            """)!.AsObject();
        string project = Path.Combine(Path.GetTempPath(), "kicad-typed-footprint-placement");
        string board = Path.Combine(project, "placement.kicad_pcb");
        input["document"]!["project"]!["path"] = project;
        input["expectedState"]!["document"]!["project"]!["path"] = project;
        input["expectedState"]!["nativeFiles"]![0] = board;
        input["expectedState"]!["fileBaselines"]![0]!["path"] = board;
        input["expectedState"]!["fileBaselines"]![0]!["baselinePath"] = board;
        return input;
    }

    internal static PcbFootprintPlacementInput Input(JsonObject? json = null) =>
        (json ?? Json()).Deserialize<PcbFootprintPlacementInput>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
