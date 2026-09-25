using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativePresentation(NativeClient client, DocumentSpecifier document,
        string textId, string symbolId, string evidence, string instanceId, CancellationToken token)
    {
        var policy = new PresentationPolicy(1, 3);
        var baseline = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(baseline.Report.CoverageComplete);
        Assert.IsFalse(baseline.Report.Clear, "Partial native coverage must not masquerade as a verification pass.");
        Assert.IsNotEmpty(baseline.Limitations);
        Guid textGuid = Guid.Parse(textId), symbolGuid = Guid.Parse(symbolId);
        var reference = baseline.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Reference");
        Assert.IsFalse(baseline.Report.Findings.Any(f => f.Rule == "designator_not_visible" && f.ObjectIds.Contains(reference.ObjectId)));
        var header = new ItemHeader { Document = document };
        var query = new GetItemsById { Header = header };
        query.Items.Add(new KIID { Value = textId }); query.Items.Add(new KIID { Value = symbolId });
        var items = await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token);
        var originalText = items.Items.Single(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        var originalSymbol = items.Items.Single(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        var tiny = originalText.Clone();
        tiny.Text.Attributes.Size = new Vector2 { XNm = 200000, YNm = 200000 };
        tiny.Text.Position.XNm = -10000000;
        var hidden = originalSymbol.Clone(); hidden.ReferenceField.Visible = false;
        // The default mode applies the reading-direction rule of the hierarchy check too: the probe (drawn unrotated) with its
        // value turned half round paints it upside down.
        Assert.IsTrue(hidden.Transform is null || hidden.Transform.Orientation is SchematicSymbolOrientation.SsoUnknown or SchematicSymbolOrientation.Sso0,
            "The fixture probe is drawn unrotated: " + hidden.Transform);
        hidden.ValueField.Text.Attributes.Angle = new() { ValueDegrees = 180 };
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Presentation must-catch fixture" };
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(tiny) });
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(hidden) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var broken = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsTrue(broken.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(textGuid) && f.Measured == 0.2m));
        Assert.IsTrue(broken.Report.Findings.Any(f => f.Rule == "page_overflow" && f.ObjectIds.Contains(textGuid)));
        // Field runtime IDs may change through native symbol deserialization;
        // repair ownership remains the exact symbol UUID + canonical field name.
        var brokenReference = broken.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Reference");
        Assert.IsTrue(broken.Report.Findings.Any(f => f.Rule == "designator_not_visible" && f.ObjectIds.Contains(brokenReference.ObjectId)));
        var brokenValue = broken.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Value");
        var upsideDown = broken.Report.Findings.Single(f => f.Rule == "text_orientation" && f.ObjectIds.Contains(brokenValue.ObjectId));
        Assert.AreEqual(180m, upsideDown.Measured); Assert.AreEqual(90m, upsideDown.Limit); Assert.AreEqual(PresentationUnits.Degrees, upsideDown.Unit);
        Assert.AreEqual(policy, broken.Report.Policy, "The report states the policy it applied.");
        var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-presentation-defects.png"), image.Png.ToByteArray(), token);
        batch.Operations.Clear();
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(originalText) });
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(originalSymbol) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var restored = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        var restoredReference = restored.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Reference");
        Assert.IsFalse(restored.Report.Findings.Any(f => f.ObjectIds.Contains(textGuid) && f.Rule is "text_size" or "page_overflow"));
        Assert.IsFalse(restored.Report.Findings.Any(f => f.Rule == "designator_not_visible" && f.ObjectIds.Contains(restoredReference.ObjectId)));
        var restoredValue = restored.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Value");
        Assert.IsFalse(restored.Report.Findings.Any(f => f.Rule == "text_orientation" && f.ObjectIds.Contains(restoredValue.ObjectId)));

        // Guard (presentation re-review finding 1): the same probe made a power symbol, its reference hidden as KiCad hides power
        // references, reports nothing about that reference in the default mode either, although the ordinary probe with the same
        // hidden reference was reported above. KiCad itself says the reference need not show.
        var powered = originalSymbol.Clone();
        powered.Definition.Type = SchematicSymbolType.SstGlobalPower;
        powered.ReferenceField.Visible = false;
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(powered) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var powerCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        var powerReference = powerCheck.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Reference");
        Assert.IsFalse(powerCheck.Report.Findings.Any(f => f.Rule.StartsWith("designator_", StringComparison.Ordinal) && f.ObjectIds.Contains(powerReference.ObjectId)),
            "A power symbol's hidden reference is not a finding: " + string.Join("; ", powerCheck.Report.Findings
                .Where(f => f.ObjectIds.Contains(powerReference.ObjectId)).Select(f => f.Rule + " " + f.Message)));
        var checkedAt = powerCheck.Report.Revision;
        var powerFacts = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
        {
            Document = document.Clone(), IncludePresentation = true,
            ExpectedRevision = new KiCad.Automation.Protocol.DocumentRevision { Epoch = checkedAt.Epoch, Sequence = checkedAt.Sequence }
        }, token);
        var powerReferenceFact = powerFacts.Presentation.Objects.Single(o => o.Id.Value == powerReference.ObjectId.ToString("D"));
        Assert.IsFalse(powerReferenceFact.Visible);
        Assert.IsFalse(powerReferenceFact.DesignatorRequired, "KiCad does not require a power symbol's reference to show.");
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(originalSymbol) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var unpowered = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(unpowered.Report.Findings.Any(f => f.Rule.StartsWith("designator_", StringComparison.Ordinal)
            && f.ObjectIds.Contains(unpowered.RepairTargets.Single(t => t.OwnerId == symbolGuid && t.FieldName == "Reference").ObjectId)));
        var pending = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            NativePresentationChecks.CheckAsync(client, document, policy, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Header = header, Id = pending.Id, Action = (CommitAction)2 }, token);
        await NativePresentationChecks.CheckAsync(client, document, policy, token);

        var table = new SchematicTable
        {
            Id = new KIID { Value = Guid.NewGuid().ToString("D") }, ColumnCount = 2,
            BorderStroke = new StrokeAttributes { Width = new Distance { ValueNm = 200000 } },
            SeparatorsStroke = new StrokeAttributes { Width = new Distance { ValueNm = 350000 } }
        };
        table.ColumnWidths.Add(new[] { new Distance { ValueNm = 20000000 }, new Distance { ValueNm = 20000000 } });
        table.RowHeights.Add(new Distance { ValueNm = 10000000 });
        for (int column = 0; column < 2; ++column)
            table.Cells.Add(new SchematicTableCell
            {
                ColumnSpan = column == 0 ? 2 : 0, RowSpan = 1,
                TextBox = new SchematicTextBox
                {
                    Id = new KIID { Value = Guid.NewGuid().ToString("D") },
                    Textbox = new TextBox
                    {
                        TopLeft = new Vector2 { XNm = 100000000 + column * 20000000, YNm = 140000000 },
                        BottomRight = new Vector2 { XNm = 140000000, YNm = 150000000 },
                        Text = column == 0 ? "Visible table note" : "Covered merged-cell text",
                        Attributes = originalText.Text.Attributes.Clone()
                    }
                }
            });
        foreach (var cell in table.Cells)
            cell.TextBox.Textbox.Attributes.Size = new Vector2 { XNm = 200000, YNm = 200000 };
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(table) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Guid visibleCell = Guid.Parse(table.Cells[0].TextBox.Id.Value);
        Guid coveredCell = Guid.Parse(table.Cells[1].TextBox.Id.Value);
        var tableCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsTrue(tableCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(visibleCell)));
        Assert.IsFalse(tableCheck.Report.Findings.Any(f => f.ObjectIds.Contains(coveredCell)));
        Assert.AreEqual(Guid.Parse(table.Id.Value), tableCheck.RepairTargets.Single(t => t.ObjectId == visibleCell).OwnerId);
        table.Cells[0].TextBox.Textbox.Attributes.Size = new Vector2 { XNm = 1500000, YNm = 1500000 };
        table.Cells[0].TextBox.Textbox.Attributes.Multiline = true;
        table.Cells[0].TextBox.Textbox.Text = string.Join('\n', Enumerable.Repeat("Overflowing table note", 160));
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(table) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        tableCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        var overflowFacts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new() { Document = document }, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-presentation-table-overflow-facts.json"),
            overflowFacts.ToString(), token);
        Assert.IsTrue(tableCheck.Report.Findings.Any(f => f.Rule == "text_container_overflow" && f.ObjectIds.Contains(visibleCell)));
        Assert.IsTrue(tableCheck.Report.Findings.Any(f => f.Rule == "page_overflow" && f.ObjectIds.Contains(visibleCell)),
            "Page: " + overflowFacts.PageBounds + "; text bounds: " + overflowFacts.Objects.Single(o => o.Id.Value == visibleCell.ToString("D")).TextBounds);
        Assert.IsFalse(tableCheck.Report.Findings.Any(f => f.ObjectIds.Contains(coveredCell)));
        var overflowImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-presentation-table-overflow.png"), overflowImage.Png.ToByteArray(), token);
        var originalAngle = table.Cells[0].TextBox.Textbox.Attributes.Angle?.Clone();
        table.Cells[0].TextBox.Textbox.Attributes.Angle = new() { ValueDegrees = 90 };
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(table) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        tableCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        var rotatedOverflow = tableCheck.Report.Findings.Single(f => f.Rule == "page_overflow" && f.ObjectIds.Contains(visibleCell));
        Assert.IsNotNull(rotatedOverflow.Bounds);
        Assert.IsTrue(rotatedOverflow.Bounds.RightNm - rotatedOverflow.Bounds.LeftNm
            > rotatedOverflow.Bounds.BottomNm - rotatedOverflow.Bounds.TopNm,
            "The vertical text's multiline extent must rotate into the horizontal axis.");
        var rotatedImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-presentation-table-rotated-overflow.png"), rotatedImage.Png.ToByteArray(), token);
        table.Cells[0].TextBox.Textbox.Attributes.Angle = originalAngle;
        table.Cells[0].TextBox.Textbox.Text = "Visible table note";
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(table) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        tableCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(tableCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(visibleCell)));
        Assert.IsFalse(tableCheck.Report.Findings.Any(f => f.Rule == "page_overflow" && f.ObjectIds.Contains(visibleCell)));
        var tableImage = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-presentation-table.png"), tableImage.Png.ToByteArray(), token);
        var tableQuery = new GetItemsById { Header = header }; tableQuery.Items.Add(table.Id);
        async Task<SchematicTable> ReadTable() => (await client.InvokeAsync<GetItemsById, GetItemsResponse>(tableQuery, token))
            .Items.Single().Unpack<SchematicTable>();
        var serializedTable = await ReadTable();
        string tableXml = SchematicDataXml.Write(serializedTable);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-table.xml"), tableXml, token);
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Remove = table.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation
        { Create = Any.Pack((SchematicTable)SchematicDataXml.Read(tableXml)) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(serializedTable, await ReadTable(), "XML reconstruction must preserve every table and cell property.");
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        Assert.AreEqual(serializedTable, await ReadTable(), "Reconstructed table properties must survive native save/reload.");
        var unmergedTable = serializedTable.Clone();
        foreach (var cell in unmergedTable.Cells) { cell.ColumnSpan = 1; cell.RowSpan = 1; }
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(unmergedTable) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(unmergedTable, await ReadTable(), "Valid unmerged cells must not be rejected by merge validation.");
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(serializedTable) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        foreach (Action<SchematicTable> corrupt in new Action<SchematicTable>[]
        {
            t => t.Cells[1].TextBox = null,
            t => t.Cells.RemoveAt(1),
            t => t.ColumnWidths.RemoveAt(1),
            t => t.RowHeights.Clear(),
            t => t.Cells[0].ColumnSpan = 3,
            t => t.Cells[0].RowSpan = 2,
            t => t.Cells[0].ColumnSpan = -1,
            t => t.Cells[1].TextBox.Id = t.Cells[0].TextBox.Id.Clone(),
            t => t.Cells[0].TextBox.Id = t.Id.Clone(),
            t => t.Cells[0].TextBox.Id.Value = "not-a-native-uuid",
            t => t.Cells[0].TextBox.Id.Value = Guid.Empty.ToString("D"),
            t => t.Cells[1].ColumnSpan = 1,
            t => t.Cells[0].ColumnSpan = 1
        })
        {
            var invalidTable = serializedTable.Clone(); corrupt(invalidTable);
            batch.Operations.Clear();
            // Earlier graphical work must roll back when table validation fails.
            batch.Operations.Add(new SchematicItemOperation { Remove = originalText.Id });
            batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(invalidTable) });
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token))).Status);
            Assert.AreEqual(serializedTable, await ReadTable());
            var textQuery = new GetItemsById { Header = header }; textQuery.Items.Add(originalText.Id);
            Assert.AreEqual(originalText, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token))
                .Items.Single().Unpack<SchematicText>());
        }
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Remove = table.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);

        var sheetQuery = new GetItems { Header = header }; sheetQuery.Types_.Add(KiCadObjectType.KotSchSheet);
        var originalSheet = (await client.InvokeAsync<GetItems, GetItemsResponse>(sheetQuery, token)).Items.First().Unpack<SheetSymbol>();
        var pinSheet = originalSheet.Clone();
        var pin = new SheetPin
        {
            Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Position = pinSheet.Position.Clone(),
            Text = originalText.Text.Clone(), Side = SheetSide.ShsLeft, Shape = SchematicLabelShape.SlshInput
        };
        pin.Position.YNm += 5000000;
        pin.Text.Text_ = "PRESENTATION_PIN";
        pin.Text.Attributes.Size = new Vector2 { XNm = 200000, YNm = 200000 };
        pinSheet.Pins.Add(pin);
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(pinSheet) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Guid pinId = Guid.Parse(pin.Id.Value);
        var pinCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsTrue(pinCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(pinId)));
        Assert.AreEqual(Guid.Parse(pinSheet.Id.Value), pinCheck.RepairTargets.Single(t => t.ObjectId == pinId).OwnerId);
        pin.Text.Attributes.Size = new Vector2 { XNm = 1500000, YNm = 1500000 };
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(pinSheet) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        pinCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(pinCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(pinId)));
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(originalSheet) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);

        // Use a real captured PNG as the native image payload; no fabricated
        // screenshot or guessed image extent enters this acceptance case.
        var picture = new SchematicImage
        {
            Id = new KIID { Value = Guid.NewGuid().ToString("D") }, ImageData = image.Png,
            Position = new Vector2 { XNm = -10000000, YNm = 50000000 },
            ImageScale = new Ratio { Value = 0.25 }
        };
        batch.Operations.Clear();
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(picture) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var cropped = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Guid pictureId = Guid.Parse(picture.Id.Value);
        Assert.IsTrue(cropped.Report.Findings.Any(f => f.Rule == "image_cropped" && f.ObjectIds.Contains(pictureId)));
        picture.Position = new Vector2 { XNm = 100000000, YNm = 100000000 };
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(picture) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var fitted = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(fitted.Report.Findings.Any(f => f.Rule == "image_cropped" && f.ObjectIds.Contains(pictureId)));
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Remove = picture.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.IsFalse((await NativePresentationChecks.CheckAsync(client, document, policy, token)).RepairTargets.Any(t => t.ObjectId == pictureId));

        var lineQuery = new GetItems { Header = header }; lineQuery.Types_.Add(KiCadObjectType.KotSchLine);
        var lineTemplate = (await client.InvokeAsync<GetItems, GetItemsResponse>(lineQuery, token)).Items
            .Select(i => i.Unpack<SchematicLine>()).First(w => w.Type == SchematicLineType.SltWire);
        var labelQuery = new GetItems { Header = header }; labelQuery.Types_.Add(KiCadObjectType.KotSchLabel);
        var labelTemplate = (await client.InvokeAsync<GetItems, GetItemsResponse>(labelQuery, token)).Items.First().Unpack<LocalLabel>();
        var notedLabel = labelTemplate.Clone();
        var note = new SchematicField { Name = "Placement note", Visible = true, Text = originalText.Text.Clone() };
        note.Text.Text_ = "Keep readable";
        note.Text.Position = labelTemplate.Position.Clone();
        note.Text.Position.YNm += 3000000;
        note.Text.Attributes.Size = new Vector2 { XNm = 200000, YNm = 200000 };
        notedLabel.Fields.Add(note);
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(notedLabel) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var noteCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Guid labelOwner = Guid.Parse(notedLabel.Id.Value);
        var noteTarget = noteCheck.RepairTargets.Single(t => t.OwnerId == labelOwner && t.FieldName == note.Name);
        Assert.IsTrue(noteCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(noteTarget.ObjectId)));
        note.Text.Attributes.Size = new Vector2 { XNm = 1500000, YNm = 1500000 };
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(notedLabel) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        noteCheck = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        noteTarget = noteCheck.RepairTargets.Single(t => t.OwnerId == labelOwner && t.FieldName == note.Name);
        Assert.IsFalse(noteCheck.Report.Findings.Any(f => f.Rule == "text_size" && f.ObjectIds.Contains(noteTarget.ObjectId)));
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(labelTemplate) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var created = new List<KIID>();
        var crossingLabels = new List<LocalLabel>();
        batch.Operations.Clear();
        var mainLine = AddWire(110, 180, 170, 180, "PRESENTATION_MAIN");
        for (int i = 0; i < 3; ++i) AddWire(125 + i * 15, 165, 125 + i * 15, 195, "PRESENTATION_CROSS_" + i);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var crossed = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Guid mainId = Guid.Parse(mainLine.Id.Value);
        var crossingFinding = crossed.Report.Findings.Single(f => f.Rule == "excessive_crossings" && f.ObjectIds.Contains(mainId));
        Assert.AreEqual(3m, crossingFinding.Measured);
        Assert.AreEqual(2m, crossingFinding.Limit);
        Assert.IsNotNull(crossingFinding.Locations);
        Assert.AreEqual(3, crossingFinding.Locations.Count);
        for (int i = 0; i < 3; ++i)
        {
            long x = (125L + i * 15) * 1000000;
            Assert.AreEqual(new PresentationBounds(x, 180000000, x, 180000000), crossingFinding.Locations[i].Bounds);
            Assert.AreEqual(2, crossingFinding.Locations[i].ObjectIds.Count);
            Assert.IsTrue(crossingFinding.Locations[i].ObjectIds.Contains(mainId));
        }
        Assert.IsFalse(crossed.Limitations.Any(l => l.Contains("wire lacks", StringComparison.Ordinal)));
        image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-presentation-crossings.png"), image.Png.ToByteArray(), token);
        // Shorten the main wire so it crosses two signals; the label remains at
        // its unchanged start and native connectivity remains explicit.
        mainLine.End.XNm = 145000000;
        batch.Operations.Clear(); batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(mainLine) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var fewer = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(fewer.Report.Findings.Any(f => f.Rule == "excessive_crossings" && f.ObjectIds.Contains(mainId)));
        // Preserve all three geometric intersections but electrically merge
        // their signals through labels. The native graph, not geometry alone,
        // must determine whether these count as unrelated signal crossings.
        mainLine.End.XNm = 170000000;
        batch.Operations.Clear();
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(mainLine) });
        foreach (var label in crossingLabels.Skip(1))
        {
            label.Text.Text_ = "PRESENTATION_MAIN";
            batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(label) });
        }
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var joined = await NativePresentationChecks.CheckAsync(client, document, policy, token);
        Assert.IsFalse(joined.Limitations.Any(l => l.Contains("wire lacks", StringComparison.Ordinal)));
        Assert.IsFalse(joined.Report.Findings.Any(f => f.Rule == "excessive_crossings" && f.ObjectIds.Contains(mainId)));
        var nativeFacts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, token);
        var fixtureWires = nativeFacts.Wires.Where(w => created.Any(id => id.Value == w.Id.Value)).ToArray();
        Assert.HasCount(4, fixtureWires);
        Assert.HasCount(1, fixtureWires.Select(w => w.SignalKey).Distinct().ToArray());
        batch.Operations.Clear();
        foreach (var id in created) batch.Operations.Add(new SchematicItemOperation { Remove = id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        await VerifyRepeatedSheetPresentation(client, document, originalSymbol, policy, evidence, instanceId, token);

        SchematicLine AddWire(int x1, int y1, int x2, int y2, string name)
        {
            var wire = lineTemplate.Clone(); wire.Id = new KIID { Value = Guid.NewGuid().ToString("D") };
            wire.Start = new Vector2 { XNm = x1 * 1000000L, YNm = y1 * 1000000L };
            wire.End = new Vector2 { XNm = x2 * 1000000L, YNm = y2 * 1000000L };
            var label = labelTemplate.Clone(); label.Id = new KIID { Value = Guid.NewGuid().ToString("D") };
            label.Position = wire.Start.Clone(); label.Text.Position = wire.Start.Clone(); label.Text.Text_ = name;
            created.Add(wire.Id); created.Add(label.Id);
            crossingLabels.Add(label);
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(wire) });
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(label) });
            return wire;
        }
    }

    // Hierarchy mode on this session's repeated sheets (ledger p0cd7a559e13ec029). The two instances of one shared child
    // file are measured separately, each with its own references: a copy of the fixture symbol added to the shared child
    // with an annotated reference on the first instance and the unannotated "?" form on the second is reported on the
    // second instance only. A check started from either child instance covers only that instance. A revision observed
    // before the design changed, and a sheet instance KiCad has not loaded, are refused by name.
    private static async Task VerifyRepeatedSheetPresentation(NativeClient client, DocumentSpecifier document,
        SchematicSymbolInstance template, PresentationPolicy policy, string evidence, string instanceId, CancellationToken token)
    {
        static string Key(DocumentSpecifier instance) => string.Join('/', instance.SheetPath.Path.Select(p => p.Value));
        var before = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = document.Clone() }, token);
        var repeated = before.Data.Instances.Where(s => s.Metadata.Document.SheetPath.Path.Count > 1)
            .GroupBy(s => s.Metadata.ScreenId.Value).First(g => g.Count() >= 2)
            .Select(s => s.Metadata.Document).OrderBy(Key, StringComparer.Ordinal).Take(2).ToArray();
        DocumentSpecifier first = repeated[0], second = repeated[1];
        string prefix = new(template.ReferenceField.Text.Text_.TakeWhile(char.IsLetter).ToArray());
        Assert.IsFalse(string.IsNullOrEmpty(prefix), "The fixture symbol's reference starts with its designator letters.");
        string annotated = prefix + "900", unannotated = prefix + "?";

        var symbol = template.Clone();
        symbol.Id = new KIID { Value = Guid.NewGuid().ToString("D") };
        long dx = 100_000_000 - symbol.Position.XNm, dy = 100_000_000 - symbol.Position.YNm;
        foreach (var field in new[] { symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField }
                     .Concat(symbol.UserFields).Where(f => f?.Text?.Position is not null))
        {
            field.Text.Position.XNm += dx; field.Text.Position.YNm += dy;
        }
        symbol.Position = new Vector2 { XNm = 100_000_000, YNm = 100_000_000 };
        symbol.Path = first.SheetPath.Clone();
        symbol.Locked = LockedState.LsUnlocked;
        symbol.ReferenceField.Text.Text_ = annotated;
        symbol.ReferenceField.Visible = true;
        if (symbol.SeparatePinIdentities)
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.LibraryPinId is null) continue;
                pin.Id = new KIID { Value = Guid.NewGuid().ToString("D") };
                child.Item = Any.Pack(pin);
            }
        symbol.InstanceRecords = new();
        foreach (var (instance, reference) in new[] { (first, annotated), (second, unannotated) })
        {
            var record = new SymbolSheetRecord { ProjectName = document.Project.Name, Reference = reference, Unit = symbol.Unit?.Unit ?? 1, Variants = new() };
            record.Path.Add(instance.SheetPath.Path.Select(p => p.Clone()));
            symbol.InstanceRecords.Records.Add(record);
        }
        var batch = new ApplySchematicItemBatch { Document = first.Clone(), Description = "Presentation per-instance reference fixture" };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Guid owner = Guid.Parse(symbol.Id.Value);

        IEnumerable<PresentationFinding> About(NativePresentationCheck check, DocumentSpecifier instance)
        {
            var reference = check.RepairTargets.Single(t => t.OwnerId == owner && t.FieldName == "Reference" && t.SheetPath == Key(instance));
            return check.Report.Findings.Where(f => f.SheetPath == Key(instance) && f.ObjectIds.Contains(reference.ObjectId));
        }
        var whole = await NativePresentationChecks.CheckHierarchyAsync(client, document, policy, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-presentation-repeated-sheets.json"),
            System.Text.Json.JsonSerializer.Serialize(whole), token);
        CollectionAssert.AreEquivalent(before.Data.Instances.Select(s => Key(s.Metadata.Document)).ToArray(),
            whole.Report.Sheets!.Select(s => s.SheetPath).ToArray(), "Every loaded sheet instance is checked, each repeated instance on its own.");
        Assert.IsFalse(About(whole, first).Any(), $"The first instance shows '{annotated}' and reports nothing about it: "
            + string.Join("; ", About(whole, first).Select(f => f.Rule + " " + f.Message)));
        var reported = About(whole, second).Single();
        Assert.AreEqual("designator_unannotated", reported.Rule);
        StringAssert.Contains(reported.Message, "'" + unannotated + "'");
        Assert.AreEqual(0m, reported.Measured); Assert.AreEqual(1m, reported.Limit); Assert.AreEqual(PresentationUnits.Count, reported.Unit);

        // Started from one child instance, the check covers that instance only, with that instance's own reference.
        foreach (var (instance, expectedRules) in new[] { (first, Array.Empty<string>()), (second, new[] { "designator_unannotated" }) })
        {
            var branch = await NativePresentationChecks.CheckHierarchyAsync(client, instance, policy, token);
            CollectionAssert.AreEqual(new[] { Key(instance) }, branch.Report.Sheets!.Select(s => s.SheetPath).ToArray());
            CollectionAssert.AreEqual(expectedRules, About(branch, instance).Select(f => f.Rule).ToArray(), Key(instance));
        }

        // The revision read before the symbol was added no longer holds, and a sheet instance below a leaf is not loaded.
        Assert.AreEqual("presentation_revision_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            NativePresentationChecks.CheckHierarchyAsync(client, document, policy, token, before.Revision))).Code);
        var absent = second.Clone(); absent.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        Assert.AreEqual("presentation_sheet_not_loaded", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            NativePresentationChecks.CheckHierarchyAsync(client, absent, policy, token))).Code);
        // A stale revision is refused as stale first, even when it names a sheet instance KiCad does not hold now.
        Assert.AreEqual("presentation_revision_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            NativePresentationChecks.CheckHierarchyAsync(client, absent, policy, token, before.Revision))).Code);
        // The displayed-sheet check refuses a stale revision as stale first as well, when it names a sheet instance KiCad does
        // not hold now or one it holds but does not display, rather than as a request for a sheet that is not displayed.
        foreach (var instance in new[] { absent, first })
            Assert.AreEqual("presentation_revision_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                NativePresentationChecks.CheckAsync(client, instance, policy, token, before.Revision))).Code, Key(instance));

        batch.Operations.Clear();
        batch.Operations.Add(new SchematicItemOperation { Remove = symbol.Id.Clone() });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
    }
}
