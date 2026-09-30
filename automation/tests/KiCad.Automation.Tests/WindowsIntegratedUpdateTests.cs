using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsIntegratedUpdateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsIntegratedUpdate")]
    public Task ActualCaptionUpdatePreservesTwoDirtyDesignsAcrossDifferentSignedBuilds() => RunAsync(reconnectMcp: false);

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsIntegratedMcpUpdate")]
    public Task ActualPublicCaptionUpdateReconnectsPackagedMcpAndPreservesBothDesigns() => RunAsync(reconnectMcp: true);

    private async Task RunAsync(bool reconnectMcp)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows packages and desktop."); return; }
        string method = reconnectMcp ? nameof(ActualPublicCaptionUpdateReconnectsPackagedMcpAndPreservesBothDesigns)
            : nameof(ActualCaptionUpdatePreservesTwoDirtyDesignsAcrossDifferentSignedBuilds);
        string? workerRoot = Environment.GetEnvironmentVariable("KICAD_WINDOWS_PAIR_WORKER_ROOT");
        if (workerRoot is not null)
        {
            Assert.AreEqual(method, Environment.GetEnvironmentVariable("KICAD_WINDOWS_PAIR_WORKER_METHOD"));
            await RunNativeAsync(reconnectMcp, workerRoot);
            return;
        }

        // The native NNG DLL stays loaded for the life of the test process.
        // Only its parent can remove the retained installation after it exits.
        string scratch = Directory.CreateTempSubdirectory("kwpair-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, reconnectMcp ? "windows-integrated-mcp-update" : "windows-integrated-update")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        await WindowsFixtureCleanup.PreserveFailuresAsync(async () =>
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KiCad.Automation.slnx"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var worker = await WindowsLauncherTests.Invoke("dotnet", ["test", "KiCad.Automation.slnx", "--configuration", configuration,
                "--no-build", "--no-restore", "--filter", "FullyQualifiedName=" + typeof(WindowsIntegratedUpdateTests).FullName + "." + method,
                "--logger", "trx", "--results-directory", Path.Combine(evidence, "native-worker")], repository.FullName,
                deadline.Token, input: null, environment: new Dictionary<string, string?>
                {
                    ["KICAD_WINDOWS_PAIR_WORKER_ROOT"] = scratch,
                    ["KICAD_WINDOWS_PAIR_WORKER_METHOD"] = method,
                    ["KICAD_HOSTED_FIXTURE_EVIDENCE"] = Path.GetDirectoryName(evidence)!
                });
            await File.WriteAllTextAsync(Path.Combine(evidence, "native-worker.stdout.log"), worker.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "native-worker.stderr.log"), worker.Error, deadline.Token);
            Assert.AreEqual(0, worker.ExitCode, "The native update worker failed; inspect its result and retained cleanup evidence.");
        }, async () =>
        {
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            // The two known project roots own their separate native Git
            // history. Do not search arbitrary descendants for read-only data.
            await WindowsFixtureCleanup.RemoveOwnedTemporaryProjectAsync(scratch, "first");
            await WindowsFixtureCleanup.RemoveOwnedTemporaryProjectAsync(scratch, "second");
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        });
    }

    private async Task RunNativeAsync(bool reconnectMcp, string scratch)
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException("Frozen input required: " + name);
        string baselineCommit = Required("KICAD_WINDOWS_UI_BASELINE_COMMIT"), candidateCommit = Required("KICAD_WINDOWS_UI_EXPECTED_COMMIT");
        foreach (string commit in new[] { baselineCommit, candidateCommit }) Assert.IsTrue(Regex.IsMatch(commit, "^[0-9a-f]{40}$"));
        Assert.AreNotEqual(baselineCommit, candidateCommit, "The actual native build must change.");
        byte[] key = await File.ReadAllBytesAsync(Required("KICAD_WINDOWS_UI_PUBLISHER_SPKI"));
        byte[] baselineEnvelope = await File.ReadAllBytesAsync(Required("KICAD_WINDOWS_UI_BASELINE_ENVELOPE"));
        var baseline = UpdateManifestCodec.Verify(baselineEnvelope, key, "preview");
        Assert.AreEqual(baselineCommit, baseline.Release.Commit);
        var origin = new Uri(Required("KICAD_WINDOWS_UI_ORIGIN"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var downloads = new UpdateDownloader(origin);
        byte[] candidateEnvelope = await downloads.FetchManifestAsync("preview", deadline.Token);
        var candidate = UpdateManifestCodec.Verify(candidateEnvelope, key, "preview", new(baseline.Release.Sequence, baseline.PayloadSha256));
        Assert.AreEqual(candidateCommit, candidate.Release.Commit);
        Assert.IsTrue(candidate.Release.Sequence > baseline.Release.Sequence);
        Assert.IsNotNull(candidate.ForInstallation("win-x64", "zip"));
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, reconnectMcp ? "windows-integrated-mcp-update" : "windows-integrated-update")).FullName;
        string root = Path.Combine(scratch, "installed"), state = Path.Combine(scratch, "registry");
        string? priorLibrary = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        var processes = new List<Process>(); var runtimeDirectories = new HashSet<string>();
        using var job = new WindowsProcessJob();
        try
        {
            string archive = (await downloads.DownloadAsync(baseline, "win-x64", "zip",
                Directory.CreateDirectory(Path.Combine(scratch, "download")).FullName, cancellationToken: deadline.Token)).Path;
            var installed = await WindowsVerifiedVersions.InstallAsync(root, archive, baselineEnvelope, key, origin, "preview", deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(installed.Version.VersionDirectory, "bin/nng.dll"));
            var observer = await WindowsUiObserver.CreateAsync(scratch, evidence, deadline.Token);
            var ui = new WindowsUiAutomation(observer);
            WindowsInstalledPackageTests.Mcp? mcp = null;
            NativeCaptionMcpProbe? mcpProbe = null;
            if (reconnectMcp)
            {
                mcpProbe = new NativeCaptionMcpProbe(state, evidence, deadline.Token, async (executable, name) =>
                {
                    mcp = await WindowsInstalledPackageTests.Mcp.Start(executable, scratch, state, evidence, name, job,
                        deadline.Token, traceUpdates: true);
                    return mcp;
                });
                await mcpProbe.StartAsync(Path.Combine(root, "kicad-mcp.exe"));
            }
            else mcp = await WindowsInstalledPackageTests.Mcp.Start(Path.Combine(root, "kicad-mcp.exe"), scratch, state,
                evidence, "pair", job, deadline.Token, traceUpdates: true);
            await using var mcpLifetime = (IAsyncDisposable?)mcpProbe ?? mcp!;
            var first = await Start("first");
            var second = await Start("second");
            await Task.WhenAll(WaitPrepared(first), WaitPrepared(second));
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(second.Identity, "Update", deadline.Token);
            await Capture(first.Identity, "update-available");
            await ui.PressAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Cancel", deadline.Token);
            await Capture(first.Identity, "cancel-prompt");
            await ui.PressAsync(first.Identity, "Cancel", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            Assert.AreEqual(installed.SelectionId, WindowsVerifiedVersions.InspectSelectionId(root));
            await Marker(first.Client, first.Document, first.MarkerId, first.MarkerText, dirty: true);
            if (mcpProbe is not null) await mcpProbe.ObserveAsync(first.Record.InstanceId, "first-after-cancel", originalDocumentEpoch: true);
            Assert.IsFalse(File.Exists(first.Schematic));
            var updatedFirst = await Update(first, "first");
            Assert.AreEqual(second.Identity, WindowsProcessIdentity.Read(second.Identity.ProcessId));
            Assert.AreEqual(second.Client.Epoch, (await second.Client.HandshakeAsync(deadline.Token)).Epoch);
            await Marker(second.Client, second.Document, second.MarkerId, second.MarkerText, dirty: true);
            Assert.IsFalse(File.Exists(second.Schematic));
            var updatedSecond = await Update(second, "second");
            CollectionAssert.AreEqual(candidateEnvelope, await downloads.FetchManifestAsync("preview", deadline.Token), "The frozen public feed changed.");
            foreach (var process in new[] { updatedFirst, updatedSecond })
            {
                nint schematic = await WindowsNativeUi.WaitForWindow(process, "Schematic Editor", deadline.Token);
                WindowsNativeUi.Close(process, schematic);
                await WindowsNativeUi.WaitForClosedWindow(process, schematic, deadline.Token);
                WindowsNativeUi.Close(process, await WindowsNativeUi.WaitForWindow(process, "KiCad", deadline.Token));
                await process.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, process.ExitCode);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, baselineCommit, candidateCommit, platform = "win-x64", publicSignedFeed = origin.AbsoluteUri,
                actualCaptionClicks = true, automaticInstalledContext = true, cancelPreservedDirtyObject = true,
                savePersistedMarkerAndIdentity = true, secondInstancePreserved = true, bothInstancesRestarted = true,
                nativeEpochsChanged = true, postUpdateMcpReconnectionVerified = mcpProbe?.ReconnectedDesigns == 2,
                packagedMcpRestartVerified = mcpProbe?.ServerRestartVerified == true,
                automaticUpdatingQualified = false, crossPlatformReady = false
            }), deadline.Token);

            async Task<Design> Start(string name)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, name)).FullName;
                string project = Path.Combine(directory, name + ".kicad_pro"), schematic = Path.ChangeExtension(project, ".kicad_sch");
                await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                var started = await mcp!.Tool("kicad_instance_start", new { executable = installed.Version.NativeExecutable,
                    projectPath = project, softwareRendering = true });
                string id = McpToolPayload.Object(started).GetProperty("instanceId").GetString()!;
                var record = JsonSerializer.Deserialize<InstanceRecord>(await File.ReadAllTextAsync(Path.Combine(state, id + ".json"), deadline.Token))!;
                Assert.AreEqual(project, record.ProjectPath); Assert.IsNotNull(record.ProcessId);
                var process = Process.GetProcessById(record.ProcessId.Value);
                if (!job.Contains(process)) { process.Dispose(); Assert.Fail("Only this fixture's child may receive input."); }
                processes.Add(process); runtimeDirectories.Add(NativeIpcEndpoint.RuntimeDirectory(id));
                var client = new NativeClient(new NngTransport(), record.Endpoint, record.Epoch);
                var session = await client.HandshakeAsync(deadline.Token); Assert.AreEqual(id, session.InstanceId);
                var created = await mcp.Tool("kicad_schematic_create", new { instanceId = id, path = schematic });
                string documentJson = created.GetProperty("content").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
                string markerId = Guid.NewGuid().ToString("D"), markerText = "Unsaved Windows " + name + " design " + Guid.NewGuid().ToString("N");
                var before = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = before.Revision,
                    DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Update qualification fixture" };
                batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
                {
                    Id = new() { Value = markerId }, Text = new() { Text_ = markerText,
                        Position = new() { XNm = 100000000, YNm = 80000000 },
                        Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } }
                }) });
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, deadline.Token);
                if (mcpProbe is not null) await mcpProbe.AttachDesignAsync(id, name, client, document);
                await Marker(client, document, markerId, markerText, dirty: true);
                return new(record, WindowsProcessIdentity.Read(process.Id), client, document, schematic, markerId, markerText);
            }

            async Task WaitPrepared(Design design)
            {
                try
                {
                    await ui.WaitButtonAsync(design.Identity, "Update", deadline.Token, TimeSpan.FromMinutes(5));
                }
                catch
                {
                    try { await Capture(design.Identity, Path.GetFileNameWithoutExtension(design.Schematic) + "-update-not-ready"); }
                    catch (Exception captureError)
                    {
                        await File.WriteAllTextAsync(Path.Combine(evidence, design.Record.InstanceId + "-capture-failure.txt"), captureError.ToString());
                    }
                    throw;
                }
            }

            async Task<Process> Update(Design old, string name)
            {
                string handovers = Path.Combine(root, "handovers");
                string[] before = Directory.Exists(handovers) ? Directory.GetDirectories(handovers) : [];
                await ui.WaitButtonAsync(old.Identity, "Update", deadline.Token);
                await ui.PressAsync(old.Identity, "Update", deadline.Token);
                await ui.WaitButtonAsync(old.Identity, "Save", deadline.Token); await Capture(old.Identity, name + "-save-prompt");
                await ui.PressAsync(old.Identity, "Save", deadline.Token);
                var original = processes.Single(x => x.Id == old.Identity.ProcessId);
                await original.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, original.ExitCode);
                byte[] saved = await File.ReadAllBytesAsync(old.Schematic, deadline.Token);
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); ready.CancelAfter(TimeSpan.FromMinutes(3));
                WindowsUpdateHandoffState? result = null; int delay = 50;
                while (result is null)
                {
                    foreach (string journal in Directory.Exists(handovers) ? Directory.GetDirectories(handovers).Except(before) : [])
                    {
                        string path = Path.Combine(journal, "state.json"); if (!File.Exists(path)) continue;
                        var observed = JsonSerializer.Deserialize<WindowsUpdateHandoffState>(await File.ReadAllBytesAsync(path, ready.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        Assert.IsFalse(observed.Status is "activation_failed" or "reconciliation_required" or "launch_failed", observed.Error);
                        if (observed.Status == "restarted") result = observed;
                    }
                    if (result is null) { await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 1000); }
                }
                Assert.IsNotNull(result.ProcessId); Assert.IsNotNull(result.ProcessIdentity); Assert.IsNotNull(result.Endpoint);
                var process = Process.GetProcessById(result.ProcessId.Value);
                if (!job.Contains(process)) { process.Dispose(); Assert.Fail("The replacement escaped the fixture's owned process job."); }
                processes.Add(process); Assert.AreEqual(result.ProcessIdentity, WindowsProcessIdentity.Read(process.Id));
                var version = await WindowsVerifiedVersions.InspectExecutableAsync(root, result.ProcessIdentity.Executable, ready.Token);
                Assert.AreEqual(candidateCommit, version.Commit);
                var client = new NativeClient(new NngTransport(), result.Endpoint, result.NativeEpoch);
                var session = await client.HandshakeAsync(ready.Token);
                Assert.AreEqual(old.Record.InstanceId, session.InstanceId); Assert.AreEqual(old.Record.ProjectPath, session.ProjectPath);
                Assert.AreNotEqual(old.Record.Epoch, session.Epoch);
                var document = (await client.OpenRootSchematicAsync(old.Schematic, ready.Token)).Document;
                await Marker(client, document, old.MarkerId, old.MarkerText, dirty: false);
                CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(old.Schematic, ready.Token));
                if (mcpProbe is not null)
                    await mcpProbe.ReconnectAsync(old.Record.InstanceId, root, Path.GetFileName(result.JournalDirectory), result.NativeEpoch!,
                        name == "first" ? Path.Combine(root, "kicad-mcp.exe") : null);
                await Capture(result.ProcessIdentity, name + "-restarted");
                return process;
            }

            async Task Marker(NativeClient client, DocumentSpecifier document, string id, string text, bool dirty)
            {
                var snapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var marker = snapshot.Data.Items.Where(x => x.Is(SchematicText.Descriptor)).Select(x => x.Unpack<SchematicText>()).Single(x => x.Id.Value == id);
                Assert.AreEqual(text, marker.Text.Text_);
                var saved = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, deadline.Token);
                Assert.AreEqual(dirty, saved.UnsavedSchematicChanges);
            }

            async Task Capture(WindowsProcessIdentity identity, string name)
            {
                var observed = await observer.InspectAsync(identity, deadline.Token);
                using var process = Process.GetProcessById(identity.ProcessId); Assert.IsTrue(job.Contains(process));
                var windows = observed.GetProperty("windows").EnumerateArray().ToArray(); Assert.IsTrue(windows.Length > 0);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), observed.GetRawText(), deadline.Token);
                int index = 0;
                foreach (var window in windows.Take(5))
                    WindowsNativeUi.Capture(process, checked((nint)ulong.Parse(window.GetProperty("handle").GetString()!, CultureInfo.InvariantCulture)),
                        Path.Combine(evidence, name + "-" + index++ + ".png"));
            }
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString()); throw; }
        finally
        {
            using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try { await job.StopAndWaitAsync(cleanup.Token); }
                finally { job.Dispose(); }
            }
            foreach (var process in processes)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(cleanup.Token); process.Dispose();
            }
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", priorLibrary);
            // Include unacknowledged starts recorded before a failed MCP reply.
            foreach (string file in Directory.Exists(state) ? Directory.GetFiles(state, "*.json", SearchOption.AllDirectories) : [])
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id))
                    runtimeDirectories.Add(NativeIpcEndpoint.RuntimeDirectory(id.ToString("D")));
            foreach (string directory in runtimeDirectories.Where(Directory.Exists))
            {
                string destination = Directory.CreateDirectory(Path.Combine(evidence, Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory, "*.log")) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                await WindowsFixtureCleanup.RemoveOwnedRuntimeDirectoryAsync(directory);
            }
            string handovers = Path.Combine(root, "handovers");
            foreach (string file in Directory.Exists(handovers) ? Directory.GetFiles(handovers, "*.json", SearchOption.AllDirectories) : [])
            {
                string destination = Path.Combine(evidence, "handovers", Path.GetRelativePath(handovers, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            // The parent deletes this installation after native libraries unload.
        }
    }

    private sealed record Design(InstanceRecord Record, WindowsProcessIdentity Identity, NativeClient Client,
        DocumentSpecifier Document, string Schematic, string MarkerId, string MarkerText);
}
