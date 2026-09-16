using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyAutomaticSynchronization(NativeClient client, DocumentSpecifier root,
        DesignRecoveryStore store, string designPath, string evidence, string instanceId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var driver = await AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token);
        await using var session = new AutomaticDesignSynchronization(driver);
        await Watching(_ => true);
        var conflict = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
            AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token));
        Assert.AreEqual("automatic_sync_ownership_conflict", conflict.Code);

        string title = "Automatic native commit " + instanceId;
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = root.Clone(), TitleBlock = new() { Title = title } }, deadline.Token);
        await Watching(design => design.Schematic.Instances.Single(s => s.Metadata.Document.Equals(root)).Metadata.TitleBlock.Title == title);
        var afterNative = await Capture(); var parsed = Read();
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(afterNative.Electrical.Hierarchy.Data, parsed.Schematic, deadline.Token));

        var target = parsed.Engineering.Circuit.Components.First(c => parsed.Engineering.Circuit.Symbols.Any(s => s.ComponentId == c.Id));
        string nextReference = "AUTO900";
        var desired = parsed with { Engineering = parsed.Engineering with { Circuit = parsed.Engineering.Circuit with
        { Components = parsed.Engineering.Circuit.Components.Select(c => c.Id == target.Id ? c with { Reference = nextReference } : c).ToArray() } } };
        await File.WriteAllTextAsync(designPath, SchematicDesignXml.Write(desired, []), Encoding.UTF8, deadline.Token);
        await Watching(design => design.Engineering.Circuit.Components.Single(c => c.Id == target.Id).Reference == nextReference
            && SchematicDesignBindings.Inspect(design, []).Differences.Count == 0);
        var afterXml = await Capture(); parsed = Read();
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(afterXml.Electrical.Hierarchy.Data, parsed.Schematic, deadline.Token));
        Assert.IsTrue(SchematicElectricalComparison.Compare(parsed, afterXml.Electrical, [], deadline.Token).ConnectivityEquivalent);

        byte[] valid = await File.ReadAllBytesAsync(designPath, deadline.Token);
        await File.WriteAllBytesAsync(designPath, [0xff], deadline.Token);
        await Status(s => s.Phase == AutomaticDesignPhase.InvalidDesign);
        CollectionAssert.AreEqual(new byte[] { 0xff }, store.Read()!.State.DesiredFileBytes);
        Assert.AreEqual(afterXml, await Capture(), "Invalid XML cannot change native objects or revision.");
        await File.WriteAllBytesAsync(designPath, valid, deadline.Token);
        await Watching(_ => true);
        var settled = store.Read()!; byte[] settledFile = await File.ReadAllBytesAsync(designPath, deadline.Token);
        var settledNative = await Capture();
        // An identical saved file can notify the watcher but must not generate
        // another native edit, baseline publication or operation receipt.
        await File.WriteAllBytesAsync(designPath, settledFile, deadline.Token);
        await session.DisposeAsync();
        Assert.AreEqual(AutomaticDesignPhase.Stopped, session.Inspect().Phase);
        Assert.AreEqual(settledNative.State.ProcessEpoch, (await Capture()).State.ProcessEpoch);
        Assert.AreEqual(settledNative.State.Revision, (await Capture()).State.Revision);
        Assert.AreEqual(settled.State.LastSynchronization?.OperationId, store.Read()!.State.LastSynchronization?.OperationId);

        // A native edit made while stopped is recovered on a new subscription.
        string missed = "Automatic reattach " + instanceId;
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = root.Clone(), TitleBlock = new() { Title = missed } }, deadline.Token);
        var newDriver = await AutomaticDesignDriver.CreateAsync(store, client, designPath, store.Read()!.RevisionToken, deadline.Token);
        var reattached = new AutomaticDesignSynchronization(newDriver);
        await Wait(reattached, s => s.Phase == AutomaticDesignPhase.Watching
            && Read().Schematic.Instances.Single(item => item.Metadata.Document.Equals(root)).Metadata.TitleBlock.Title == missed);
        Assert.IsTrue(SchematicElectricalComparison.Compare(Read(), (await Capture()).Electrical, [], deadline.Token).ConnectivityEquivalent);
        await reattached.DisposeAsync();
        await using var publicHost = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-automatic-public-host"), Path.Combine(evidence, instanceId + "-automatic-public-host.log"), deadline.Token);
        RequireToolSuccess(await publicHost.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var current = store.Read()!;
        var publicStart = await publicHost.Tool("kicad_design_automatic_sync_start", new
        { instanceId, recoveryPath = store.StatePath, designPath, expectedRecoveryRevision = current.RevisionToken });
        RequireToolSuccess(publicStart);
        string publicSession = publicStart.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
        var listed = await publicHost.Tool("kicad_design_automatic_sync_list", new { instanceId });
        RequireToolSuccess(listed); Assert.HasCount(1, listed.GetProperty("structuredContent").GetProperty("sessions").EnumerateArray());
        var waited = await publicHost.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId = publicSession, afterSequence = 0UL });
        RequireToolSuccess(waited);
        var publicStop = await publicHost.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId = publicSession });
        RequireToolSuccess(publicStop);
        Assert.AreEqual("Stopped", publicStop.GetProperty("structuredContent").GetProperty("status").GetProperty("phase").GetString());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-sync-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeCommitPublishedWithoutApplyCall = true, xmlSaveAppliedWithoutApplyCall = true,
            invalidXmlPreserved = true, correctedXmlResumed = true, competingOwnerRejected = true,
            stopPreservedEditor = true, missedEditRecoveredAfterReattach = true,
            publicToolsQualified = true, crossPlatformReady = false
        }), deadline.Token);

        SchematicDesign Read() => SchematicDesignXml.Read(File.ReadAllText(designPath), []);
        Task<CheckedSchematicState> Capture() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, deadline.Token);
        Task Watching(Func<SchematicDesign, bool> accept) => Wait(session, s => s.Phase == AutomaticDesignPhase.Watching && accept(Read()));
        Task Status(Func<AutomaticDesignStatus, bool> accept) => Wait(session, accept);
        async Task Wait(AutomaticDesignSynchronization current, Func<AutomaticDesignStatus, bool> accept)
        {
            var status = current.Inspect();
            try
            {
                while (!accept(status))
                {
                    Assert.IsFalse(status.Phase is AutomaticDesignPhase.Paused or AutomaticDesignPhase.Stopped,
                        status.ErrorCode + ": " + status.ErrorMessage);
                    status = await current.WaitAsync(status.Sequence, deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-automatic-timeout.json"),
                    JsonSerializer.Serialize(new { status = current.Inspect(), recovery = store.Read() }), CancellationToken.None);
                throw;
            }
        }
    }
}
