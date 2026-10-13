using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbFootprintPlacementTests
{
    [TestMethod]
    public void PlacementPlanEmitsExactAnyPackedFootprintCandidate()
    {
        DocumentSpecifier document = Document();
        DocumentLifecycleState state = State(document);
        FootprintInstance source = Footprint(BoardLayer.BlBCu, 90.0);

        var first = PcbFootprintPlacementTools.BuildPlan(document, state, source);
        var second = PcbFootprintPlacementTools.BuildPlan(document, state, source.Clone());
        Assert.AreEqual(first.PlanId, second.PlanId);

        CreateItems candidate = BoardJson.Parser.Parse<CreateItems>(first.CandidateRequestJson);
        Assert.AreEqual(document, candidate.Header.Document);
        FootprintInstance placed = candidate.Items.Single().Unpack<FootprintInstance>();
        Assert.AreEqual(source, placed);
        Assert.AreEqual("Generated", placed.Definition.Id.LibraryNickname);
        Assert.AreEqual("QFN-4", placed.Definition.Id.EntryName);
        Assert.AreEqual(BoardLayer.BlBCu, placed.Layer);
        Assert.AreEqual(90.0, placed.Orientation.ValueDegrees);
    }

    [TestMethod]
    public void PlacementApplyBuildsOneCheckedCreateTransactionWithRetainedOperationIdentity()
    {
        DocumentSpecifier document = Document();
        DocumentLifecycleState state = State(document);
        FootprintInstance source = Footprint(BoardLayer.BlFCu, 0);
        var candidate = new CreateItems { Header = new() { Document = document }, Items = { Any.Pack(source) } };
        string operationId = Guid.NewGuid().ToString("D");

        CheckedPcbItemTransaction checkedRequest = PcbFootprintPlacementTools.BuildCheckedRequest(
            state, candidate, operationId);

        Assert.AreEqual(operationId, checkedRequest.OperationId);
        Assert.AreEqual(state.ProcessEpoch, checkedRequest.ProcessEpoch);
        Assert.AreEqual(1, checkedRequest.Operations.Count);
        Assert.AreEqual(PcbItemOperation.ActionOneofCase.Create, checkedRequest.Operations[0].ActionCase);
        Assert.AreEqual(candidate, checkedRequest.Operations[0].Create);
        CheckedPcbItemContract.ValidateRequest(checkedRequest, state.ProcessEpoch, inspect: true);
    }

    [TestMethod]
    public void PlacementPlanRejectsNonCopperFootprintLayer()
    {
        DocumentSpecifier document = Document();
        DocumentLifecycleState state = State(document);
        FootprintInstance source = Footprint(BoardLayer.BlFSilkS, 0);

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbFootprintPlacementTools.BuildPlan(document, state, source));
        Assert.AreEqual("invalid_pcb_footprint_layer", error.Code);
    }

    [TestMethod]
    public void PlacementPlanRejectsUnidentifiedLibraryDefinition()
    {
        DocumentSpecifier document = Document();
        DocumentLifecycleState state = State(document);
        FootprintInstance source = Footprint(BoardLayer.BlFCu, 0);
        source.Definition.Id.EntryName = "";

        AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
            PcbFootprintPlacementTools.BuildPlan(document, state, source));
        Assert.AreEqual("invalid_pcb_footprint_library_id", error.Code);
    }

    [TestMethod]
    public void TypedPlacementPreservesCompleteCheckpointAndPermitsRepeatedPadNumbers()
    {
        var json = PcbFootprintTypedFixture.Json();
        json["footprint"]!["pads"]![1]!["number"] = "1";
        var source = PcbFootprintTypedFixture.Input(json).ToNative(CancellationToken.None);
        var plan = PcbFootprintPlacementTools.BuildPlan(source.Document, source.State, source.Footprint);
        var candidate = BoardJson.Parser.Parse<CreateItems>(plan.CandidateRequestJson);
        var request = PcbFootprintPlacementTools.BuildCheckedRequest(source.State, candidate,
            "dddddddd-dddd-dddd-dddd-dddddddddddd");
        CheckedPcbItemContract.ValidateRequest(request, source.State.ProcessEpoch);
        Assert.IsTrue(request.ExpectedState.CompleteChangeTracking);
        Assert.IsTrue(request.ExpectedState.DiskBaselineChecked);
        Assert.AreEqual(source.State.Revision, request.ExpectedState.Revision);
        Assert.AreEqual(source.Footprint, candidate.Items.Single().Unpack<FootprintInstance>());
        Assert.AreEqual("1", source.Footprint.Definition.Items[1].Unpack<Pad>().Number);
    }

    [TestMethod]
    public void TypedPlacementRejectsMalformedGeometryAndDuplicateIdentities()
    {
        foreach (string defect in new[] { "zero-pad", "duplicate-id", "collinear-arc", "unknown-layer", "null-child", "invalid-circle" })
        {
            var json = PcbFootprintTypedFixture.Json();
            switch (defect)
            {
                case "zero-pad": json["footprint"]!["pads"]![0]!["widthNm"] = 0; break;
                case "duplicate-id": json["footprint"]!["pads"]![0]!["id"] = "11111111-1111-1111-1111-111111111111"; break;
                case "collinear-arc":
                    json["footprint"]!["graphics"]![0]!["kind"] = "Arc";
                    json["footprint"]!["graphics"]![0]!["points"] = System.Text.Json.Nodes.JsonNode.Parse(
                        "[{\"xNm\":0,\"yNm\":0},{\"xNm\":10,\"yNm\":0},{\"xNm\":20,\"yNm\":0}]");
                    break;
                case "unknown-layer": json["footprint"]!["pads"]![0]!["layers"]![0] = "unknown"; break;
                case "null-child": json["footprint"]!["pads"]![0] = null; break;
                case "invalid-circle": json["footprint"]!["pads"]![0]!["shape"] = "Circle"; break;
            }
            AutomationException error = Assert.ThrowsExactly<AutomationException>(() =>
                PcbFootprintTypedFixture.Input(json).ToNative(CancellationToken.None), defect);
            Assert.AreEqual("invalid_pcb_footprint_typed_input", error.Code, defect);
        }
    }

    [TestMethod]
    public void TypedPlacementRejectsUnknownFieldsAndRepeatedJsonMembers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbFootprintPlacementInput>(
            "{\"footprintJson\":\"{}\"}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbFootprintPadInput>(
            "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"id\":\"22222222-2222-2222-2222-222222222222\"}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbFootprintPadInput>(
            "{\"shape\":\"Custom\"}", options));
    }

    [TestMethod]
    public void AssignmentValidationMatchesTypedSymbolFieldToFootprintIdentity()
    {
        var symbol = new SchematicSymbolInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            FootprintField = new() { Name = "Footprint", Text = new() { Text_ = "Generated:QFN-4" } }
        };
        FootprintInstance footprint = Footprint(BoardLayer.BlBCu, 0);
        footprint.Definition.Items.Clear();
        var result = new PcbFootprintValidationTools().ValidateAssignment(
            SchematicJson.Formatter.Format(symbol), BoardJson.Formatter.Format(footprint));
        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.IsTrue(data.GetProperty("valid").GetBoolean(), Text(result));
        Assert.IsTrue(data.GetProperty("assignmentMatched").GetBoolean(), Text(result));
        Assert.AreEqual("Generated:QFN-4", data.GetProperty("assignment").GetString());
        Assert.IsFalse(data.GetProperty("nativeContacted").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("pinMappingChecked").GetBoolean(), Text(result));
    }

    [TestMethod]
    public void AssignmentValidationReportsExactMismatchWithoutInferringAReplacement()
    {
        var symbol = new SchematicSymbolInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            FootprintField = new() { Name = "Footprint", Text = new() { Text_ = "Generated:SOIC-8" } }
        };
        FootprintInstance footprint = Footprint(BoardLayer.BlFCu, 0);
        footprint.Definition.Items.Clear();
        var result = new PcbFootprintValidationTools().ValidateAssignment(
            SchematicJson.Formatter.Format(symbol), BoardJson.Formatter.Format(footprint));
        Assert.IsFalse(result.IsError ?? false, Text(result));
        JsonElement data = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.IsFalse(data.GetProperty("valid").GetBoolean(), Text(result));
        Assert.IsFalse(data.GetProperty("assignmentMatched").GetBoolean(), Text(result));
        Assert.IsTrue(data.GetProperty("issues").EnumerateArray()
            .Any(issue => issue.GetProperty("code").GetString() == "footprint_assignment_mismatch"), Text(result));
        Assert.AreEqual("Generated:SOIC-8", data.GetProperty("assignment").GetString());
    }

    private static FootprintInstance Footprint(BoardLayer layer, double rotation) => new()
    {
        Id = new() { Value = Guid.Parse("8d37b5f9-9b0d-4a64-9d7b-ef9517fe6f42").ToString("D") },
        Position = new() { XNm = 12_345_000, YNm = 23_456_000 },
        Orientation = new() { ValueDegrees = rotation },
        Layer = layer,
        Locked = LockedState.LsUnlocked,
        Definition = new()
        {
            Id = new() { LibraryNickname = "Generated", EntryName = "QFN-4" },
            Attributes = new() { MountingStyle = FootprintMountingStyle.FmsSmd },
            Items = { Any.Pack(new BoardGraphicShape
            {
                Id = new() { Value = Guid.Parse("b3b6f554-e8d4-4c02-bb74-cb2f94b1a6aa").ToString("D") },
                Layer = BoardLayer.BlFSilkS
            }) }
        }
    };

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text ?? "<no tool content>";

    private static DocumentSpecifier Document() => new()
    {
        Type = DocumentType.DoctypePcb,
        BoardFilename = "placement.kicad_pcb",
        Project = new() { Name = "placement", Path = "/tmp/kicad-placement" }
    };

    private static DocumentLifecycleState State(DocumentSpecifier document)
    {
        string identity = Guid.NewGuid().ToString("D");
        return new()
        {
            Document = document,
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = Guid.NewGuid().ToString("D"),
            NativeIdentity = identity,
            ProjectSettingsIncluded = true,
            StateSha256 = new string('a', 64),
            Revision = new() { Epoch = identity, Sequence = 7 }
        };
    }
}
