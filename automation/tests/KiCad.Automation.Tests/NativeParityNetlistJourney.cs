using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using System.Security.Cryptography;
using System.Text;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyParityNetlistCapture(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string evidence, CancellationToken token)
    {
        var before = await ObserveLifecycleState(client, document, token);
        var request = new ReadSchematicParityNetlist
            { Document = document.Clone(), ExpectedState = before.Clone(), SchemaVersion = 1 };
        var captured = await client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(request, token);
        Assert.AreEqual(1u, captured.SchemaVersion);
        Assert.AreEqual(before, captured.SourceState);
        Assert.IsTrue(captured.SourceState.Document.SheetPath.Path.Count > 0,
            "Native relative netlist paths require an explicit root instance anchor.");
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));
        StringAssert.Contains(captured.NativeNetlistSexpr, "TP1");
        StringAssert.Contains(captured.NativeNetlistSexpr, "TP2");
        StringAssert.Contains(captured.NativeNetlistSexpr, fixture.Symbol);
        StringAssert.Contains(captured.NativeNetlistSexpr, "SIGNAL");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(captured.NativeNetlistSexpr))),
            captured.NetlistSha256);
        await File.WriteAllTextAsync(Path.Combine(evidence, fixture.Symbol + "-parity.net"), captured.NativeNetlistSexpr, token);

        var invalidVersion = request.Clone(); invalidVersion.SchemaVersion = 2;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(invalidVersion, token))).Status);
        var noState = request.Clone(); noState.ExpectedState = null;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(noState, token))).Status);
        var foreign = request.Clone(); foreign.ExpectedState.ProcessEpoch = Guid.NewGuid().ToString("D");
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(foreign, token))).Status);
        var wrongProject = request.Clone();
        wrongProject.Document.Project.Path = Path.Combine(document.Project.Path, "not-this-project");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(wrongProject, token));
        Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

        var title = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        var changed = title.Clone(); changed.Title += " parity-state check";
        try
        {
            await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = changed }, token);
            Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(request, token))).Status);
            request.ExpectedState = await ObserveLifecycleState(client, document, token);
            var updated = await client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(request, token);
            Assert.AreEqual(request.ExpectedState, updated.SourceState);
            StringAssert.Contains(updated.NativeNetlistSexpr, "parity-state check");
            Assert.AreEqual(request.ExpectedState, await ObserveLifecycleState(client, document, token));
        }
        finally
        {
            await client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = document, TitleBlock = title }, token);
        }
        request.ExpectedState = await ObserveLifecycleState(client, document, token);
        var recovered = await client.InvokeAsync<ReadSchematicParityNetlist, SchematicParityNetlistSnapshot>(request, token);
        Assert.AreEqual(request.ExpectedState, recovered.SourceState);
        StringAssert.Contains(recovered.NativeNetlistSexpr, fixture.Symbol);
    }
}
