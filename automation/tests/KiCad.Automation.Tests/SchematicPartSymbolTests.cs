using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicPartSymbolTests
{
    [TestMethod]
    public void CreatesAnUnusedDeclaredPartWithSeparateOwnedAndPlacedPinIdentities()
    {
        var (declared, library) = Fixture();
        var source = declared.PartSymbols!.Single();
        var manufacturer = source.Symbol.Definition.DescriptionField.Clone();
        manufacturer.Name = "Manufacturer";
        source.Symbol.Definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(manufacturer) });
        var baseline = declared with { PartSymbols = null, Engineering = declared.Engineering with
            { Circuit = declared.Engineering.Circuit with
                { Parts = declared.Engineering.Circuit.Parts.Where(p => p.Id != source.PartId).ToArray() } } };
        var additions = SchematicNativeCreationProjectionTests.AddComponent(baseline);
        var oldDefinitions = baseline.Engineering.Circuit.Sheets.SelectMany(s => s.Components).Select(c => c.Id).ToHashSet();
        var desired = declared with { Engineering = additions with { Circuit = additions.Circuit with
        {
            Parts = declared.Engineering.Circuit.Parts,
            Sheets = additions.Circuit.Sheets.Select(s => s with { Components = s.Components.Select(c =>
                oldDefinitions.Contains(c.Id) ? c : c with { PartId = source.PartId }).ToArray() }).ToArray()
        } } };
        string original = SchematicDesignXml.Write(baseline, [library]);
        var first = SchematicNativeCreationProjection.Project(baseline, desired, [library]);
        var second = SchematicNativeCreationProjection.Project(baseline, desired, [library]);
        Assert.AreEqual(SchematicDesignXml.Write(first.Candidate, [library]), SchematicDesignXml.Write(second.Candidate, [library]));
        Assert.AreEqual(original, SchematicDesignXml.Write(baseline, [library]));
        Assert.AreEqual(source.Symbol, first.Candidate.PartSymbols!.Single().Symbol);
        var reordered = source with { Symbol = source.Symbol.Clone() };
        var children = reordered.Symbol.Definition.Items.Reverse().ToArray();
        reordered.Symbol.Definition.Items.Clear(); reordered.Symbol.Definition.Items.Add(children);
        var permuted = SchematicNativeCreationProjection.Project(baseline, desired with { PartSymbols = [reordered] }, [library]);
        var nativeIds = first.Candidate.SymbolBindings.Where(b => first.CreatedOccurrences.Contains(b.SymbolOccurrenceId))
            .Select(b => b.NativeObjectId.ToString("D")).ToHashSet();
        var created = first.Candidate.Schematic.Instances.SelectMany(s => s.Items)
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Where(s => nativeIds.Contains(s.Id.Value)).ToArray();
        Assert.IsNotEmpty(created);
        foreach (var symbol in created)
        {
            Assert.IsTrue(symbol.Definition.Items[0].Item.Is(SchematicField.Descriptor),
                "Native library fields precede pins even when the declaration listed them last.");
            Assert.AreEqual(0, symbol.Definition.Items[0].Unit.Unit);
            Assert.AreEqual(0, symbol.Definition.Items[0].BodyStyle.Style);
            var pinOrder = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor))
                .Select(c => c.Item.Unpack<SchematicPin>().Id.Value).ToArray();
            CollectionAssert.AreEqual(pinOrder.Order(StringComparer.Ordinal).ToArray(), pinOrder);
            var permutedSymbol = permuted.Candidate.Schematic.Instances.SelectMany(s => s.Items)
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .First(s => s.Id.Equals(symbol.Id) && s.Path.Equals(symbol.Path));
            Assert.AreEqual(symbol, permutedSymbol, "Pin enumeration must not allocate different placed identities.");
            Assert.AreEqual(source.LibraryId, symbol.LibraryId);
            Assert.AreEqual(source.Symbol.Definition.Id, symbol.Definition.Id);
            Assert.AreEqual(source.Symbol.CacheKey, symbol.LibName);
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                if ((child.BodyStyle?.Style ?? 0) == 0 || child.BodyStyle!.Style == source.BodyStyle)
                { Assert.IsNotNull(pin.LibraryPinId); Assert.AreNotEqual(pin.Id, pin.LibraryPinId); }
                else Assert.IsNull(pin.LibraryPinId);
            }
        }
        Assert.AreEqual("new_part_requires_library_definition", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicNativeCreationProjection.Project(baseline, desired with { PartSymbols = null }, [library])).Code);
        var conflicting = baseline with { Schematic = baseline.Schematic.Clone() };
        foreach (var screen in conflicting.Schematic.Instances)
        { var entry = source.Symbol.Clone(); entry.ShowPinNumbers = !entry.ShowPinNumbers; screen.CachedSymbols.Add(entry); }
        Assert.AreEqual("created_symbol_cache_conflict", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicNativeCreationProjection.Project(conflicting, desired, [library])).Code);
    }

    [TestMethod]
    public void UnplacedDefinitionRoundTripsWithoutInventingAComponentOrLosingItsThreeNames()
    {
        var (design, library) = Fixture();
        string native = SchematicDataXml.Write(design.Schematic);
        string xml = SchematicDesignXml.Write(design, [library]);
        var read = SchematicDesignXml.Read(xml, [library]);
        Assert.AreEqual(xml, SchematicDesignXml.Write(read, [library]));
        var source = read.PartSymbols!.Single();
        Assert.AreEqual("External", source.LibraryId.LibraryNickname);
        Assert.AreEqual("RequestedPart", source.LibraryId.EntryName);
        Assert.AreEqual("LocalDefinition", source.Symbol.Definition.Id.EntryName);
        Assert.AreEqual("CacheAlias", source.Symbol.CacheKey);
        Assert.AreEqual(design.PartSymbols!.Single().Symbol, source.Symbol);
        Assert.AreEqual(native, SchematicDataXml.Write(read.Schematic));
        Assert.AreEqual(design.SymbolBindings.Count, read.SymbolBindings.Count);
        Assert.AreEqual(design.Engineering.Circuit.Components.Count, read.Engineering.Circuit.Components.Count);
        Assert.IsTrue(SchematicDesignBindings.Inspect(read, [library]).IdentitiesResolved);
        Assert.IsFalse(xml.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void LegacyDocumentsRemainUnchangedAndEmptyDeclarationsDoNotChurnXml()
    {
        var (design, library) = SchematicDesignTests.Fixture();
        string xml = SchematicDesignXml.Write(design, [library]);
        Assert.IsNull(SchematicDesignXml.Read(xml, [library]).PartSymbols);
        Assert.AreEqual(xml, SchematicDesignXml.Write(design with { PartSymbols = [] }, [library]));
    }

    [TestMethod]
    public void InvalidCoverageOwnershipAndSelectionsRejectWithoutChangingInputs()
    {
        foreach (string problem in new[] { "unknown-part", "duplicate-part", "unit-count", "missing-pin", "pin-name",
            "pin-unit", "negative-unit", "foreign-style", "duplicate-pin-id", "placed-pin", "active-alternate", "pin-name-space", "pin-number-space",
            "empty-pin-id", "noncanonical-pin-id", "sheet-coordinates", "body-style", "library-id", "future-library-id", "spacing", "empty-child",
            "pin-position" })
        {
            var (design, library) = Fixture();
            string before = SchematicDesignXml.Write(design, [library]);
            var original = design.PartSymbols!.Single();
            var source = original with { Symbol = original.Symbol.Clone() };
            var definition = source.Symbol.Definition;
            var first = definition.Items[0].Item.Unpack<SchematicPin>();
            switch (problem)
            {
                case "unknown-part": source = source with { PartId = Guid.NewGuid() }; break;
                case "unit-count": definition.UnitCount++; break;
                case "missing-pin": definition.Items.RemoveAt(0); break;
                case "pin-name": first.Name = "not the declared pin"; break;
                case "pin-name-space": first.Name = "V CC"; break;
                case "pin-number-space": first.Number = "1 2"; break;
                case "pin-unit": definition.Items[0].Unit = new() { Unit = 1 }; break;
                case "negative-unit": definition.Items[0].Unit = new() { Unit = -1 }; break;
                case "foreign-style": definition.Items[0].BodyStyle = new() { Style = 3 }; break;
                case "duplicate-pin-id": first.Id = definition.Items[1].Item.Unpack<SchematicPin>().Id.Clone(); break;
                case "placed-pin": first.LibraryPinId = new() { Value = Guid.NewGuid().ToString("D") }; break;
                case "active-alternate": first.ActiveAlternate = "alternate"; break;
                case "empty-pin-id": first.Id.Value = Guid.Empty.ToString("D"); break;
                case "noncanonical-pin-id": first.Id.Value = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"; break;
                case "sheet-coordinates": definition.PinsUseLocalCoordinates = false; break;
                case "body-style": source = source with { BodyStyle = 3 }; break;
                case "library-id": source = source with { LibraryId = new() { EntryName = "ambiguous:entry" } }; break;
                case "future-library-id": source = source with { LibraryId = Kiapi.Common.Types.LibraryIdentifier.Parser.ParseFrom(
                    [.. source.LibraryId.ToByteArray(), 0xf8, 0x3e, 0x01]) }; break;
                case "spacing": source.Symbol.PinNameOffset.ValueNm = 1; break;
                case "empty-child": definition.Items.Add(new SchematicSymbolChild()); break;
                // KiCad would read the missing position as the origin and join the pin to every other pin there.
                case "pin-position": first.Position = null; break;
            }
            if (problem != "missing-pin") definition.Items[0].Item = Any.Pack(first);
            var invalid = design with { PartSymbols = problem == "duplicate-part" ? [source, source] : [source] };
            Assert.AreEqual("invalid_part_symbol",
                Assert.ThrowsExactly<AutomationException>(() => SchematicDesignXml.Write(invalid, [library]), problem).Code);
            Assert.AreEqual(before, SchematicDesignXml.Write(design, [library]), problem);
        }
    }

    [TestMethod]
    public void UserFieldsKeepCaseDistinctNamesPrivacyAndMetadataButRejectLossyDeclarations()
    {
        var (design, library) = Fixture();
        var source = design.PartSymbols!.Single();
        SchematicSymbolChild Field(string name, bool privacy = false)
        {
            var field = source.Symbol.Definition.DescriptionField.Clone();
            field.Name = name; field.IsPrivate = privacy;
            field.CustomProperties.Add(new CustomProperty { Key = "automation.guidance", Value = "Preserve this field-owned note." });
            return new() { Item = Any.Pack(field), IsPrivate = privacy };
        }
        var valid = source with { Symbol = source.Symbol.Clone() };
        valid.Symbol.Definition.Items.Add(Field("MPN"));
        valid.Symbol.Definition.Items.Add(Field("mpn", true));
        string xml = SchematicDesignXml.Write(design with { PartSymbols = [valid] }, [library]);
        Assert.AreEqual(valid.Symbol, SchematicDesignXml.Read(xml, [library]).PartSymbols!.Single().Symbol);
        foreach (string problem in new[] { "duplicate", "standard", "case-standard", "empty", "ki_keywords", "ki_description",
            "ki_fp_filters", "ki_locked", "unit", "style", "privacy", "private-standard", "renamed-standard", "duplicate-property" })
        {
            var invalid = source with { Symbol = source.Symbol.Clone() };
            var child = Field("Manufacturer");
            var field = child.Item.Unpack<SchematicField>();
            switch (problem)
            {
                case "duplicate": invalid.Symbol.Definition.Items.Add(child.Clone()); break;
                case "standard": field.Name = "Reference"; break;
                case "case-standard": field.Name = "rEfErEnCe"; break;
                case "empty": field.Name = ""; break;
                case "unit": child.Unit = new() { Unit = 1 }; break;
                case "style": child.BodyStyle = new() { Style = 1 }; break;
                case "privacy": child.IsPrivate = true; break;
                case "private-standard": invalid.Symbol.Definition.ValueField.IsPrivate = true; break;
                case "renamed-standard": invalid.Symbol.Definition.ValueField.Name = "value"; break;
                case "duplicate-property": field.CustomProperties.Add(new CustomProperty { Key = "AUTOMATION.GUIDANCE", Value = "conflict" }); break;
                default: field.Name = problem; break;
            }
            child.Item = Any.Pack(field); invalid.Symbol.Definition.Items.Add(child);
            Assert.AreEqual("invalid_part_symbol", Assert.ThrowsExactly<AutomationException>(() =>
                SchematicDesignXml.Write(design with { PartSymbols = [invalid] }, [library]), problem).Code);
        }
    }

    [TestMethod]
    public void TypedXmlRejectsPlacementWrappersFutureFieldsAndCancellation()
    {
        var (design, library) = Fixture();
        string xml = SchematicDesignXml.Write(design, [library]);
        XNamespace ns = SchematicDesignXml.Namespace;
        foreach (Action<XElement> corrupt in new Action<XElement>[]
        {
            root => root.Element(ns + "part-symbols")!.Elements().Single().SetAttributeValue("future", "value"),
            root => root.Element(ns + "part-symbols")!.Elements().Single().Elements().Single().ReplaceWith(
                XElement.Parse(SchematicDataXml.Write(new SchematicSymbolInstance()))),
            root => root.Element(ns + "part-symbols")!.Elements().Single().SetAttributeValue("body-style", "0")
        })
        {
            var root = XElement.Parse(xml); corrupt(root);
            Assert.ThrowsExactly<AutomationException>(() => SchematicDesignXml.Read(root.ToString(), [library]));
        }
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicPartSymbols.Validate(design, new(true)));
    }

    internal static (SchematicDesign Design, ComponentKnowledgeLibrary Library) Fixture()
    {
        var (design, library) = SchematicDesignTests.Fixture();
        var part = new PartDefinition(Guid.NewGuid(), "Declared two-unit part", 2,
            [new("1", "VCC", 0), new("2", "A", 1), new("3", "B", 2)]);
        var definition = new SchematicSymbol
        {
            Id = new() { LibraryNickname = "Owned", EntryName = "LocalDefinition" },
            UnitCount = 2, PinsUseLocalCoordinates = true, Type = SchematicSymbolType.SstNormal,
            EmbeddedFiles = new(), ReferenceField = Field("Reference", "U"), ValueField = Field("Value", "Declared part"),
            FootprintField = Field("Footprint", ""), DatasheetField = Field("Datasheet", ""), DescriptionField = Field("Description", "")
        };
        definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Standard" });
        definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Alternate" });
        foreach (var pin in part.Pins)
            definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = pin.Unit },
                Item = Any.Pack(new SchematicPin { Id = new() { Value = Guid.NewGuid().ToString("D") },
                    Number = pin.Number, Name = pin.Name, Position = new() }) });
        // An inactive alternate pin is still preserved and identity-checked, but is not
        // an extra electrical pin in the selected representation.
        definition.Items.Add(new SchematicSymbolChild { Unit = new() { Unit = 1 }, BodyStyle = new() { Style = 2 },
            Item = Any.Pack(new SchematicPin { Id = new() { Value = Guid.NewGuid().ToString("D") },
                Number = "2", Name = "alternate_name", Position = new() }) });
        var source = new SchematicPartSymbol(part.Id, new() { LibraryNickname = "External", EntryName = "RequestedPart" },
            new() { CacheKey = "CacheAlias", Definition = definition, ShowPinNames = true,
                ShowPinNumbers = true, PinNameOffset = new() { ValueNm = 1270000 } });
        return (design with { Engineering = design.Engineering with
            { Circuit = design.Engineering.Circuit with { Parts = [.. design.Engineering.Circuit.Parts, part] } },
            PartSymbols = [source] }, library);

        static SchematicField Field(string name, string text) => new() { Name = name,
            Text = new() { Text_ = text, Position = new(), Attributes = new() { Multiline = true } } };
    }
}
