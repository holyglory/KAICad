using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ComponentReferenceRetentionTests
{
    private static (EngineeringDesign Design, ComponentKnowledgeLibrary Library) Fixture()
    {
        var (design, library) = EngineeringDesignXmlTests.Fixture();
        var circuit = design.Circuit; var root = circuit.SheetInstances[0];
        var secondDefinition = circuit.Sheets[0].Components[0] with { Id = Guid.NewGuid() };
        circuit = circuit with { SheetInstances = [root],
            Sheets = [circuit.Sheets[0] with { Components = [circuit.Sheets[0].Components[0], secondDefinition] }],
            Components = [circuit.Components[0], circuit.Components[1] with { DefinitionId = secondDefinition.Id, SheetInstanceId = root.Id }] };
        var concrete = design.Structure.Statements.Single(s => s.Connection is not null);
        var componentNote = new EngineeringStatement(Guid.NewGuid(), circuit.Components[0].Id, EngineeringStatementRole.Intent,
            GuidanceStrength.Requirement, "Keep this device cool.\r\nDo not infer an operating limit.", null, [],
            [new("datasheet", "rev-C", 7, "Operating conditions", "A\tB")]);
        var reverse = concrete with { Id = Guid.NewGuid(), Connection = new(concrete.Connection!.Second, concrete.Connection.First) };
        var netNote = componentNote with { Id = Guid.NewGuid(), TargetId = circuit.Nets[0].Id, Text = "Retain the supply requirement." };
        design = design with { Circuit = circuit, Structure = design.Structure with
            { Statements = [.. design.Structure.Statements, componentNote, reverse, netNote] } };
        design.Validate([library]); return (design, library);
    }

    private static Circuit Without(Circuit circuit, Guid component, bool removeNet = true)
    {
        Guid definition = circuit.Components.Single(c => c.Id == component).DefinitionId;
        return circuit with
        {
            Components = circuit.Components.Where(c => c.Id != component).ToArray(),
            Sheets = circuit.Sheets.Select(s => s with { Components = s.Components.Where(c => c.Id != definition).ToArray() }).ToArray(),
            Symbols = circuit.Symbols.Where(s => s.ComponentId != component).ToArray(),
            Nets = removeNet ? [] : circuit.Nets.Select(n => n with { Pins = n.Pins.Where(p => p.ComponentId != component).ToArray() }).ToArray()
        };
    }

    private static EngineeringDesign Remove(EngineeringDesign design, ComponentKnowledgeLibrary library)
    {
        Guid first = design.Circuit.Components[0].Id, second = design.Circuit.Components[1].Id;
        return ComponentReferenceRetention.Retain(design, Without(design.Circuit, first),
            [new(new(first), ComponentReferenceChangeKind.Removed, "Native component was removed.", [new(second)])], [library],
            [new(design.Circuit.Nets[0].Id, NetBindingChangeKind.Removed, "Its electrical realization disappeared.", [])]);
    }

    [TestMethod]
    public void ComponentAndNetRemovalRetainEveryInstructionWithoutGuessingPins()
    {
        var (before, library) = Fixture(); string original = EngineeringDesignXml.Write(before, [library]);
        var after = Remove(before, library);
        Assert.IsTrue(after.HasUnresolvedComponentReferences);
        Assert.AreEqual(4, after.Structure.UnresolvedComponentReferences!.Count);
        Assert.AreEqual(2, after.Structure.UnresolvedNetBindings!.Count);
        Assert.AreEqual(1, after.UnresolvedGuidanceBindings!.Count);
        Assert.IsEmpty(after.Structure.Blocks[0].ComponentIds);
        CollectionAssert.AreEqual(before.Structure.Statements.ToArray(), after.Structure.Statements.ToArray());
        Assert.AreEqual(ComponentKnowledgeXml.WriteBinding(before.ComponentBindings[0], library),
            ComponentKnowledgeXml.WriteBinding(after.ComponentBindings[0], library));
        Assert.IsEmpty(after.Validate([library]), "Retained guidance must not be reported as attached to a live component.");
        foreach (var reference in after.Structure.UnresolvedComponentReferences.Where(r => r.FormerTarget.PinNumber is not null))
            Assert.IsEmpty(reference.CandidateTargets, "A replacement component does not imply a replacement pin.");
        string xml = EngineeringDesignXml.Write(after, [library]);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(EngineeringDesignXml.Read(xml, [library]), [library]));
        Assert.AreEqual(xml, EngineeringDesignXml.Write(ComponentReferenceRetention.Retain(after, after.Circuit, [], [library]), [library]));
        Assert.AreEqual(original, EngineeringDesignXml.Write(before, [library]));
    }

    [TestMethod]
    public void PinRenumberingRetainsBothEndsUntilExplicitExactResolution()
    {
        var (before, library) = Fixture();
        var circuit = before.Circuit with
        {
            Parts = before.Circuit.Parts.Select(p => p with { Pins = p.Pins.Select(pin => pin.Number == "8" ? pin with { Number = "9" } : pin).ToArray() }).ToArray(),
            Nets = before.Circuit.Nets.Select(n => n with { Pins = n.Pins.Select(p => p with { Pin = "9" }).ToArray() }).ToArray()
        };
        var changes = circuit.Components.Select(c => new ComponentReferenceChange(new(c.Id, "8"),
            ComponentReferenceChangeKind.Reidentified, "Exact native pin identity acquired a new number.", [new(c.Id, "9")])).ToArray();
        var pending = ComponentReferenceRetention.Retain(before, circuit, changes, [library]);
        Assert.AreEqual(4, pending.Structure.UnresolvedComponentReferences!.Count);
        Assert.IsNull(pending.UnresolvedGuidanceBindings);
        foreach (var reference in pending.Structure.UnresolvedComponentReferences.ToArray())
            pending = ComponentReferenceRetention.Resolve(pending, reference.OwnerId, reference.Slot,
                reference.FormerTarget, reference.CandidateTargets.Single(), [library]);
        Assert.IsFalse(pending.HasUnresolvedComponentReferences);
        Assert.IsTrue(pending.Structure.Statements.Where(s => s.Connection is not null)
            .All(s => s.Connection!.First.Pin == "9" && s.Connection.Second.Pin == "9"));
        CollectionAssert.AreEqual(before.Structure.Statements.Select(s => (s.Text, s.Role, s.Strength, s.Sources, s.DerivedFrom)).ToArray(),
            pending.Structure.Statements.Select(s => (s.Text, s.Role, s.Strength, s.Sources, s.DerivedFrom)).ToArray());
    }

    [TestMethod]
    public void ExplicitResolutionChangesOnlyTheChosenOwnerAndPreservesClassGuidance()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        Guid target = pending.Circuit.Components.Single().Id;
        foreach (var reference in pending.Structure.UnresolvedComponentReferences!.ToArray())
        {
            int count = pending.Structure.UnresolvedComponentReferences!.Count;
            var selected = new ComponentReferenceTarget(target, reference.FormerTarget.PinNumber is null ? null : "1");
            pending = ComponentReferenceRetention.Resolve(pending, reference.OwnerId, reference.Slot, reference.FormerTarget, selected, [library]);
            Assert.AreEqual(count - 1, pending.Structure.UnresolvedComponentReferences?.Count ?? 0);
        }
        pending = ComponentReferenceRetention.ResolveGuidance(pending, before.Circuit.Components[0].Id, target, [library]);
        Assert.IsFalse(pending.HasUnresolvedComponentReferences);
        Assert.AreEqual(target, pending.ComponentBindings.Single().ComponentInstanceId);
        CollectionAssert.AreEqual(before.ComponentBindings[0].Guidance.ToArray(), pending.ComponentBindings[0].Guidance.ToArray());
        Assert.AreEqual(1, pending.Validate([library]).Count);
        Assert.AreEqual(2, pending.Structure.UnresolvedNetBindings!.Count, "Component resolution cannot silently resolve unrelated net requirements.");
    }

    [TestMethod]
    public void InvalidOwnersSlotsCandidatesAndTypeCollisionsDoNotBypassValidation()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        var records = pending.Structure.UnresolvedComponentReferences!.ToArray();
        var first = records[0];
        foreach (var invalid in new[]
        {
            first with { OwnerId = Guid.NewGuid() }, first with { Reason = " " }, first with { Slot = (ComponentReferenceSlot)99 },
            first with { FormerTarget = new(pending.Circuit.Parts[0].Id) },
            first with { CandidateTargets = [new(Guid.NewGuid())] },
            first with { Change = (ComponentReferenceChangeKind)99 }
        })
            Assert.ThrowsExactly<AutomationException>(() => (pending with { Structure = pending.Structure with
                { UnresolvedComponentReferences = [invalid, .. records.Skip(1)] } }).Validate([library]));
        Assert.ThrowsExactly<AutomationException>(() => (pending with { Structure = pending.Structure with
            { UnresolvedComponentReferences = [.. records, first] } }).Validate([library]));
        Assert.ThrowsExactly<AutomationException>(() => (pending with { UnresolvedGuidanceBindings = null }).Validate([library]));
        Assert.ThrowsExactly<AutomationException>(() => ComponentReferenceRetention.Retain(before, Without(before.Circuit, before.Circuit.Components[0].Id), [], [library]));
        Assert.ThrowsExactly<AutomationException>(() => ComponentReferenceRetention.ResolveGuidance(pending,
            before.Circuit.Components[0].Id, Guid.NewGuid(), [library]));
        var pin = records.First(r => r.Slot == ComponentReferenceSlot.FirstPin);
        Assert.ThrowsExactly<AutomationException>(() => ComponentReferenceRetention.Resolve(pending, pin.OwnerId, pin.Slot,
            pin.FormerTarget, new(pending.Circuit.Components[0].Id, "8"), [library])); // would connect a pin to itself
    }

    [TestMethod]
    public void FurtherCandidateRemovalNeedsAnExplicitChangeAndRemainsIdempotent()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        Guid second = pending.Circuit.Components.Single().Id;
        var empty = Without(pending.Circuit, second);
        Assert.ThrowsExactly<AutomationException>(() => ComponentReferenceRetention.Retain(pending, empty, [], [library]));
        var retained = ComponentReferenceRetention.Retain(pending, empty,
            [new(new(second), ComponentReferenceChangeKind.Removed, "The candidate was also removed.", [])], [library]);
        Assert.IsTrue(retained.HasUnresolvedComponentReferences);
        Assert.IsTrue(retained.Structure.UnresolvedComponentReferences!.All(r => r.CandidateTargets.Count == 0));
        Assert.IsEmpty(retained.UnresolvedGuidanceBindings![0].CandidateComponentIds);
        string xml = EngineeringDesignXml.Write(retained, [library]);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(ComponentReferenceRetention.Retain(retained, empty, [], [library]), [library]));
    }

    [TestMethod]
    public void CompetingGuidanceAndMalformedRetainedAssignmentsCannotOverwriteOwners()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        Guid former = before.Circuit.Components[0].Id, live = pending.Circuit.Components.Single().Id;
        var inheritedOnly = pending.ComponentBindings[0] with { ComponentInstanceId = live, Guidance = [] };
        var competing = pending with { ComponentBindings = [.. pending.ComponentBindings, inheritedOnly] };
        competing.Validate([library]);
        string unchanged = EngineeringDesignXml.Write(competing, [library]);
        Assert.ThrowsExactly<AutomationException>(() => ComponentReferenceRetention.ResolveGuidance(competing, former, live, [library]));
        Assert.AreEqual(unchanged, EngineeringDesignXml.Write(competing, [library]));
        var original = pending.UnresolvedGuidanceBindings![0];
        foreach (var broken in new[] { original with { Reason = "" }, original with { ComponentInstanceId = Guid.NewGuid() },
            original with { CandidateComponentIds = [live, live] }, original with { CandidateComponentIds = [Guid.NewGuid()] },
            original with { Change = (ComponentReferenceChangeKind)99 } })
            Assert.ThrowsExactly<AutomationException>(() => (pending with { UnresolvedGuidanceBindings = [broken] }).Validate([library]));
        Assert.ThrowsExactly<AutomationException>(() => (pending with { UnresolvedGuidanceBindings = [original, original] }).Validate([library]));
    }

    [TestMethod]
    public void RecoveryStoreRetainsAllPendingReferencesWithoutClaimingNativeReconciliation()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        string scratch = Directory.CreateTempSubdirectory("kicad-component-recovery-").FullName;
        try
        {
            var initial = DesignRecoveryStoreTests.Fixture();
            var state = initial with { Baseline = initial.Baseline with { Engineering = pending }, KnowledgeLibraries = [library],
                DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(EngineeringDesignXml.Write(pending, [library])) };
            var store = new DesignRecoveryStore(Path.Combine(scratch, "recovery.json"));
            var saved = store.Save(state, null); var reopened = store.Read()!;
            Assert.AreEqual(saved.RevisionToken, reopened.RevisionToken);
            Assert.AreEqual(EngineeringDesignXml.Write(pending, [library]), EngineeringDesignXml.Write(reopened.State.Baseline.Engineering, [library]));
            Assert.IsTrue(reopened.State.Baseline.Engineering.HasUnresolvedComponentReferences);
            Assert.AreEqual(saved.RevisionToken, store.Save(reopened.State, reopened.RevisionToken).RevisionToken);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [TestMethod]
    public void XmlRejectsMalformedRecordsAndUnchangedDocumentsAcquireNoEmptySections()
    {
        var (before, library) = Fixture(); string original = EngineeringDesignXml.Write(before, [library]);
        Assert.IsFalse(original.Contains("unresolved-component-references", StringComparison.Ordinal));
        Assert.IsFalse(original.Contains("unresolved-guidance-bindings", StringComparison.Ordinal));
        Assert.AreEqual(original, EngineeringDesignXml.Write(ComponentReferenceRetention.Retain(before, before.Circuit, [], [library]), [library]));
        string xml = EngineeringDesignXml.Write(Remove(before, library), [library]);
        foreach (string broken in new[] { xml.Replace("FirstPin", "FutureSlot", StringComparison.Ordinal),
            xml.Replace("change=\"Removed\"", "change=\"FutureChange\"", StringComparison.Ordinal),
            xml.Replace("former component=", "former unknown=\"unsupported\" component=", StringComparison.Ordinal) })
            Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Read(broken, [library]));
        var result = new KnowledgeTools().ValidateDesign(xml, [ComponentKnowledgeXml.WriteLibrary(library)], default);
        Assert.IsTrue(result.ModelValid); Assert.IsFalse(result.ComponentReferencesResolved); Assert.IsFalse(result.NetBindingsResolved);
        Assert.IsEmpty(result.Guidance!); Assert.AreEqual(4, result.UnresolvedComponentReferences!.Count);
        Assert.AreEqual(1, result.UnresolvedGuidanceBindings!.Count);
    }

    [TestMethod]
    public async Task NormalStdioValidationReportsPendingReferencesRatherThanResolvedGuidance()
    {
        var (before, library) = Fixture(); var pending = Remove(before, library);
        string scratch = Directory.CreateTempSubdirectory("kicad-component-reference-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(scratch, "state"), Path.Combine(scratch, "mcp.log"), CancellationToken.None);
            var result = await mcp.Tool("kicad_engineering_design_validate", new
                { designXml = EngineeringDesignXml.Write(pending, [library]), knowledgeLibraryXml = new[] { ComponentKnowledgeXml.WriteLibrary(library) } });
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean());
            var content = result.GetProperty("structuredContent");
            Assert.IsTrue(content.GetProperty("modelValid").GetBoolean());
            Assert.IsFalse(content.GetProperty("componentReferencesResolved").GetBoolean());
            Assert.AreEqual(4, content.GetProperty("unresolvedComponentReferences").GetArrayLength());
            Assert.AreEqual(0, content.GetProperty("guidance").EnumerateObject().Count());
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
