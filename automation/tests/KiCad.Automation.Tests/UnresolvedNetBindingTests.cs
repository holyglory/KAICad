using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UnresolvedNetBindingTests
{
    internal static (Circuit Before, Circuit After, StructuralDiagram Original, StructuralDiagram Pending) Split()
    {
        var (before, original) = StructuralDiagramTests.Fixture();
        var statement = original.Statements[0] with
        {
            TargetId = before.Nets[0].Id, Text = "  Preserve supply limits\r\nwithout guessing.\t",
            Sources = [new("supply-datasheet", "rev B", 7, "limits", "industrial")]
        };
        original = original with { Statements = [statement, original.Statements[1]] };
        var after = before with { Nets = [new(Guid.NewGuid(), "left", [before.Nets[0].Pins[0]]),
            new(Guid.NewGuid(), "right", [before.Nets[0].Pins[1]])] };
        var pending = original.RetainUnresolvedNet(before, after, before.Nets[0].Id,
            NetBindingChangeKind.Split, "Native split\r\nneeds explicit requirement binding.", after.Nets.Select(n => n.Id).ToArray());
        return (before, after, original, pending);
    }

    [TestMethod]
    public void SplitRetainsRequirementsAndCandidatesWithoutRealizingThem()
    {
        var f = Split();
        f.Pending.Validate(f.After);
        Assert.IsTrue(f.Pending.HasUnresolvedNetBindings);
        Assert.AreEqual(2, f.Pending.UnresolvedNetBindings!.Count);
        Assert.IsEmpty(f.Pending.Connections[0].NetIds);
        Assert.AreEqual(f.Original.Statements[0], f.Pending.Statements[0]);
        Assert.AreEqual(f.Original.Statements[1], f.Pending.Statements[1]);
        var roundTrip = StructuralDiagramXml.Read(StructuralDiagramXml.Write(f.Pending, f.After), f.After);
        Assert.AreEqual(StructuralDiagramXml.Write(f.Pending, f.After), StructuralDiagramXml.Write(roundTrip, f.After));
        Assert.AreEqual(f.Original.Statements[0].Text, roundTrip.Statements.Single(s => s.Id == f.Original.Statements[0].Id).Text);
        Assert.AreEqual(f.Pending.UnresolvedNetBindings[0].Reason, roundTrip.UnresolvedNetBindings![0].Reason);
        Assert.AreEqual(f.Before.Nets[0].Id, roundTrip.Statements.Single(s => s.Id == f.Original.Statements[0].Id).TargetId);
        CollectionAssert.AreEquivalent(f.After.Nets.Select(n => n.Id).ToArray(), roundTrip.UnresolvedNetBindings[0].CandidateNetIds.ToArray());
        Assert.AreEqual(StructuralDiagramXml.Write(f.Pending, f.After), StructuralDiagramXml.Write(f.Pending with
        { UnresolvedNetBindings = f.Pending.UnresolvedNetBindings.Reverse().Select(b => b with { CandidateNetIds = b.CandidateNetIds.Reverse().ToArray() }).ToArray() }, f.After));
    }

    [TestMethod]
    public void ExplicitResolutionAffectsOnlyOneOwnerAndDoesNotRequireACandidateGuess()
    {
        var f = Split();
        var first = f.Pending.ResolveUnresolvedNet(f.After, f.Original.Statements[0].Id, f.Before.Nets[0].Id, f.After.Nets[1].Id);
        Assert.AreEqual(f.After.Nets[1].Id, first.Statements[0].TargetId);
        Assert.AreEqual(f.Original.Statements[0].Text, first.Statements[0].Text);
        Assert.IsTrue(first.HasUnresolvedNetBindings);
        Assert.IsEmpty(first.Connections[0].NetIds);
        var final = first.ResolveUnresolvedNet(f.After, f.Original.Connections[0].Id, f.Before.Nets[0].Id, f.After.Nets[0].Id);
        Assert.IsFalse(final.HasUnresolvedNetBindings);
        Assert.AreEqual(f.After.Nets[0].Id, final.Connections[0].NetIds.Single());
        Assert.ThrowsExactly<AutomationException>(() => final.ResolveUnresolvedNet(f.After, f.Original.Connections[0].Id,
            f.Before.Nets[0].Id, f.After.Nets[0].Id));
        Assert.ThrowsExactly<AutomationException>(() => f.Pending.ResolveUnresolvedNet(f.After, f.Original.Statements[0].Id,
            f.Before.Nets[0].Id, Guid.NewGuid()));
    }

    [TestMethod]
    public void MergeRetainsEveryFormerOwnerInOneValidatedBatch()
    {
        var f = Split();
        var left = f.After.Nets[0]; var right = f.After.Nets[1];
        var original = f.Original with
        {
            Connections = [f.Original.Connections[0] with { NetIds = [left.Id, right.Id] }],
            Statements = [f.Original.Statements[0] with { TargetId = left.Id },
                f.Original.Statements[1] with { TargetId = right.Id }]
        };
        var after = f.Before with { Nets = [new(Guid.NewGuid(), "combined", f.Before.Nets[0].Pins)] };
        var pending = original.RetainUnresolvedNets(f.After, after,
            [new(left.Id, NetBindingChangeKind.Merged, "Left net merged", [after.Nets[0].Id]),
             new(right.Id, NetBindingChangeKind.Merged, "Right net merged", [after.Nets[0].Id])]);
        Assert.AreEqual(4, pending.UnresolvedNetBindings!.Count);
        Assert.IsEmpty(pending.Connections[0].NetIds);
        var read = StructuralDiagramXml.Read(StructuralDiagramXml.Write(pending, after), after);
        Assert.AreEqual(4, read.UnresolvedNetBindings!.Count);
        Assert.AreEqual(left.Id, read.Statements.Single(s => s.Id == original.Statements[0].Id).TargetId);
        Assert.AreEqual(right.Id, read.Statements.Single(s => s.Id == original.Statements[1].Id).TargetId);
    }

    [TestMethod]
    public void RemovedNetNeedsNoInventedSuccessorAndUndeclaredDanglingReferencesStillFail()
    {
        var f = Split(); var removed = f.Before with { Nets = [] };
        var pending = f.Original.RetainUnresolvedNet(f.Before, removed, f.Before.Nets[0].Id,
            NetBindingChangeKind.Removed, "Removed without a replacement", []);
        pending.Validate(removed);
        Assert.IsTrue(pending.UnresolvedNetBindings!.All(b => b.CandidateNetIds.Count == 0));
        Assert.ThrowsExactly<AutomationException>(() => (pending with { UnresolvedNetBindings = null }).Validate(removed));
        Assert.ThrowsExactly<AutomationException>(() => (f.Pending with
        { Connections = [f.Pending.Connections[0] with { NetIds = [f.Before.Nets[0].Id] }] }).Validate(f.After));
    }

    [TestMethod]
    public void MalformedDuplicateOrOrphanUnresolvedRecordsDoNotBypassValidation()
    {
        var f = Split(); var binding = f.Pending.UnresolvedNetBindings![0];
        foreach (var malformed in new[]
        {
            binding with { OwnerId = Guid.NewGuid() }, binding with { FormerNetId = Guid.Empty },
            binding with { FormerNetId = f.Before.Components[0].Id }, binding with { Reason = " " },
            binding with { Change = (NetBindingChangeKind)100 },
            binding with { CandidateNetIds = [Guid.NewGuid()] },
            binding with { CandidateNetIds = [f.After.Nets[0].Id, f.After.Nets[0].Id] }
        })
            Assert.ThrowsExactly<AutomationException>(() => (f.Pending with
            { UnresolvedNetBindings = f.Pending.UnresolvedNetBindings.Select(b => b == binding ? malformed : b).ToArray() }).Validate(f.After));
        Assert.ThrowsExactly<AutomationException>(() => (f.Pending with
        { UnresolvedNetBindings = [.. f.Pending.UnresolvedNetBindings, binding] }).Validate(f.After));
    }

    [TestMethod]
    public void EngineeringAndRecoveryRoundTripsExposeUnresolvedBindingsToTools()
    {
        var f = Split(); var design = new EngineeringDesign(f.After, f.Pending, [], []);
        string xml = EngineeringDesignXml.Write(design, []);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(EngineeringDesignXml.Read(xml, []), []));
        var result = new KnowledgeTools().ValidateDesign(xml, [], CancellationToken.None);
        Assert.IsTrue(result.ModelValid);
        Assert.IsFalse(result.NetBindingsResolved);
        Assert.AreEqual(2, result.UnresolvedNetBindings!.Count);
        string root = Directory.CreateTempSubdirectory("net-binding-recovery-").FullName;
        try
        {
            var initial = DesignRecoveryStoreTests.Fixture();
            var state = initial with { Baseline = initial.Baseline with { Engineering = design },
                KnowledgeLibraries = [], DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(xml) };
            var store = new DesignRecoveryStore(Path.Combine(root, "recovery.json"));
            var saved = store.Save(state, null);
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            Assert.IsTrue(store.Read()!.State.Baseline.Engineering.Structure.HasUnresolvedNetBindings);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExistingXmlHasNoNewEmptyGroupOrFileChurn()
    {
        var (circuit, structure) = StructuralDiagramTests.Fixture();
        string xml = StructuralDiagramXml.Write(structure, circuit);
        Assert.IsFalse(xml.Contains("unresolved-net-bindings", StringComparison.Ordinal));
        Assert.AreEqual(xml, StructuralDiagramXml.Write(structure with { UnresolvedNetBindings = [] }, circuit));
        Assert.AreEqual(xml, StructuralDiagramXml.Write(StructuralDiagramXml.Read(xml, circuit), circuit));
    }
}
