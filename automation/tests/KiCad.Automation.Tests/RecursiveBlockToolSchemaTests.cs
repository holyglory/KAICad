using System.Text.Json;
using KiCad.Automation.Model;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockToolSchemaTests
{
    [TestMethod]
    public void OptionalAnnotationsDoNotBreakTypedMcpSchemaGeneration()
    {
        var tool = McpServerTool.Create((BlockLocalDiagram diagram) => true);
        var schema = tool.ProtocolTool.InputSchema;
        string text = schema.GetRawText();
        StringAssert.Contains(text, "interfaces"); StringAssert.Contains(text, "connections");
        StringAssert.Contains(text, "annotations"); StringAssert.Contains(text, "targetId");
        var defaults = new BlockLocalDiagram([], []);
        Assert.IsFalse(defaults.Annotations.IsDefault);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(defaults, options);
        Assert.IsTrue(defaults.SameContents(JsonSerializer.Deserialize<BlockLocalDiagram>(json, options)!));
        // Existing XML semantics permit omitted optional annotations; JSON input
        // preserves that absence without inventing a note or an electrical object.
        var omitted = JsonSerializer.Deserialize<BlockLocalDiagram>("{\"interfaces\":[],\"connections\":[]}", options)!;
        omitted.Validate(); Assert.IsEmpty(omitted.Notes);
        Assert.IsNull(omitted.Presentation); Assert.IsEmpty(omitted.Realizations);
    }

    [TestMethod]
    public void SchemaTwoRecordsReachableFromAgentToolsKeepTypedMcpSchemaGeneration()
    {
        // Contract rbg-v2 section 2.5: every record reachable from a tool parameter describes itself
        // without enumerating a default collection, and omitted collections read as empty.
        var tool = McpServerTool.Create((BlockLocalDiagram diagram, ProposedConnection connection, ProposedBlock block) => true);
        string text = tool.ProtocolTool.InputSchema.GetRawText();
        foreach (string member in new[] { "presentation", "interfaceRealizations", "domain", "direction", "realization", "segments", "joins",
            "frame", "ports", "routes", "waypoints", "targets" })
            StringAssert.Contains(text, "\"" + member + "\"", member);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var view = JsonSerializer.Deserialize<DiagramPresentationView>("{\"blocks\":[]}", options)!;
        view.Validate(); Assert.IsTrue(view.IsEmpty);
        var realization = JsonSerializer.Deserialize<InterconnectRealization>("{\"state\":0,\"unresolvedReason\":\"Not stated yet.\"}", options)!;
        realization.Validate(); Assert.IsEmpty(realization.SegmentList); Assert.IsEmpty(realization.JoinList); Assert.IsEmpty(realization.SourceList);
        var record = JsonSerializer.Deserialize<InterfaceRealization>(
            "{\"interfaceId\":\"" + Guid.NewGuid() + "\",\"state\":0,\"unresolvedReason\":\"Not stated yet.\"}", options)!;
        record.Validate(); Assert.IsEmpty(record.TargetList);
        // An agent's proposal carries the level it refines with its drawn layout and notes (the native connection-details journey
        // publishes one); the whole local diagram comes back exactly.
        Guid block = Guid.NewGuid(), port = Guid.NewGuid(), link = Guid.NewGuid();
        var drawn = new BlockLocalDiagram([new(port, "DC input", "")], [new(link, Guid.NewGuid(), Guid.NewGuid())],
            [new(Guid.NewGuid(), DiagramAnnotationRole.Comment, "Feed the CPU from the rail.", new(DiagramAnnotationTargetKind.Connection, link), null, [],
                RecursiveBlockFixture.Origin())],
            new DiagramPresentationView([new(block, new(140, 130, 240, 140))], [new(Guid.NewGuid(), port, DiagramPortSide.Left, 110)],
                [new(link, 1, [new(510, 250), new(510, 275)])], new(100, 90, 780, 310)), default);
        var back = JsonSerializer.Deserialize<BlockLocalDiagram>(JsonSerializer.Serialize(drawn, options), options)!;
        Assert.IsTrue(drawn.SameContents(back), "A drawn level survives the agent's JSON exactly.");
        // The level-edit command gained a signal list (Round A3); the level-edit tools deferred in rbg-v2 section 13 will take it
        // as a parameter, so it follows the same rules now: it describes itself, an omitted list reads as empty, the old six-value
        // constructor still builds it, and its contents compare in order.
        string command = McpServerTool.Create((LevelEditCommand edit) => true).ProtocolTool.InputSchema.GetRawText();
        StringAssert.Contains(command, "\"memberIds\"");
        var origin = RecursiveBlockFixture.Origin();
        var removal = new LevelEditCommand(LevelEditCommandKind.RemoveConnection, null, link, null, false, origin);
        Assert.IsTrue(removal.SameContents(JsonSerializer.Deserialize<LevelEditCommand>(JsonSerializer.Serialize(removal, options), options)));
        var omittedMembers = JsonSerializer.Deserialize<LevelEditCommand>(JsonSerializer.Serialize(removal, options).Replace(",\"memberIds\":[]", ""), options)!;
        Assert.IsTrue(omittedMembers.MemberIds.IsDefault); Assert.IsEmpty(omittedMembers.MemberIdList); Assert.IsTrue(removal.SameContents(omittedMembers));
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var signals = new LevelEditCommand(LevelEditCommandKind.RemoveConnectionMembers, null, link, null, false, origin, [first, second]);
        Assert.IsTrue(signals.SameContents(signals with { MemberIds = [first, second] }), "The same signals in the same order are the same removal.");
        Assert.IsFalse(signals.SameContents(signals with { MemberIds = [second, first] }));
        Assert.IsFalse(signals.SameContents(removal));
    }
}
