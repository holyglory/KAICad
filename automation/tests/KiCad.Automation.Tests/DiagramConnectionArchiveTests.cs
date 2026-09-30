using System.Collections.Immutable;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

internal sealed record DiagramConnectionFixture(DiagramConnectionArchive Archive,
    ImmutableDictionary<string, ConnectionSelection> Selected, ImmutableDictionary<string, ConnectionSelection> Alternatives)
{
    public static DiagramConnectionFixture Create(Guid? document = null, Guid? owner = null, Guid? firstBlock = null, Guid? secondBlock = null)
    {
        Guid doc = document ?? Guid.NewGuid(), block = owner ?? Guid.NewGuid();
        Guid first = firstBlock ?? Guid.NewGuid(), second = secondBlock ?? Guid.NewGuid();
        var states = new List<ConnectionDesignState>(); var revisions = new List<DiagramConnectionRevision>();
        var histories = new List<DiagramRequirementHistory>();
        var selected = ImmutableDictionary.CreateBuilder<string, ConnectionSelection>();
        var alternatives = ImmutableDictionary.CreateBuilder<string, ConnectionSelection>();
        ConnectionSelection Add(string name, DiagramConnectionKind kind, params ConnectionSelection[] members)
        {
            Guid connection = Guid.NewGuid();
            for (int i = 0; i < 2; ++i)
            {
                Guid state = Guid.NewGuid(), revision = Guid.NewGuid(), requirement = Guid.NewGuid();
                var selection = new ConnectionSelection(connection, state, revision);
                states.Add(new(state, connection, i == 0 ? "Concept" : "Detailed exploration", revision));
                histories.Add(new(new(doc, connection, state), [new(requirement, null,
                    new($"{name}: test-only architectural intent", "", name == "Memory interface" ? "Interface-level budget remains unspecified." : ""), RecursiveBlockFixture.Origin(), [])]));
                bool abstractRoot = name == "Memory interface" && i == 0;
                revisions.Add(new(selection, null, name, abstractRoot ? DiagramConnectionKind.Abstract : kind,
                    [DiagramEndpointBinding.Unknown(first, "Processor side"), DiagramEndpointBinding.Unknown(second, "Memory side")],
                    requirement, abstractRoot ? [] : [.. members], RecursiveBlockFixture.Origin()));
                (i == 0 ? selected : alternatives).Add(name, selection);
            }
            return selected[name];
        }
        var plus = Add("Data+", DiagramConnectionKind.Signal); var minus = Add("Data-", DiagramConnectionKind.Signal);
        var pair = Add("Data pair", DiagramConnectionKind.DifferentialPair, plus, minus);
        var clock = Add("Clock", DiagramConnectionKind.Signal);
        _ = Add("Memory interface", DiagramConnectionKind.Interface, pair, clock);
        return new(new(doc, block, states, revisions, histories), selected.ToImmutable(), alternatives.ToImmutable());
    }
}

[TestClass]
public sealed class DiagramConnectionArchiveTests
{
    [TestMethod]
    public void AnAbstractRelationshipCanSelectADetailedInterfaceWithoutRewritingItsHistory()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive;
        var abstractRoot = f.Selected["Memory interface"]; var detailed = f.Alternatives["Memory interface"];
        Assert.HasCount(1, archive.Walk([abstractRoot]));
        Assert.HasCount(5, archive.Walk([detailed]));
        var selection = archive.Select([abstractRoot], [abstractRoot], detailed, [], RecursiveBlockFixture.Origin());
        Assert.IsTrue(selection.Changed); Assert.AreEqual(detailed, selection.Roots.Single());
        Assert.AreEqual(DiagramConnectionKind.Abstract, archive.Inspect(abstractRoot).Kind);
        Assert.AreEqual("Interface-level budget remains unspecified.", archive.Requirements(detailed).Requirements.Routing);
        Assert.AreEqual("", archive.Requirements(f.Selected["Data+"]).Requirements.Routing); // No blanket budget inheritance.
        Assert.IsTrue(archive.Inspect(detailed).Endpoints.All(e => e.Pin is null));
        var unchanged = archive.Select([detailed], [detailed], detailed, [], RecursiveBlockFixture.Origin());
        Assert.IsFalse(unchanged.Changed); Assert.AreSame(archive, unchanged.Archive);
    }

    [TestMethod]
    public void RefiningOneMemberPreservesItsPairSiblingRootHistoryAndIndependentEndpointState()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive;
        var root = f.Alternatives["Memory interface"]; var pair = f.Selected["Data pair"]; var member = f.Selected["Data+"];
        var before = archive.Inspect(member);
        var source = archive.RequirementHistories.Single(h => h.Scope.DesignStateId == member.StateId);
        var requirements = source.Commit(source.Current.Id, source.StartDraft().Edit(DiagramRequirementField.Routing,
            "Preserve this member's explicit matching preference."), Guid.NewGuid(), RecursiveBlockFixture.Origin("Agent client")).History;
        var processor = before.Endpoints[0] with { Kind = DiagramEndpointKind.Compatible, Selector = new("Data", "", [], []) };
        var memory = before.Endpoints[1].Choose(new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], "D15"));
        var changed = before with { Selection = member with { RevisionId = Guid.NewGuid() }, ParentRevisionId = member.RevisionId,
            RequirementRevisionId = requirements.Current.Id, Endpoints = [processor, memory] };
        var appended = archive.AppendRevision(member.RevisionId, changed, requirements);
        Assert.HasCount(5, appended.Walk([root]));
        Assert.IsNull(appended.Inspect(member).Endpoints[1].Pin);
        var selected = appended.Select([root], [root, pair, member], changed.Selection,
            [Guid.NewGuid(), Guid.NewGuid()], RecursiveBlockFixture.Origin());
        Assert.HasCount(2, selected.CreatedAncestors);
        Assert.AreEqual(archive.Revisions.Length + 3, selected.Archive.Revisions.Length);
        var chosen = selected.Archive.Walk(selected.Roots).Single(s => s.ConnectionId == member.ConnectionId);
        Assert.AreEqual(DiagramEndpointKind.Compatible, selected.Archive.Inspect(chosen).Endpoints[0].Kind);
        Assert.IsNull(selected.Archive.Inspect(chosen).Endpoints[0].Pin);
        Assert.AreEqual("D15", selected.Archive.Inspect(chosen).Endpoints[1].Pin!.Pin);
        Assert.AreSame(archive.Inspect(f.Selected["Data-"]), selected.Archive.Inspect(f.Selected["Data-"]));
        CollectionAssert.AreEqual(archive.Walk([root]).ToArray(), selected.Archive.Walk([root]).ToArray());
        Assert.AreEqual("", selected.Archive.Requirements(member).Requirements.Routing);
        Assert.AreEqual(requirements.Current.Requirements.Routing, selected.Archive.Requirements(chosen).Requirements.Routing);
    }

    [TestMethod]
    public void InvalidPairsCyclesSharedMembersUnknownStatesAndWrongRequirementsAreRejected()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive;
        DiagramConnectionArchive Replace(ConnectionSelection selection, Func<DiagramConnectionRevision, DiagramConnectionRevision> change) =>
            new(archive.DocumentId, archive.OwnerBlockId, archive.States,
                archive.Revisions.Select(r => r.Selection == selection ? change(r) : r), archive.RequirementHistories);
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Data pair"], r => r with { Members = [f.Selected["Data+"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Data pair"], r => r with { Members = [f.Selected["Data+"], f.Selected["Data+"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Data+"], r => r with { Members = [f.Selected["Clock"]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Alternatives["Memory interface"], r => r with { Members = [r.Selection] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Clock"], r => r with { ParentRevisionId = r.Selection.RevisionId }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Clock"], r => r with { Kind = (DiagramConnectionKind)999 }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Clock"], r => r with { Endpoints = [r.Endpoints[0]] }));
        Assert.ThrowsExactly<AutomationException>(() => Replace(f.Selected["Clock"], r => r with { RequirementRevisionId = archive.Inspect(f.Selected["Data+"]).RequirementRevisionId }));
        Assert.ThrowsExactly<AutomationException>(() => archive.Walk([f.Selected["Data pair"], f.Selected["Data+"]]));
        // A connection or member implementation continues the field history of a saved revision of another implementation
        // of the same connection only, and implementations cannot continue each other in a circle.
        DiagramConnectionArchive ContinueWith(Guid stateId, Func<DiagramRequirementRevision, DiagramRequirementRevision> first) => new(archive.DocumentId,
            archive.OwnerBlockId, archive.States, archive.Revisions, archive.RequirementHistories.Select(h => h.Scope.DesignStateId == stateId
                ? new DiagramRequirementHistory(h.Scope, [first(h.Revisions[0])]) : h));
        DiagramConnectionArchive Continue(Dictionary<Guid, Guid> links) => new(archive.DocumentId, archive.OwnerBlockId, archive.States,
            archive.Revisions, archive.RequirementHistories.Select(h => links.TryGetValue(h.Scope.DesignStateId, out var parentId)
                ? new DiagramRequirementHistory(h.Scope, [h.Revisions[0] with { ParentId = parentId }]) : h));
        Guid Text(ConnectionSelection selection) => archive.Inspect(selection).RequirementRevisionId;
        var plus = f.Selected["Data+"]; var plusAlternative = f.Alternatives["Data+"];
        Assert.AreEqual(Text(plus), Continue(new() { [plusAlternative.StateId] = Text(plus) }).RequirementHistories
            .Single(h => h.Scope.DesignStateId == plusAlternative.StateId).Lineage.Single().Id);
        Assert.ThrowsExactly<AutomationException>(() => Continue(new() { [plusAlternative.StateId] = Text(f.Selected["Data-"]) }));
        Assert.ThrowsExactly<AutomationException>(() => Continue(new() { [plusAlternative.StateId] = Guid.NewGuid() }));
        Assert.ThrowsExactly<AutomationException>(() => Continue(new() { [plusAlternative.StateId] = Text(plus), [plus.StateId] = Text(plusAlternative) }));
        // The continuing member's first revision is an unchanged copy of the text it continues and restores nothing.
        Assert.ThrowsExactly<AutomationException>(() => ContinueWith(plusAlternative.StateId, r => r with { ParentId = Text(plus),
            Requirements = r.Requirements with { Routing = "Not the continued text." } }));
        Assert.ThrowsExactly<AutomationException>(() => ContinueWith(plusAlternative.StateId, r => r with { ParentId = Text(plus),
            Restorations = [new(DiagramRequirementField.General, Text(plus))] }));
    }

    [TestMethod]
    public void StaleParentAndRewrittenRequirementHistoryDoNotLosePublishedCandidates()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive;
        var root = f.Alternatives["Memory interface"]; var before = archive.Inspect(root);
        var candidate = before with { Selection = root with { RevisionId = Guid.NewGuid() }, ParentRevisionId = root.RevisionId, Name = "Candidate interface" };
        var appended = archive.AppendRevision(root.RevisionId, candidate);
        Assert.ThrowsExactly<AutomationException>(() => appended.AppendRevision(root.RevisionId, candidate with { Selection = candidate.Selection with { RevisionId = Guid.NewGuid() } }));
        Assert.ThrowsExactly<AutomationException>(() => appended.Select([root], [root, f.Selected["Clock"]], f.Alternatives["Clock"], [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.ThrowsExactly<AutomationException>(() => archive.Select([root], [root, f.Selected["Data+"]], f.Alternatives["Data+"], [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        var saved = archive.RequirementHistories.Single(h => h.Scope.DesignStateId == root.StateId);
        var forged = new DiagramRequirementHistory(saved.Scope, [saved.Current with { Origin = RecursiveBlockFixture.Origin("Forged provenance") }]);
        Assert.ThrowsExactly<AutomationException>(() => archive.AppendRevision(root.RevisionId, candidate, forged));
        Assert.AreEqual("Candidate interface", appended.Inspect(candidate.Selection).Name);
        Assert.AreEqual("Memory interface", appended.Inspect(root).Name);
    }
}
