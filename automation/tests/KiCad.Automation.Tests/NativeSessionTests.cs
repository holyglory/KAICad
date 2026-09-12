using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Real KiCad manager/editor processes on a test-owned X server. Focused
// diagnostic entry points reuse the same fixture; full qualification is separate.
[TestClass]
[TestCategory("NativeSession")]
public sealed partial class NativeSessionTests
{
    [TestMethod, TestCategory("NativeSourceSession")]
    public Task TwoNativeProjectsHaveIndependentEpochsAndCanReattach() => RunNativeSessions(NativeJourney.Foundation);

    [TestMethod, TestCategory("NativeTableVariant")]
    public Task TableVariantActionsPreserveNativeHistory() => RunNativeSessions(NativeJourney.TableVariants);

    [TestMethod, TestCategory("NativeNetChains")]
    public Task NetChainMetadataPreservesNativeHistory() => RunNativeSessions(NativeJourney.NetChains);

    [TestMethod, TestCategory("NativeSetupDraft")]
    public Task SetupDraftPreservesCancelledPageChangesAndNativeHistory() => RunNativeSessions(NativeJourney.Setup);

    private enum NativeJourney { Foundation, TableVariants, NetChains, Setup }

    private async Task RunNativeSessions(NativeJourney journey)
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "This virtual-display check is Linux-only, not native Mac evidence.");
        string root = FindRoot();
        string executable = Path.Combine(root, "automation", "artifacts", "native", "kicad", "kicad");
        Assert.IsTrue(File.Exists(executable), "The linked native manager must be built first.");
        string artifacts = Path.Combine(root, "automation", "artifacts");
        string evidence = NativeEvidenceDirectory.Begin(journey == NativeJourney.Foundation ? artifacts
            : Path.Combine(artifacts, journey switch { NativeJourney.TableVariants => "native-table-variants",
                NativeJourney.Setup => "native-setup-draft", _ => "native-net-chains" }));
        string temporary = Directory.CreateTempSubdirectory("kicad-native-").FullName;
        // The earlier composed journey took 433s before expanded Setup and
        // annotation coverage. Keep all per-action limits and focused ceilings;
        // only the full two-editor sequence receives the measured workload margin.
        int aggregateSeconds = journey == NativeJourney.Foundation ? 600 : 300;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(aggregateSeconds));
        var elapsed = Stopwatch.StartNew();
        var processes = new List<Process>();
        var captures = new List<Task>();
        try
        {
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
                displayStart.ArgumentList.Add(arg);
            Process display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "xvfb.stderr.log")));
            string? displayNumber = await display.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsTrue(int.TryParse(displayNumber, out _), "Xvfb did not allocate a display.");
            if (journey == NativeJourney.Foundation)
            {
                await VerifyCliCreationRejection(executable, ":" + displayNumber, temporary, evidence, deadline.Token);
                await VerifyRootCreationRejections(executable, ":" + displayNumber, temporary, evidence, deadline.Token);
                await VerifyInterruptedStartup(executable, ":" + displayNumber, temporary, evidence, deadline.Token);
                await VerifyFailedStartup(executable, temporary, evidence, deadline.Token);
            }
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(temporary, "registry"));
            var launched = new List<(string Id, string Endpoint, string Project, string RootId)>();
            for (int index = 0; index < 2; index++)
            {
                string id = Guid.NewGuid().ToString("D");
                string projectDirectory = Path.Combine(temporary, index.ToString());
                Directory.CreateDirectory(projectDirectory);
                string project = Path.Combine(projectDirectory, "fixture.kicad_pro");
                string declaredRootId = (index == 0 ? Guid.NewGuid() : Guid.Empty).ToString("D");
                // PROJECT_FILE schema 3 requires meta.version; an empty object
                // deliberately fails JSON_SETTINGS::LoadFromFile validation.
                await File.WriteAllTextAsync(project, System.Text.Json.JsonSerializer.Serialize(new
                {
                    meta = new { version = 3 }, text_variables = new { ENGINEERING_NOTE = "電源 & timing" },
                    net_settings = new { meta = new { version = 5 }, net_chain_class_definitions = new[] { "emptygroup" }, net_chain_classes = new Dictionary<string, string>
                        { ["AUTOMATION_PATH"] = "fastbus", ["UNAFFECTED_CHAIN"] = "preserved" } },
                    schematic = new
                    {
                        top_level_sheets = new[] { new { uuid = declaredRootId, name = "fixture", filename = "fixture.kicad_sch" } },
                        bus_aliases = new { DATA = new[] { "D0", "D1", "ENABLE" } },
                        variants = new[] { new { name = "Assembly", description = "original" } }
                    }
                }), deadline.Token);
                string socket = Path.Combine(projectDirectory, "api.sock");
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = projectDirectory, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.Environment["DISPLAY"] = ":" + displayNumber;
                start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
                start.Environment["WXTRACE"] = "KICAD_SETTINGS";
                start.Environment["XDG_CONFIG_HOME"] = Path.Combine(projectDirectory, "config");
                start.Environment["XDG_CACHE_HOME"] = Path.Combine(projectDirectory, "cache");
                foreach (string arg in new[] { "--new", "--automation", id, "--api-socket", socket,
                                               "--automation-log", Path.Combine(evidence, $"native-{index}.log"),
                                               "--software-rendering", project })
                    start.ArgumentList.Add(arg);
                Process native = Process.Start(start)!;
                processes.Add(native);
                captures.Add(Capture(native.StandardOutput, Path.Combine(evidence, $"native-{index}.stdout.log")));
                captures.Add(Capture(native.StandardError, Path.Combine(evidence, $"native-{index}.stderr.log")));
                launched.Add((id, "ipc://" + socket, project, declaredRootId));
            }

            foreach (var target in launched)
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Assert.IsTrue(processes.All(p => !p.HasExited),
                        $"A fixture process exited during startup. Native diagnostics: {evidence}");
                    try { await registry.AttachAsync(target.Endpoint, target.Id, deadline.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, deadline.Token);
                }
            }
            Assert.AreEqual(2, registry.List().Count);
            Assert.AreNotEqual(registry.Get(launched[0].Id).Epoch, registry.Get(launched[1].Id).Epoch);
            foreach (var target in launched)
            {
                Assert.AreEqual(target.Project, registry.Get(target.Id).ProjectPath);
                Assert.IsFalse(string.IsNullOrEmpty((await registry.Client(target.Id).GetVersionAsync(deadline.Token)).Version.FullVersion));
            }
            var reattached = new InstanceRegistry(new NngTransport(), Path.Combine(temporary, "registry"));
            await reattached.ReattachAsync(launched[0].Id, deadline.Token);
            AutomationException mismatch = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                registry.AttachAsync(launched[1].Endpoint, launched[0].Id, deadline.Token));
            Assert.AreEqual("instance_mismatch", mismatch.Code);
            Assert.IsTrue(processes.All(p => !p.HasExited));

            // Open actual graphical editors through the manager's own IPC peer.
            // The fixture has native drawing content, not a mocked editor result.
            foreach (var target in launched)
            {
                Console.WriteLine($"Native editor journey {target.Id} started at {elapsed.Elapsed.TotalSeconds:F1}s.");
                NativeClient client = registry.Client(target.Id);
                var nativeVersion = (await client.GetVersionAsync(deadline.Token)).Version;
                string hotkeyDirectory = Path.Combine(Path.GetDirectoryName(target.Project)!, "config", "kicad",
                    $"{nativeVersion.Major}.{nativeVersion.Minor}");
                Directory.CreateDirectory(hotkeyDirectory);
                // Only the fixture's isolated user configuration is changed.
                await File.WriteAllTextAsync(Path.Combine(hotkeyDirectory, "user.hotkeys"),
                    "common.Control.pageSettings\tCtrl+F12\t\n", deadline.Token);
                string schematic = Path.ChangeExtension(target.Project, ".kicad_sch");
                var missing = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<OpenDocument, OpenDocumentResponse>(
                        new() { Type = (DocumentType)1, Path = schematic }, deadline.Token));
                Assert.AreEqual(3, missing.Status);
                var emptyRoot = await VerifyEmptyRootCreation(client, schematic,
                    Path.ChangeExtension(launched.Single(p => p.Id != target.Id).Project, ".kicad_sch"),
                    evidence, target.Id, target.RootId, deadline.Token);
                // Saving the native-created root records its instance identity
                // in the project. Reuse that identity in the populated fixture.
                string rootId = emptyRoot.SheetPath.Path[0].Value;
                string rootScreenId = Guid.NewGuid().ToString("D");
                Assert.AreNotEqual(rootId, rootScreenId, "Exercise distinct project-instance and file-screen identities.");
                string textId = Guid.NewGuid().ToString("D");
                var electrical = MakeElectricalFixture(rootId);
                var hierarchyFixture = await MakeHierarchyFixture(rootId, Path.GetDirectoryName(schematic)!, deadline.Token);
                var embeddedAsset = await MakeEmbeddedAssetFixture(root, deadline.Token);
                Assert.AreEqual(1, hierarchyFixture.Contents.Split("(page \"2\")", StringSplitOptions.None).Length - 1);
                // Deliberately duplicate the root's page number. Native loading
                // must repair it to page 2 without a modal acknowledgement.
                string repairableHierarchy = hierarchyFixture.Contents.Replace("(page \"2\")", "(page \"1\")", StringComparison.Ordinal);
                await File.WriteAllTextAsync(schematic,
                    $"(kicad_sch (version 20250114) (generator eeschema) (uuid {rootScreenId}) (paper \"A4\") " + electrical.Contents + repairableHierarchy +
                    "(text \"Native automation fixture\" (at 50 50 0) (effects (font (size 1.27 1.27))) " +
                    $"(uuid {textId})) (sheet_instances (path \"/\" (page \"1\"))) {embeddedAsset.Native})", deadline.Token);
                // The native-created empty document was saved and checked above.
                // Load this separate serialized-feature fixture explicitly; opening
                // again must never silently replace the existing native document.
                try
                {
                    await client.InvokeAsync<RevertDocument, Empty>(new() { Document = emptyRoot }, deadline.Token);
                }
                catch
                {
                    await NativeKeyboard.CaptureAsync(":" + displayNumber,
                        Path.Combine(evidence, target.Id + "-root-fixture-reload-failed.png"), CancellationToken.None);
                    throw;
                }
                OpenDocumentResponse opened = await client.OpenRootSchematicAsync(schematic, deadline.Token);
                Assert.AreEqual(rootId, opened.Document.SheetPath.Path[0].Value);
                var repairedHierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                    new() { Document = opened.Document }, deadline.Token);
                var repairedFirst = repairedHierarchy.Data.Instances[0].Items.Single(i =>
                    i.Is(Kiapi.Schematic.Types.SheetSymbol.Descriptor)
                    && i.Unpack<Kiapi.Schematic.Types.SheetSymbol>().Id.Value == hierarchyFixture.First)
                    .Unpack<Kiapi.Schematic.Types.SheetSymbol>();
                Assert.AreEqual("2", repairedFirst.PageNumber);
                Assert.IsTrue((await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = opened.Document }, deadline.Token)).UnsavedSchematicChanges,
                    "Load-time repairs must remain unsaved until explicitly saved.");
                GetOpenDocumentsResponse documents = await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                    new() { Type = (DocumentType)1 }, deadline.Token);
                Assert.AreEqual(opened.Document, documents.Documents.Single());
                OpenDocumentResponse repeated = await client.OpenRootSchematicAsync(schematic, deadline.Token);
                Assert.AreEqual(opened.Document, repeated.Document);
                string otherProject = launched.Single(p => p.Id != target.Id).Project;
                var wrongProject = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.OpenRootSchematicAsync(Path.ChangeExtension(otherProject, ".kicad_sch"), deadline.Token));
                Assert.AreEqual(3, wrongProject.Status);
                Assert.AreEqual(opened.Document, (await client.OpenRootSchematicAsync(schematic, deadline.Token)).Document);
                SchematicPreview preview;
                while (true)
                {
                    try
                    {
                        preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                            new() { Document = opened.Document }, deadline.Token);
                        break;
                    }
                    catch (NativeApiException error) when (error.Status == 4)
                    {
                        await Task.Delay(100, deadline.Token);
                    }
                }
                Assert.AreEqual(opened.Document, preview.Document);
                Assert.IsGreaterThan(100u, preview.WidthPixels);
                Assert.IsGreaterThan(100u, preview.HeightPixels);
                byte[] png = preview.Png.ToByteArray();
                CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
                Assert.AreEqual(preview.WidthPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
                Assert.AreEqual(preview.HeightPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
                await File.WriteAllBytesAsync(Path.Combine(evidence, $"{target.Id}-schematic.png"), png, deadline.Token);
                var wrongSheet = opened.Document.Clone();
                wrongSheet.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
                var rejectedPreview = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                        new() { Document = wrongSheet }, deadline.Token));
                Assert.AreEqual(3, rejectedPreview.Status);
                Assert.AreEqual(opened.Document, (await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                    new() { Document = opened.Document }, deadline.Token)).Document);
                if (journey != NativeJourney.Foundation)
                {
                    int focusProcessId = processes.Single(p => p.StartInfo.ArgumentList.Contains(target.Project)).Id;
                    await client.InvokeAsync<SaveDocument, Empty>(new() { Document = opened.Document }, deadline.Token);
                    Console.WriteLine($"Focused {journey} {target.Id} reached its target at {elapsed.Elapsed.TotalSeconds:F1}s.");
                    if (journey == NativeJourney.Setup)
                    {
                        await VerifyAnnotation(client, opened.Document, focusProcessId, ":" + displayNumber,
                            evidence, deadline.Token);
                        await VerifyManualSetup(client, opened.Document, focusProcessId, ":" + displayNumber,
                            evidence, target.Id, deadline.Token);
                        await VerifySetupPinMap(client, opened.Document, focusProcessId, ":" + displayNumber,
                            evidence, target.Id, deadline.Token);
                        await VerifySetupImport(client, opened.Document, focusProcessId, ":" + displayNumber,
                            evidence, target.Id, deadline.Token);
                        await VerifySetupAssets(client, opened.Document, focusProcessId, ":" + displayNumber,
                            evidence, target.Id, deadline.Token);
                    }
                    else if (journey == NativeJourney.TableVariants)
                        await VerifyTableVariantEdits(client, opened.Document, schematic, focusProcessId,
                            ":" + displayNumber, evidence, deadline.Token);
                    else
                        await VerifyNetChainMetadata(client, opened.Document, schematic, electrical, focusProcessId,
                            ":" + displayNumber, evidence, deadline.Token);
                    Console.WriteLine($"Focused {journey} {target.Id} completed at {elapsed.Elapsed.TotalSeconds:F1}s.");
                    continue;
                }
                var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = opened.Document }, deadline.Token);
                Assert.IsTrue(journal.ResetRequired);
                Assert.IsFalse(journal.TrackingComplete, "Partial edit-path coverage must not advertise a safe revision token.");
                Assert.IsFalse(preview.TrackingComplete);
                Assert.AreEqual(journal.DocumentEpoch, preview.Revision.Epoch);
                Assert.AreEqual(journal.Sequence, preview.Revision.Sequence);
                Assert.IsTrue(double.IsFinite(preview.Viewport.OriginXNm));
                Assert.IsTrue(double.IsFinite(preview.Viewport.OriginYNm));
                Assert.IsGreaterThan(0.0, preview.Viewport.PixelXDxNm);
                Assert.IsGreaterThan(0.0, preview.Viewport.PixelYDyNm);
                Assert.AreEqual(0.0, preview.Viewport.PixelXDyNm, 0.001);
                Assert.AreEqual(0.0, preview.Viewport.PixelYDxNm, 0.001);
                Assert.IsNotEmpty(preview.Viewport.VisibleNativeLayers);
                Assert.IsFalse(string.IsNullOrWhiteSpace(journal.DocumentEpoch));
                var cursor = new ReadSchematicChangeJournal
                {
                    Document = opened.Document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
                };
                var header = new ItemHeader { Document = opened.Document };
                var query = new GetItemsById { Header = header };
                query.Items.Add(new KIID { Value = textId });
                var fetched = await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, deadline.Token);
                var originalText = fetched.Items.Single().Unpack<Kiapi.Schematic.Types.SchematicText>();
                var moved = originalText.Clone();
                var boundsQuery = new GetBoundingBox { Header = header, Mode = (BoundingBoxMode)2 };
                boundsQuery.Items.Add(new KIID { Value = textId });
                var originalBounds = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(boundsQuery, deadline.Token);
                Assert.AreEqual(textId, originalBounds.Items.Single().Value);
                Assert.IsGreaterThan(0L, originalBounds.Boxes.Single().Size.XNm);
                Assert.IsGreaterThan(0L, originalBounds.Boxes.Single().Size.YNm);
                var missingBounds = boundsQuery.Clone(); missingBounds.Items.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
                Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(missingBounds, deadline.Token))).Status);
                moved.Text.Position.XNm += 10000000;
                var update = new UpdateItems { Header = header };
                update.Items.Add(Any.Pack(moved));
                var updated = await client.InvokeAsync<UpdateItems, UpdateItemsResponse>(update, deadline.Token);
                Assert.AreEqual(1, (int)updated.UpdatedItems.Single().Status.Code);
                var afterEdit = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token);
                Assert.IsFalse(afterEdit.ResetRequired);
                Assert.IsGreaterThan(journal.Sequence, afterEdit.Sequence);
                Assert.AreEqual(SchematicChange.Types.Kind.Commit, afterEdit.Changes.Last().Kind);
                cursor.AfterSequence = afterEdit.Sequence;
                Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token)).Changes.Count);

                // Exercise keyboard input through the rendered native window,
                // then verify the resulting model and journal over IPC.
                int nativeProcessId = processes.Single(p => p.StartInfo.ArgumentList.Contains(target.Project)).Id;
                foreach (var (key, kind) in new[] { ("z", SchematicChange.Types.Kind.Undo),
                                                    ("y", SchematicChange.Types.Kind.Redo) })
                {
                    NativeKeyboard.SchematicShortcut(":" + displayNumber, nativeProcessId, key);
                    SchematicChangeJournal changed;
                    using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    // Test-owned bounded watcher: XSync confirms delivery to
                    // the X server, not processing by the editor's event loop.
                    do
                    {
                        changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                        if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
                    } while (changed.Changes.Count == 0);
                    Assert.AreEqual(kind, changed.Changes.Last().Kind);
                    cursor.AfterSequence = changed.Sequence;
                    var actual = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, deadline.Token))
                        .Items.Single().Unpack<Kiapi.Schematic.Types.SchematicText>();
                    Assert.AreEqual(kind == SchematicChange.Types.Kind.Undo ? originalText.Text.Position : moved.Text.Position,
                        actual.Text.Position);
                }
                var afterRedo = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                    new() { Document = opened.Document }, deadline.Token);
                var movedBounds = (await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(boundsQuery, deadline.Token)).Boxes.Single();
                Assert.AreEqual(originalBounds.Boxes.Single().Position.XNm + 10000000, movedBounds.Position.XNm);
                Assert.AreEqual(originalBounds.Boxes.Single().Position.YNm, movedBounds.Position.YNm);
                Assert.AreEqual(originalBounds.Boxes.Single().Size, movedBounds.Size);
                Assert.AreEqual(cursor.AfterSequence, afterRedo.Revision.Sequence);
                Assert.AreEqual(preview.Revision.Epoch, afterRedo.Revision.Epoch);
                await File.WriteAllBytesAsync(Path.Combine(evidence, $"{target.Id}-after-redo.png"),
                    afterRedo.Png.ToByteArray(), deadline.Token);

                // Dropping a pending native commit restores the model and must
                // not publish it as an accepted user edit.
                var begin = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, deadline.Token);
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(boundsQuery, deadline.Token))).Status);
                byte[] persistedBeforeStaging = await File.ReadAllBytesAsync(schematic, deadline.Token);
                string blockedCopy = Path.Combine(Path.GetDirectoryName(schematic)!, "blocked-copy.kicad_sch");
                update.Items.Clear();
                update.Items.Add(Any.Pack(originalText));
                await client.InvokeAsync<UpdateItems, UpdateItemsResponse>(update, deadline.Token);
                var observer = new NativeClient(new NngTransport(), target.Endpoint, client.Epoch);
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    observer.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, deadline.Token))).Status);
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    observer.InvokeAsync<UpdateItems, UpdateItemsResponse>(update, deadline.Token))).Status);
                var competingCreate = new CreateItems { Header = header };
                competingCreate.Items.Add(Any.Pack(originalText));
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    observer.InvokeAsync<CreateItems, CreateItemsResponse>(competingCreate, deadline.Token))).Status);
                var competingDelete = new DeleteItems { Header = header };
                competingDelete.ItemIds.Add(new KIID { Value = textId });
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    observer.InvokeAsync<DeleteItems, DeleteItemsResponse>(competingDelete, deadline.Token))).Status);
                foreach (var observingClient in new[] { client, observer })
                {
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                            new() { Document = opened.Document }, deadline.Token))).Status);
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<GetPageSettings, PageSettings>(
                            new() { Document = opened.Document }, deadline.Token))).Status);
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<SetPageSettings, PageSettings>(new()
                        {
                            Document = opened.Document, PageSettings = new PageSettings
                            { PageSize = (PageSize)2, Orientation = (PageOrientation)1 }
                        }, deadline.Token))).Status);
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<SaveDocument, Empty>(new() { Document = opened.Document }, deadline.Token))).Status);
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<SaveCopyOfDocument, Empty>(new()
                        {
                            Document = opened.Document, Path = blockedCopy,
                            Options = new SaveOptions { Overwrite = false, IncludeProject = false }
                        }, deadline.Token))).Status);
                    Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<SetTitleBlockInfo, Empty>(new()
                        {
                            Document = opened.Document, TitleBlock = new TitleBlockInfo { Title = "Must not leak from staging" }
                        }, deadline.Token))).Status);
                    var blockedImage = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                            new() { Document = opened.Document }, deadline.Token));
                    Assert.AreEqual(7, blockedImage.Status);
                    var blockedHistory = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        observingClient.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token));
                    Assert.AreEqual(7, blockedHistory.Status);
                }
                CollectionAssert.AreEqual(persistedBeforeStaging, await File.ReadAllBytesAsync(schematic, deadline.Token));
                Assert.IsFalse(File.Exists(blockedCopy));
                await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
                {
                    Header = header, Id = begin.Id, Action = (CommitAction)2
                }, deadline.Token);
                Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token)).Changes.Count);
                Assert.AreEqual(moved.Text.Position, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, deadline.Token))
                    .Items.Single().Unpack<Kiapi.Schematic.Types.SchematicText>().Text.Position);
                Assert.AreEqual(opened.Document, (await observer.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                    new() { Document = opened.Document }, deadline.Token)).Document);
                var unsaved = await observer.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = opened.Document }, deadline.Token);
                Assert.IsTrue(unsaved.UnsavedSchematicChanges);
                CollectionAssert.Contains(unsaved.ModifiedSheetInstances.ToArray(), opened.Document);
                Assert.IsFalse(unsaved.ProjectSettingsChecked);
                await observer.InvokeAsync<SaveDocument, Empty>(new() { Document = opened.Document }, deadline.Token);
                var saved = await observer.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = opened.Document }, deadline.Token);
                Assert.IsFalse(saved.UnsavedSchematicChanges);
                Assert.IsEmpty(saved.ModifiedSheetInstances);
                Assert.IsFalse(saved.TrackingComplete);
                byte[] persistedAfterCancellation = await File.ReadAllBytesAsync(schematic, deadline.Token);
                Assert.IsFalse(persistedBeforeStaging.SequenceEqual(persistedAfterCancellation),
                    "A completed edit can be saved after the pending transaction is cancelled.");
                Assert.AreEqual("", (await observer.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                    new() { Document = opened.Document }, deadline.Token)).Title);
                // Cancellation releases the writer reservation for another client.
                var nextWriter = await observer.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, deadline.Token);
                await observer.InvokeAsync<EndCommit, EndCommitResponse>(new()
                {
                    Header = header, Id = nextWriter.Id, Action = (CommitAction)2
                }, deadline.Token);

                var titleBlock = new TitleBlockInfo
                {
                    Title = "Controller — 電源", Date = "2026-09-05", Revision = "A.1",
                    Company = "Fixture engineering", Comment1 = "Keep return paths short",
                    Comment2 = "Line one\nLine two", Comment9 = "Final comment"
                };
                var titleRequest = new SetTitleBlockInfo { Document = opened.Document, TitleBlock = titleBlock };
                var beforeTitle = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token);
                cursor.AfterSequence = beforeTitle.Sequence;
                var ambiguousTitle = titleRequest.Clone();
                ambiguousTitle.Document.SheetPath = null;
                Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<SetTitleBlockInfo, Empty>(ambiguousTitle, deadline.Token))).Status);
                Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<SetTitleBlockInfo, Empty>(new() { Document = opened.Document }, deadline.Token))).Status);
                Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token)).Changes.Count);

                await client.InvokeAsync<SetTitleBlockInfo, Empty>(titleRequest, deadline.Token);
                Assert.AreEqual(titleBlock, await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                    new() { Document = opened.Document }, deadline.Token));
                var titleChanged = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token);
                Assert.AreEqual(1, titleChanged.Changes.Count);
                Assert.AreEqual(SchematicChange.Types.Kind.Commit, titleChanged.Changes[0].Kind);
                cursor.AfterSequence = titleChanged.Sequence;
                await client.InvokeAsync<SetTitleBlockInfo, Empty>(titleRequest, deadline.Token);
                Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, deadline.Token)).Changes.Count);

                foreach (var (key, kind) in new[] { ("z", SchematicChange.Types.Kind.Undo), ("y", SchematicChange.Types.Kind.Redo) })
                {
                    NativeKeyboard.SchematicShortcut(":" + displayNumber, nativeProcessId, key);
                    using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    SchematicChangeJournal changed;
                    do
                    {
                        changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                        if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
                    } while (changed.Changes.Count == 0);
                    Assert.AreEqual(kind, changed.Changes.Last().Kind);
                    cursor.AfterSequence = changed.Sequence;
                    var actualTitle = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                        new() { Document = opened.Document }, deadline.Token);
                    Assert.AreEqual(kind == SchematicChange.Types.Kind.Undo ? new TitleBlockInfo() : titleBlock, actualTitle);
                }
                var titlePreview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                    new() { Document = opened.Document }, deadline.Token);
                await File.WriteAllBytesAsync(Path.Combine(evidence, $"{target.Id}-title-redo.png"), titlePreview.Png.ToByteArray(), deadline.Token);
                await VerifyPageSettings(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    Path.GetDirectoryName(schematic)!, evidence, target.Id, deadline.Token);
                await VerifyAtomicBatch(client, opened.Document, textId, nativeProcessId,
                    ":" + displayNumber, deadline.Token);
                await VerifyNativeItemDelta(client, opened.Document, textId, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyElectricalBatch(client, opened.Document, electrical, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyHierarchyBatch(client, opened.Document, hierarchyFixture, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                var retainedOperation = await VerifyNativeRetry(client, opened.Document, textId, evidence, deadline.Token);
                await VerifyMcpEditor(root, target.Id, target.Endpoint, client, opened.Document,
                    hierarchyFixture.First, evidence, retainedOperation, electrical, nativeProcessId,
                    ":" + displayNumber, deadline.Token);
                await VerifyNativePresentation(client, opened.Document, textId, electrical.Symbol,
                    evidence, target.Id, deadline.Token);
                await VerifyManualPageSettings(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, target.Id, textId, hierarchyFixture, deadline.Token);
                await VerifyManualSetup(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, target.Id, deadline.Token);
                await VerifyAnnotation(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, deadline.Token);
                await VerifySetupPinMap(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, target.Id, deadline.Token);
                await VerifySetupImport(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, target.Id, deadline.Token);
                await VerifySetupAssets(client, opened.Document, nativeProcessId, ":" + displayNumber,
                    evidence, target.Id, deadline.Token);
                await VerifyNativeEvents(client, registry.Client(launched.Single(p => p.Id != target.Id).Id),
                    opened.Document, textId, nativeProcessId, ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifySnapshotSchemaVersions(client, opened.Document, deadline.Token);
                await VerifyNativeSymbolXml(client, opened.Document, electrical, evidence, target.Id, deadline.Token);
                await VerifyNativeCacheTransaction(client, opened.Document, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyInteractiveMoveAdmission(client, opened.Document, electrical, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyConnectedSymbolMove(client, opened.Document, electrical, textId, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyRepeatedSymbolXml(client, opened.Document, electrical, hierarchyFixture,
                    evidence, target.Id, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifySheetXml(client, opened.Document, hierarchyFixture, evidence, target.Id,
                    nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyMetadataXml(client, opened.Document, hierarchyFixture, evidence, target.Id,
                    embeddedAsset.Expected, rootScreenId, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyNativeAssetRestore(client, opened.Document, textId, nativeProcessId,
                    ":" + displayNumber, deadline.Token);
                await VerifyTitleBatch(client, opened.Document, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyGroupCreation(client, opened.Document, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyMultiSheetBatch(client, opened.Document, nativeProcessId, ":" + displayNumber, schematic, deadline.Token);
                await VerifyBusAliasConnectivity(client, opened.Document, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyTextVariableBatch(client, opened.Document, nativeProcessId, ":" + displayNumber, schematic, evidence, deadline.Token);
                await VerifyRootInstanceXml(client, opened.Document, nativeProcessId,
                    ":" + displayNumber, deadline.Token);
                await VerifySharedRootOwnership(client, opened.Document, hierarchyFixture, schematic, nativeProcessId,
                    ":" + displayNumber, evidence, target.Id, deadline.Token);
                await VerifyNetChainMetadata(client, opened.Document, schematic, electrical, nativeProcessId, ":" + displayNumber, evidence, deadline.Token);
                await VerifyVariantDescriptionDialog(client, opened.Document, schematic, nativeProcessId, ":" + displayNumber, evidence, deadline.Token);
                await VerifyVariantRegistryXml(client, opened.Document, schematic, nativeProcessId, ":" + displayNumber, deadline.Token);
                await VerifyDrawingRatios(client, opened.Document, schematic, nativeProcessId, ":" + displayNumber, evidence, deadline.Token);
                await VerifyFormatting(client, opened.Document, nativeProcessId, ":" + displayNumber, evidence, deadline.Token);
                await VerifyViewportZoom(client, opened.Document, nativeProcessId, ":" + displayNumber, evidence, deadline.Token);
                await VerifyIndependentViews(client, opened.Document, nativeProcessId, evidence, deadline.Token);
                Console.WriteLine($"Native editor journey {target.Id} completed at {elapsed.Elapsed.TotalSeconds:F1}s.");
            }

            // A competing process must fail without showing a lock-override
            // dialog or disturbing the two existing writers.
            var competingStart = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(launched[0].Project)!,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            competingStart.Environment["DISPLAY"] = ":" + displayNumber;
            competingStart.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            competingStart.Environment["XDG_CONFIG_HOME"] = Path.Combine(temporary, "competing-config");
            competingStart.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "competing-cache");
            string competingLog = Path.Combine(evidence, "competing-native.log");
            foreach (string arg in new[] { "--new", "--automation", Guid.NewGuid().ToString("D"),
                                           "--api-socket", Path.Combine(temporary, "competing.sock"),
                                           "--automation-log", competingLog,
                                           "--software-rendering", launched[0].Project })
                competingStart.ArgumentList.Add(arg);
            Process competing = Process.Start(competingStart)!;
            processes.Add(competing);
            captures.Add(Capture(competing.StandardOutput, Path.Combine(evidence, "competing.stdout.log")));
            captures.Add(Capture(competing.StandardError, Path.Combine(evidence, "competing.stderr.log")));
            using var rejectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            rejectionDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            await competing.WaitForExitAsync(rejectionDeadline.Token);
            Assert.AreNotEqual(0, competing.ExitCode, "A second writable process must reject the owned project.");
            StringAssert.Contains(await File.ReadAllTextAsync(competingLog, deadline.Token), "already open");
            foreach (var target in launched)
                Assert.AreEqual(registry.Get(target.Id).Epoch,
                    (await registry.Client(target.Id).HandshakeAsync(deadline.Token)).Epoch);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            Assert.Fail($"The aggregate native-session ceiling of {aggregateSeconds} seconds expired after {elapsed.Elapsed.TotalSeconds:F1}s. "
                + $"This is not a successful journey or an individual-operation timeout. Original cancellation: {error}");
        }
        finally
        {
            // Only disposable fixture-owned processes are terminated. No existing
            // editor is discovered, attached to, or killed by this test.
            foreach (Process process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(captures);
            // Bounded fixture diagnostics survive even when the coordinator
            // cannot retain an artifact from a failed check. Values stay cold.
            foreach (string log in Directory.EnumerateFiles(evidence, "native-?.log"))
                foreach (string line in File.ReadLines(log).Where(line =>
                    line.Contains("Schematic setup changed setting:", StringComparison.Ordinal)
                    || line.Contains("Unable to capture schematic setup settings:", StringComparison.Ordinal)).Take(16))
                    Console.WriteLine(line);
            foreach (Process process in processes) process.Dispose();
            Directory.Delete(temporary, true);
        }
    }

    private static async Task Capture(StreamReader input, string path)
    {
        await using var output = new StreamWriter(path);
        char[] buffer = new char[4096];
        int length;
        while ((length = await input.ReadAsync(buffer)) != 0) await output.WriteAsync(buffer.AsMemory(0, length));
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "automation", "KiCad.Automation.slnx"))) return current.FullName;
        throw new InvalidOperationException("No KiCad source checkout was found.");
    }
}
