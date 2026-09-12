using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySnapshotSchemaVersions(NativeClient client, DocumentSpecifier document, CancellationToken token)
    {
        const string gap = "erc_settings_require_snapshot_schema_2";
        const string classGap = "net_chain_classes_require_snapshot_schema_3";
        const string exclusionGap = "net_chain_exclusions_require_snapshot_schema_4";
        const string annotationGap = "annotation_requires_snapshot_schema_5";
        void ProjectAnnotation(SchematicMetadata metadata)
        {
            metadata.Annotation = null;
            metadata.UnrepresentedState.Add(annotationGap);
        }
        void ExclusionsUnavailable(SchematicMetadata metadata)
        {
            Assert.IsNull(metadata.Annotation);
            CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), annotationGap);
            foreach (var chain in metadata.NetChains) Assert.IsNull(chain.Exclusions);
            if (metadata.NetChains.Count != 0) CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), exclusionGap);
        }
        void ProjectExclusions(SchematicMetadata metadata)
        {
            foreach (var chain in metadata.NetChains) chain.Exclusions = null;
            if (metadata.NetChains.Count != 0) metadata.UnrepresentedState.Add(exclusionGap);
        }
        void VersionTwo(SchematicMetadata metadata)
        {
            Assert.IsNotNull(metadata.ErcSettings);
            Assert.IsNull(metadata.NetChainClasses);
            CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), classGap);
            ExclusionsUnavailable(metadata);
        }
        void Legacy(SchematicMetadata metadata)
        {
            Assert.IsNull(metadata.ErcSettings);
            Assert.IsNull(metadata.NetChainClasses);
            CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), gap);
            CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), classGap);
            ExclusionsUnavailable(metadata);
        }
        var current = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Assert.IsNotNull(current.Data.Metadata.ErcSettings);
        Assert.IsNotNull(current.Data.Metadata.NetChainClasses);
        Assert.IsNotNull(current.Data.Metadata.Annotation);
        var legacy = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(legacy.Data.Metadata);
        var expected = current.Clone(); expected.Data.Metadata.ErcSettings = null; expected.Data.Metadata.UnrepresentedState.Add(gap);
        expected.Data.Metadata.NetChainClasses = null; expected.Data.Metadata.UnrepresentedState.Add(classGap);
        ProjectExclusions(expected.Data.Metadata);
        ProjectAnnotation(expected.Data.Metadata);
        Assert.AreEqual(expected, legacy, "Legacy projection may omit only the explicitly declared new fields.");

        // A truly old sender omits the new request field. Bypass the current
        // client's automatic opt-in to exercise those exact legacy wire bytes.
        var oldRequest = new ApiRequest { Header = new() { KicadToken = client.Epoch, ClientName = "legacy-snapshot-fixture" },
            Message = Any.Pack(new ReadSchematicScreenData { Document = document }) };
        var oldReply = ApiResponse.Parser.ParseFrom(await new NngTransport().ExchangeAsync(client.Endpoint,
            oldRequest.ToByteArray(), TimeSpan.FromSeconds(15), token));
        Assert.AreEqual(1, (int)oldReply.Status.Status);
        Assert.AreEqual(client.Epoch, oldReply.Header.KicadToken);
        Assert.AreEqual(legacy, oldReply.Message.Unpack<SchematicScreenDataSnapshot>());

        var metadata = await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(metadata.Metadata);
        var hierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        foreach (var instance in hierarchy.Data.Instances) Legacy(instance.Metadata);
        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(observation.Snapshot.Data.Metadata);
        Assert.AreEqual(observation.Snapshot.Revision, observation.Preview.Revision);
        Assert.IsTrue(observation.Preview.Png.Length > 24);

        var views = new RenderSchematicViews { Document = document, SchemaVersion = 1 };
        views.Views.Add(new SchematicRenderView { Key = "legacy", WidthPixels = 128, HeightPixels = 128,
            Region = new() { Position = new(), Size = new() { XNm = 100000000, YNm = 100000000 } } });
        var rendered = await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(views, token);
        Legacy(rendered.Snapshot.Data.Metadata);
        Assert.AreEqual(rendered.Snapshot.Revision, rendered.Views[0].Preview.Revision);
        Assert.IsTrue(rendered.Views[0].Preview.Png.Length > 24);

        var v2Screen = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 2 }, token);
        var v2Expected = current.Clone(); v2Expected.Data.Metadata.NetChainClasses = null;
        v2Expected.Data.Metadata.UnrepresentedState.Add(classGap);
        ProjectExclusions(v2Expected.Data.Metadata);
        ProjectAnnotation(v2Expected.Data.Metadata);
        Assert.AreEqual(v2Expected, v2Screen);
        VersionTwo((await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document, SchemaVersion = 2 }, token)).Metadata);
        var v2Hierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document, SchemaVersion = 2 }, token);
        foreach (var instance in v2Hierarchy.Data.Instances) VersionTwo(instance.Metadata);
        var v2Electrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document, SchemaVersion = 2 }, token);
        foreach (var instance in v2Electrical.Hierarchy.Data.Instances) VersionTwo(instance.Metadata);
        VersionTwo((await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document, SchemaVersion = 2 }, token)).Snapshot.Data.Metadata);
        views.SchemaVersion = 2;
        VersionTwo((await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(views, token)).Snapshot.Data.Metadata);
        var v3 = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 3 }, token);
        var v3Expected = current.Clone(); ProjectExclusions(v3Expected.Data.Metadata);
        ProjectAnnotation(v3Expected.Data.Metadata);
        Assert.AreEqual(v3Expected, v3);
        ExclusionsUnavailable(v3.Data.Metadata);

        var v4Expected = current.Clone(); ProjectAnnotation(v4Expected.Data.Metadata);
        var v4 = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 4 }, token);
        Assert.AreEqual(v4Expected, v4);

        Func<Task>[] unknown = [
            async () => await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document, SchemaVersion = 6 }, token),
            async () => await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 6 }, token),
            async () => await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document, SchemaVersion = 6 }, token),
            async () => await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document, SchemaVersion = 6 }, token),
            async () => await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document, SchemaVersion = 6 }, token),
            async () => { var request = views.Clone(); request.SchemaVersion = 6; await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token); }
        ];
        foreach (var call in unknown)
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(call)).Status);
        Assert.AreEqual(current, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token));
    }
}
