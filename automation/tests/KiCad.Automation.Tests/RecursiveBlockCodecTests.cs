using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockCodecTests
{
    [TestMethod]
    public void SharedMessagesPreserveImmutableHistoryAndRestoreSourceAcrossBinaryRoundTrip()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var draft = graph.StartDraft(graph.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Exact text\r\n  Ω🙂"), Name = "Changed system" };
        var origin = RecursiveBlockFixture.Origin("Agent client") with
        { RecordedAt = RecursiveBlockFixture.Origin().RecordedAt.AddTicks(13), Sources = [new("datasheet", "rev3", 4, "table2", "variantA")], InputIds = [Guid.NewGuid()] };
        var changed = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        var restoredDraft = changed.RestoreAsDraft(changed.StartDraft(changed.SelectedRoot), graph.SelectedRoot);
        var restored = changed.SaveDraft(changed.SelectedRoot, [changed.SelectedRoot], restoredDraft, Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        var data = RecursiveBlockCodec.Encode(restored);
        var decoded = RecursiveBlockCodec.Decode(P.RecursiveBlockGraphData.Parser.ParseFrom(data.ToByteArray()));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(restored), RecursiveBlockGraphXml.Write(decoded));
        Assert.AreEqual(graph.SelectedRoot, decoded.Inspect(decoded.SelectedRoot).RestoredFrom);
        Assert.AreEqual(origin.RecordedAt, decoded.Inspect(decoded.SelectedRoot).Origin.RecordedAt);
        Assert.AreEqual("Exact text\r\n  Ω🙂", decoded.Requirements(changed.SelectedRoot).Requirements.Routing);
    }

    [TestMethod]
    [DataRow("future-version")]
    [DataRow("wrong-target")]
    [DataRow("noncanonical-identity")]
    [DataRow("missing-origin")]
    [DataRow("missing-time")]
    [DataRow("unsupported-precision")]
    [DataRow("invalid-seconds")]
    [DataRow("negative-nanos")]
    [DataRow("unnormalized-nanos")]
    [DataRow("unknown-actor")]
    [DataRow("missing-fields")]
    [DataRow("empty-parent")]
    [DataRow("unknown-root-field")]
    [DataRow("unknown-nested-field")]
    // A schema 1 exchange (the native editor of this build) cannot carry any schema 2 field; each is refused
    // as the unsupported field it is for that version. The conversion receipt stays refused in schema 2 too.
    [DataRow("v2-interface-domain")]
    [DataRow("v2-interface-direction")]
    [DataRow("v2-level-presentation")]
    [DataRow("v2-interface-realization")]
    [DataRow("v2-connection-domain")]
    [DataRow("v2-connection-direction")]
    [DataRow("v2-connection-realization")]
    [DataRow("v2-migration")]
    [DataRow("v2-block-draft-interface-domain")]
    [DataRow("v2-block-draft-presentation")]
    [DataRow("v2-connection-draft-domain")]
    [DataRow("v2-connection-draft-direction")]
    [DataRow("v2-connection-draft-realization")]
    public void UnknownFieldsBadTargetsMissingFieldsAndUnsupportedPrecisionAreRejected(string scenario)
    {
        bool schemaTwo = scenario.StartsWith("v2-", StringComparison.Ordinal);
        var linked = LinkedDiagramFixture.Create();
        var data = RecursiveBlockCodec.Encode(schemaTwo ? linked.Graph : RecursiveBlockFixture.Create().Graph, schemaTwo && scenario != "v2-migration" ? 1u : 2u);
        var blockDraft = RecursiveBlockCodec.Encode(linked.Graph.StartDraft(linked.Blocks["PSU"]));
        var connectionDraft = RecursiveBlockCodec.Encode(linked.Graph.Connections(linked.Blocks["PSU"].BlockId).StartDraft(linked.Links["PSU/Supply"]));
        Guid document = linked.Graph.DocumentId;
        if (schemaTwo)
        {
            // Precision: every schema 1 carrier decodes while the schema 2 fields keep their default values.
            _ = RecursiveBlockCodec.Decode(data);
            _ = RecursiveBlockCodec.Decode(blockDraft, document, 1);
            _ = RecursiveBlockCodec.Decode(connectionDraft, document, 1);
        }
        P.BlockLocalDiagramData Level() => data.Revisions.First(r => r.LocalDiagram?.Interfaces.Count > 0).LocalDiagram;
        P.ConnectionRevisionData Link() => data.ConnectionArchives[0].Revisions[0];
        Action decode = () => RecursiveBlockCodec.Decode(data);
        switch (scenario)
        {
            case "future-version": data.SchemaVersion = 3; break;
            case "wrong-target": data.SelectedRoot.BlockId = Guid.NewGuid().ToString("D"); break;
            case "noncanonical-identity": data.SelectedRoot.StateId = data.SelectedRoot.StateId.ToUpperInvariant(); break;
            case "missing-origin": data.Revisions[0].Origin = null; break;
            case "missing-time": data.Revisions[0].Origin.RecordedAt = null; break;
            case "unsupported-precision": data.Revisions[0].Origin.RecordedAt.Nanos = 1; break;
            case "invalid-seconds": data.Revisions[0].Origin.RecordedAt.Seconds = long.MaxValue; break;
            case "negative-nanos": data.Revisions[0].Origin.RecordedAt.Nanos = -1; break;
            case "unnormalized-nanos": data.Revisions[0].Origin.RecordedAt.Nanos = 1000000000; break;
            case "unknown-actor": data.Revisions[0].Origin.Kind = P.DiagramActorKind.DakUnknown; break;
            case "missing-fields": data.RequirementHistories[0].Revisions[0].Fields = null; break;
            case "empty-parent": data.RequirementHistories[0].Revisions[0].ParentId = ""; break;
            case "unknown-root-field":
                data = P.RecursiveBlockGraphData.Parser.ParseFrom(data.ToByteArray().Concat(UnknownField).ToArray()); break;
            case "unknown-nested-field":
                data.Revisions[0].Selection = P.BlockSelectionData.Parser.ParseFrom(
                    data.Revisions[0].Selection.ToByteArray().Concat(UnknownField).ToArray()); break;
            case "v2-interface-domain": Level().Interfaces[0].Domain = P.DiagramDomain.DdPower; break;
            case "v2-interface-direction": Level().Interfaces[0].Direction = P.DiagramInterfaceDirection.DidrInput; break;
            case "v2-level-presentation": Level().Presentation = new() { Units = "diagram-unit" }; break;
            case "v2-interface-realization":
                Level().InterfaceRealizations.Add(new P.InterfaceRealizationData { InterfaceId = Level().Interfaces[0].Id,
                    State = P.DiagramRealizationState.DrsUnknown, UnresolvedReason = "Test-only: not yet decided." }); break;
            case "v2-connection-domain": Link().Domain = P.DiagramDomain.DdData; break;
            case "v2-connection-direction": Link().Direction = P.DiagramConnectionDirection.DcdrFromFirst; break;
            case "v2-connection-realization":
                Link().Realization = new() { State = P.DiagramRealizationState.DrsUnknown, UnresolvedReason = "Test-only: not yet decided." }; break;
            case "v2-migration": data.Migration = new() { Id = Guid.NewGuid().ToString("D") }; break;
            case "v2-block-draft-interface-domain":
                blockDraft.LocalDiagram.Interfaces[0].Domain = P.DiagramDomain.DdPower; decode = () => RecursiveBlockCodec.Decode(blockDraft, document, 1); break;
            case "v2-block-draft-presentation":
                blockDraft.LocalDiagram.Presentation = new(); decode = () => RecursiveBlockCodec.Decode(blockDraft, document, 1); break;
            case "v2-connection-draft-domain":
                connectionDraft.Domain = P.DiagramDomain.DdPower; decode = () => RecursiveBlockCodec.Decode(connectionDraft, document, 1); break;
            case "v2-connection-draft-direction":
                connectionDraft.Direction = P.DiagramConnectionDirection.DcdrBidirectional;
                decode = () => RecursiveBlockCodec.Decode(connectionDraft, document, 1); break;
            case "v2-connection-draft-realization":
                connectionDraft.Realization = new() { State = P.DiagramRealizationState.DrsUnknown, UnresolvedReason = "Test-only: not yet decided." };
                decode = () => RecursiveBlockCodec.Decode(connectionDraft, document, 1); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        var error = Assert.ThrowsExactly<AutomationException>(decode);
        if (schemaTwo || scenario is "unknown-root-field" or "unknown-nested-field")
            Assert.AreEqual("The recursive diagram message contains unsupported fields; no history was simplified.", error.Message, scenario);
    }

    // Field 1000 with a varint value: outside every lane band (100-499) and the Phase 3 range (500-999).
    private static readonly byte[] UnknownField = [0xc0, 0x3e, 0x01];

    [TestMethod]
    public void SchemaOneObservationsOmitSchemaTwoFieldsAndSchemaTwoOmitsOnlyTheUnimplemented()
    {
        var linked = LinkedDiagramFixture.Create();
        var graph = RecursiveBlockCodec.Encode(linked.Graph);
        var diagram = graph.Revisions.First(r => r.LocalDiagram?.Interfaces.Count > 0).Clone();
        diagram.LocalDiagram.Interfaces[0].Domain = P.DiagramDomain.DdPower;
        diagram.LocalDiagram.Presentation = new() { Units = "diagram-unit" };
        var link = graph.ConnectionArchives[0].Revisions[0].Clone();
        link.Direction = P.DiagramConnectionDirection.DcdrFromFirst;
        var observation = new P.RecursiveDiagramObservation { DocumentId = graph.DocumentId, Editor = new()
        {
            DocumentId = graph.DocumentId, StoredSchemaVersion = 1, SourceWritable = true, LevelDraft = new(), CanvasTool = "select",
            SelectedInterfaceId = diagram.LocalDiagram.Interfaces[0].Id
        } };
        observation.Editor.LevelViewports.Add(new P.DiagramLevelViewportState { BlockId = diagram.Selection.BlockId, Scale = 1 });
        var view = new P.RecursiveDiagramView { ViewId = "current", Units = "diagram-unit", Diagram = diagram, ResolvedLayout = new() { DormantEntries = 1 } };
        view.Connections.Add(link); observation.Views.Add(view);
        Assert.IsTrue(RecursiveBlockCodec.CarriesFieldBeyondSchema(observation, 1));
        Assert.IsTrue(RecursiveBlockCodec.CarriesUnimplementedField(observation), "The per-level editor state is still produced by nobody.");
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
        string[] editorOnly = ["storedSchemaVersion", "sourceWritable", "levelDraft", "levelViewports", "canvasTool", "selectedInterfaceId", "resolvedLayout"];
        string[] dataFields = ["domain", "direction", "presentation", "interfaceRealizations", "realization"];
        var wire = System.Text.Json.Nodes.JsonNode.Parse(formatter.Format(observation))!.AsObject();
        RecursiveBlockCodec.OmitFieldsBeyondSchema(observation, wire, 1);
        string text = wire.ToJsonString();
        foreach (string key in editorOnly.Concat(dataFields))
            Assert.IsFalse(text.Contains("\"" + key + "\":", StringComparison.Ordinal), key + " must stay out of a schema 1 observation.");
        // Precision: implemented fields, including computed defaults, are still reported.
        Assert.AreEqual("current", wire["views"]![0]!["viewId"]!.GetValue<string>());
        Assert.AreEqual(diagram.LocalDiagram.Interfaces[0].Name,
            wire["views"]![0]!["diagram"]!["localDiagram"]!["interfaces"]![0]!["name"]!.GetValue<string>());
        Assert.AreEqual(link.Name, wire["views"]![0]!["connections"]![0]!["name"]!.GetValue<string>());
        Assert.IsFalse(wire["editor"]!["dirty"]!.GetValue<bool>());
        // Schema 2 keeps the implemented data fields and still omits what nothing produces yet.
        var schemaTwo = System.Text.Json.Nodes.JsonNode.Parse(formatter.Format(observation))!.AsObject();
        RecursiveBlockCodec.OmitFieldsBeyondSchema(observation, schemaTwo, 2);
        string two = schemaTwo.ToJsonString();
        foreach (string key in editorOnly)
            Assert.IsFalse(two.Contains("\"" + key + "\":", StringComparison.Ordinal), key + " is not produced by any editor yet.");
        Assert.AreEqual("DD_POWER", schemaTwo["views"]![0]!["diagram"]!["localDiagram"]!["interfaces"]![0]!["domain"]!.GetValue<string>());
        Assert.AreEqual("DCDR_FROM_FIRST", schemaTwo["views"]![0]!["connections"]![0]!["direction"]!.GetValue<string>());
        Assert.IsFalse(RecursiveBlockCodec.CarriesFieldBeyondSchema(RecursiveBlockCodec.Encode(linked.Graph, 1), 1));
        Assert.IsFalse(RecursiveBlockCodec.CarriesUnimplementedField(RecursiveBlockCodec.Encode(linked.Graph)));
    }

    [TestMethod]
    public void PartialAndExactEndpointsRetainIndependentStatesSelectorsAndRepeatedSheetIdentity()
    {
        var pin = new DiagramPinTarget(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], "D15");
        var endpoint = new DiagramEndpointBinding(DiagramEndpointKind.Candidates, Guid.NewGuid(), Guid.NewGuid(), "Memory data role",
            new("Data", "", ["Bidirectional"], [new("requirements", "rev2", null, null, null)]), [pin], null);
        var candidate = RecursiveBlockCodec.Decode(P.DiagramEndpointBindingData.Parser.ParseFrom(RecursiveBlockCodec.Encode(endpoint).ToByteArray()));
        Assert.AreEqual(DiagramEndpointKind.Candidates, candidate.Kind); Assert.IsNull(candidate.Pin);
        Assert.AreEqual("", candidate.Selector!.Protocol);
        Assert.IsTrue(pin.SamePin(candidate.Candidates[0]));
        var chosen = RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(endpoint.Choose(pin)));
        Assert.AreEqual(DiagramEndpointKind.Pin, chosen.Kind); Assert.IsTrue(pin.SamePin(chosen.Pin!));
        Assert.IsNotNull(endpoint.Selector);
        Assert.AreEqual(endpoint.Selector.Role, chosen.Selector!.Role);
        var unknown = RecursiveBlockCodec.Encode(endpoint); unknown.Kind = P.DiagramEndpointKind.DekUnknown;
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(unknown));
        var invalidPin = RecursiveBlockCodec.Encode(endpoint.Choose(pin)); invalidPin.Pin.SheetInstancePath.Clear();
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(invalidPin));
    }
}
