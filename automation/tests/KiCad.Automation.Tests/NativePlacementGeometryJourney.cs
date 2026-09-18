using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<IReadOnlyList<SchematicPlacementGeometry>> MeasureCreationCandidates(NativeClient client,
        DocumentSpecifier root, CheckedSchematicState before, SchematicDesign proposed, SchematicDesign baseline,
        string evidence, string instanceId, CancellationToken token)
    {
        var oldIds = baseline.SymbolBindings.Select(b => b.SymbolOccurrenceId).ToHashSet();
        var newIds = proposed.SymbolBindings.Where(b => !oldIds.Contains(b.SymbolOccurrenceId))
            .Select(b => b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var human = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root.Clone() }, token);
        var result = new List<SchematicPlacementGeometry>();
        foreach (var screen in proposed.Schematic.Instances)
        {
            var request = new MeasureSchematicPlacement { Document = screen.Metadata.Document.Clone(), ExpectedRevision = before.State.Revision.Clone() };
            request.Candidates.Add(screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => newIds.Contains(s.Id.Value)));
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, token);
            Assert.AreEqual(request.Document, measured.Document); Assert.AreEqual(before.State.Revision, measured.Revision);
            Assert.AreEqual(screen.Metadata.ScreenId, measured.ScreenId);
            Assert.IsTrue(measured.PageBounds.Size.XNm > 0 && measured.PageBounds.Size.YNm > 0);
            CollectionAssert.AreEquivalent(request.Candidates.Select(c => c.Id.Value).ToArray(), measured.Candidates.Select(c => c.Id.Value).ToArray());
            foreach (var body in measured.Candidates) Assert.IsTrue(body.Bounds.Size.XNm > 0 && body.Bounds.Size.YNm > 0);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-measure-" + screen.Metadata.Document.SheetPath.Path[^1].Value + ".json"),
                SchematicJson.Formatter.Format(measured), token);
            var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
            Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(stale, token))).Status);
            if (request.Candidates.Count > 0)
            {
                var duplicate = request.Clone(); duplicate.Candidates.Add(request.Candidates[0].Clone());
                Assert.AreEqual(3, (await Assert.ThrowsAsync<NativeApiException>(() =>
                    client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(duplicate, token))).Status);
            }
            result.Add(measured);
        }
        Assert.AreEqual(human, await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = root.Clone() }, token));
        Assert.AreEqual(before, await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token));
        return result;
    }

    private static async Task VerifyCreatedGeometry(NativeClient client, DocumentSpecifier root,
        IReadOnlyList<SchematicPlacementGeometry> proposed, CancellationToken token)
    {
        foreach (var sheet in proposed)
        {
            // Independent existing-object bounds use the displayed instance.
            // Navigation here is an explicit test action after isolation proof.
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = sheet.Document.Clone() }, token);
            var query = new GetBoundingBox { Header = new() { Document = sheet.Document.Clone() }, Mode = BoundingBoxMode.BbmItemAndChildText };
            query.Items.Add(sheet.Candidates.Select(c => c.Id.Clone()));
            var actual = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(query, token);
            for (int i = 0; i < actual.Items.Count; i++)
                Assert.AreEqual(sheet.Candidates.Single(c => c.Id.Equals(actual.Items[i])).Bounds, actual.Boxes[i],
                    "A detached native proposal must measure the same complete body/field envelope as the created symbol.");
        }
        await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
    }
}
