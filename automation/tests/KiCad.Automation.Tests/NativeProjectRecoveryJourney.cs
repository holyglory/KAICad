using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Project-file recovery is a separate native journey from lost-schematic rebuild. It proves the
    // public recovery start tool can recreate the project container, start the same registered
    // instance, create the declared root and then run the existing XML rebuild against real files.
    private static async Task VerifyPsuCpuProjectFileRecovery(InstanceRegistry registry, NativeClient client,
        PsuCpuNativeContext context, Process native, string display, string evidence, string instanceId,
        Action<Process> ownProcess, bool completeDesign, CancellationToken token)
    {
        DesignRecoveryStore? completed = null;
        if (completeDesign)
        {
            // Reuse the accepted fully connected native fixture, including its non-default typed
            // settings and ERC exclusions, rather than claiming an empty-sheet probe proves it.
            completed = await VerifyPsuCpuXmlRebuild(client, context, native.Id, display, evidence, instanceId, token);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = context.Root.Clone() }, token);
        }
        evidence = Directory.CreateDirectory(Path.Combine(evidence, instanceId + "-project-recovery")).FullName;
        string projectDirectory = context.ProjectDirectory;
        string projectFile = Path.Combine(projectDirectory, "fixture.kicad_pro");
        string rootFile = Path.Combine(projectDirectory, "fixture.kicad_sch");
        string recoveryPath = completed?.StatePath ?? Path.Combine(evidence, instanceId + "-recovery.json");
        string executable = Path.Combine(FindRoot(), "automation", "artifacts", "native", "kicad", "kicad");
        var start = SyncHarnessProcessTests.ProductionStartInfo();
        start.Environment["DISPLAY"] = display;
        start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(projectDirectory, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(projectDirectory, "cache");
        await using var host = await StdioMcpFixture.StartAsync(start,
            Path.Combine(evidence, "host"), Path.Combine(evidence, "host.log"), token);
        int toolSequence = 0;
        RequireToolSuccess(await Call("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        var store = completed ?? new DesignRecoveryStore(recoveryPath);
        StoredDesignRecovery saved;
        if (completed is null)
        {
            saved = await PsuCpuFixture.InitializeRecoveryAsync(client, context, recoveryPath, token);
            // The short probe diagnoses startup/container errors before the complete design run.
            var desired = PsuCpuFixture.Desired(context, PsuCpuStage.SheetsOnly);
            byte[] desiredBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
            await File.WriteAllBytesAsync(context.DesignPath, desiredBytes, token);
            saved = store.Save(saved.State with { DesiredFileBytes = desiredBytes }, saved.RevisionToken);
            var plan = await Call("kicad_design_sync_plan", Recovery(saved));
            RequireToolSuccess(plan);
            Assert.IsTrue(plan.GetProperty("structuredContent").GetProperty("nativeRebuildRequired").GetBoolean(), plan.GetRawText());
            var applied = await Call("kicad_design_sync_apply", new
            {
                instanceId, recoveryPath, designPath = context.DesignPath,
                expectedRevisionToken = saved.RevisionToken, operationId = Guid.NewGuid().ToString("D")
            });
            RequireToolSuccess(applied);
        }
        var generated = await Capture(client, context.Root, token);
        Assert.HasCount(4, generated.Electrical.Hierarchy.Data.Instances);
        saved = store.Read()!;
        string[] schematicFiles = [.. new[] { "fixture.kicad_sch", "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" }
            .Select(name => Path.Combine(projectDirectory, name))];
        CollectionAssert.AreEquivalent(schematicFiles, Directory.GetFiles(projectDirectory, "*.kicad_sch"));
        var originalFiles = schematicFiles.Append(projectFile).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        byte[] originalXml = await File.ReadAllBytesAsync(context.DesignPath, token);
        byte[] originalRecovery = await File.ReadAllBytesAsync(recoveryPath, token);
        var originalState = generated.State.Clone();
        string originalProjectHash = Convert.ToHexStringLower(SHA256.HashData(originalFiles[projectFile]));
        await File.WriteAllTextAsync(Path.Combine(evidence, "original-files.json"), JsonSerializer.Serialize(
            originalFiles.ToDictionary(p => Path.GetFileName(p.Key), p => Convert.ToHexStringLower(SHA256.HashData(p.Value)))), token);

        // A live writer must refuse the recovery operation without changing the existing files.
        var liveRefusal = await Call("kicad_project_recovery_start", new
        {
            executable, instanceId, recoveryPath, expectedRevisionToken = saved.RevisionToken,
            softwareRendering = true
        });
        Assert.IsTrue(liveRefusal.GetProperty("isError").GetBoolean(), liveRefusal.GetRawText());
        Assert.AreEqual("project_owned", liveRefusal.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        foreach (var (path, bytes) in originalFiles) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));

        var close = await Call("kicad_document_close", new
        {
            instanceId, expectedStateJson = SchematicJson.Formatter.Format(generated.State),
            operationId = Guid.NewGuid().ToString("D")
        });
        RequireToolSuccess(close);
        // Exercise KiCad's File > Quit path: SIGTERM skips CloseProject and does not
        // settle the local-history save tag, so it is a crash, not a clean project close.
        NativeKeyboard.SchematicShortcut(display, native.Id, "q", "KiCad", controlKey: true, focusCanvas: false);
        using (var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            stopDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            await native.WaitForExitAsync(stopDeadline.Token);
        }
        Assert.AreEqual(0, native.ExitCode, "The fixture manager must close normally before losing project files.");
        await WaitForStopped(host, instanceId, token);
        var existing = await Call("kicad_project_recovery_create", Recovery(saved));
        RequireToolSuccess(existing);
        Assert.IsFalse(existing.GetProperty("structuredContent").GetProperty("created").GetBoolean());
        Assert.AreEqual(originalProjectHash, existing.GetProperty("structuredContent").GetProperty("observedFileSha256").GetString());
        foreach (var (file, bytes) in originalFiles) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file, token));

        foreach (string file in schematicFiles) File.Delete(file);
        File.Delete(projectFile);
        Assert.IsFalse(File.Exists(projectFile));
        Assert.IsEmpty(Directory.EnumerateFiles(projectDirectory, "*.kicad_sch"));
        var stale = await Call("kicad_project_recovery_start", new
        {
            executable, instanceId, recoveryPath, expectedRevisionToken = "stale", softwareRendering = true
        });
        Assert.IsTrue(stale.GetProperty("isError").GetBoolean());
        Assert.AreEqual("design_recovery_changed", stale.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.IsFalse(File.Exists(projectFile));
        var wrongPath = await Call("kicad_project_recovery_create", new
        {
            instanceId, recoveryPath, expectedRevisionToken = saved.RevisionToken,
            expectedProjectFile = Path.Combine(projectDirectory, "other.kicad_pro")
        });
        Assert.IsTrue(wrongPath.GetProperty("isError").GetBoolean());
        Assert.AreEqual("project_identity_mismatch", wrongPath.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.IsFalse(File.Exists(projectFile));
        Assert.IsFalse(File.Exists(Path.Combine(projectDirectory, "other.kicad_pro")));
        CollectionAssert.AreEqual(originalXml, await File.ReadAllBytesAsync(context.DesignPath, token));
        CollectionAssert.AreEqual(originalRecovery, await File.ReadAllBytesAsync(recoveryPath, token));

        JsonElement started;
        try
        {
            started = await Call("kicad_project_recovery_start", new
            {
                executable, instanceId, recoveryPath, expectedRevisionToken = saved.RevisionToken,
                softwareRendering = true
            });
        }
        finally
        {
            // Even a timed-out startup can leave a native process. Adopt its exact launch or
            // registration PID into the harness cleanup before inspecting the tool result.
            foreach (string receipt in new[] { Path.Combine(evidence, "host", instanceId + ".json"),
                         Path.Combine(evidence, "host", "launches", instanceId + ".json") })
            {
                if (!File.Exists(receipt)) continue;
                using var record = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, CancellationToken.None));
                if (record.RootElement.GetProperty("ProcessId") is { ValueKind: JsonValueKind.Number } pid
                    && pid.GetInt32() != native.Id && ProcessIdentity.RunsInstance(pid.GetInt32(), instanceId))
                {
                    try { ownProcess(Process.GetProcessById(pid.GetInt32())); }
                    catch (ArgumentException) { }
                    break;
                }
            }
            foreach (string name in new[] { "native.log", "bootstrap.stderr.log", "bootstrap.stdout.log" })
            {
                string log = Path.Combine(NativeIpcEndpoint.RuntimeDirectory(instanceId), name);
                if (File.Exists(log)) File.Copy(log, Path.Combine(evidence, name), overwrite: true);
            }
        }
        try
        {
            RequireToolSuccess(started);
            var startedContent = started.GetProperty("structuredContent");
            Assert.AreEqual(instanceId, startedContent.GetProperty("instanceId").GetString());
            int processId = startedContent.GetProperty("processId").GetInt32();
            string epoch = startedContent.GetProperty("epoch").GetString()!;
            Assert.AreEqual(Path.GetFullPath(projectFile), startedContent.GetProperty("projectPath").GetString());
            Assert.IsTrue(File.Exists(projectFile));
            string skeletonProjectHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(projectFile, token)));

            var created = await Call("kicad_schematic_create", new { instanceId, path = rootFile });
            RequireToolSuccess(created);
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(created.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(projectDirectory), Path.TrimEndingDirectorySeparator(document.Project.Path));
            Assert.AreEqual(context.Root, document, "The rebuilt container retains the original root instance identity.");
            Assert.AreEqual("fixture", document.Project.Name);
            var restarted = new NativeClient(new NngTransport(),
                NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(instanceId), "api.sock")), epoch);
            var handshake = await restarted.HandshakeAsync(token);
            Assert.AreEqual(instanceId, handshake.InstanceId);
            Assert.AreNotEqual(client.Epoch, handshake.Epoch);
            Assert.AreEqual(processId, (int)handshake.ProcessId);
            Assert.AreNotEqual(native.Id, processId);
            Assert.AreEqual(projectFile, handshake.ProjectPath);
            await registry.AttachInstanceAsync(restarted.Endpoint, instanceId, token);
            Assert.AreEqual(epoch, registry.Client(instanceId).Epoch);
            var empty = await restarted.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = restarted.Epoch }, token);
            RequireToolSuccess(await Call("kicad_design_recovery_reattach", new
            {
                instanceId, recoveryPath, expectedRevisionToken = saved.RevisionToken,
                expectedDocumentEpoch = empty.State.Revision.Epoch
            }));
            var rebuiltPlan = await Call("kicad_design_sync_plan", Recovery(store.Read()!));
            RequireToolSuccess(rebuiltPlan);
            Assert.IsTrue(rebuiltPlan.GetProperty("structuredContent").GetProperty("nativeRebuildRequired").GetBoolean(), rebuiltPlan.GetRawText());
            var rebuiltApply = await Call("kicad_design_sync_apply", new
            {
                instanceId, recoveryPath, designPath = context.DesignPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D")
            });
            RequireToolSuccess(rebuiltApply);
            var recovered = await restarted.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document.Clone(), ProcessEpoch = restarted.Epoch }, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "before.json"), SchematicJson.Formatter.Format(generated), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "after.json"), SchematicJson.Formatter.Format(recovered), token);
            Assert.AreEqual(originalState.StateSha256, recovered.State.StateSha256, "Typed project settings survive project-file recovery.");
            Assert.AreEqual(originalState.SaveStableStateSha256, recovered.State.SaveStableStateSha256);
            foreach (var (path, bytes) in originalFiles)
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token), Path.GetFileName(path));
            Assert.AreEqual(RebuildWithoutProvenance(generated.Electrical.Hierarchy.Data),
                RebuildWithoutProvenance(recovered.Electrical.Hierarchy.Data));
            CollectionAssert.AreEqual(RebuildPartition(generated.Electrical), RebuildPartition(recovered.Electrical));
            if (completeDesign) PsuCpuFixture.AssertNative(store.Read()!.State.Baseline, recovered.Electrical, PsuCpuStage.Complete);
            Assert.IsFalse(recovered.State.NativeContentDirty);
            var secondPlan = await Call("kicad_design_sync_plan", Recovery(store.Read()!));
            RequireToolSuccess(secondPlan);
            Assert.IsFalse(secondPlan.GetProperty("structuredContent").GetProperty("nativeRebuildRequired").GetBoolean());
            Assert.AreEqual(0, secondPlan.GetProperty("structuredContent").GetProperty("nativeOperationsJson").GetArrayLength());
            var second = await Call("kicad_design_sync_apply", new
            {
                instanceId, recoveryPath, designPath = context.DesignPath,
                expectedRevisionToken = store.Read()!.RevisionToken, operationId = Guid.NewGuid().ToString("D")
            });
            RequireToolSuccess(second);
            Assert.IsFalse(second.GetProperty("structuredContent").GetProperty("nativeMutationCommitted").GetBoolean());
            Assert.IsFalse(second.GetProperty("structuredContent").GetProperty("nativeFilesSaved").GetBoolean());
            Assert.AreEqual(recovered, await Capture(restarted, document, token));
            string recoveredProjectHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(projectFile, token)));
            await File.WriteAllTextAsync(Path.Combine(evidence, "proof.json"), JsonSerializer.Serialize(new
            {
                instanceId, completeDesign, projectFileCreated = true, sameInstance = true, projectFilePreservedBeforeStart = true,
                refusalPreservedFiles = true, typedSettingsRestored = originalState.StateSha256 == recovered.State.StateSha256,
                schematicFilesByteIdentical = true, recoveredProcessId = processId, epoch,
                originalState.StateSha256, recoveredState = recovered.State.StateSha256,
                originalProjectHash, skeletonProjectHash, recoveredProjectHash, secondApplyNoOp = true
            }), token);

        }
        finally
        {
            // Retain failures after startup too, including native create, reattach and rebuild.
            foreach (string name in new[] { "native.log", "bootstrap.stderr.log", "bootstrap.stdout.log" })
            {
                string log = Path.Combine(NativeIpcEndpoint.RuntimeDirectory(instanceId), name);
                if (File.Exists(log)) File.Copy(log, Path.Combine(evidence, name), overwrite: true);
            }
        }

        async Task<JsonElement> Call(string name, object args)
        {
            var reply = await host.Tool(name, args);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"{++toolSequence:D2}-{name}.json"), reply.GetRawText(), token);
            return reply;
        }
        object Recovery(StoredDesignRecovery value) => new
        {
            instanceId, recoveryPath, expectedRevisionToken = value.RevisionToken
        };
    }

    private static async Task<CheckedSchematicState> Capture(NativeClient client, DocumentSpecifier document,
        CancellationToken token) => await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
        { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);

    private static async Task WaitForStopped(StdioMcpFixture host, string instanceId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var listed = await host.Tool("kicad_instances_list", new { });
            RequireToolSuccess(listed);
            using var parsed = JsonDocument.Parse(listed.GetProperty("content").EnumerateArray()
                .Single(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            var row = parsed.RootElement.EnumerateArray().Single(x => x.GetProperty("instanceId").GetString() == instanceId);
            if (row.GetProperty("processState").GetString() == "exited") return;
            await Task.Delay(100, deadline.Token);
        }
    }
}
