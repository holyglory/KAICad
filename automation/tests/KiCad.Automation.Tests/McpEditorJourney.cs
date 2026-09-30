using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Actual compiled STDIO server -> NNG -> rendered editor, not a transport mock.
    private static async Task VerifyMcpEditor(string root, string instanceId, string endpoint,
        NativeClient native, DocumentSpecifier document, string childId, string evidence, ApplySchematicItemBatch retainedOperation,
        ElectricalFixture electrical, int processId, string display, CancellationToken token)
    {
        string operationId = retainedOperation.OperationId;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "automation", "src", "KiCad.Automation.Mcp", "bin",
            configuration, "net10.0", "kicad-mcp.dll"));
        string state = Directory.CreateTempSubdirectory("kicad-mcp-editor-").FullName;
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
        using Process process = Process.Start(start)!;
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(token);
        int nextId = 0;
        try
        {
            await Request("initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "native-editor-journey", version = "1" }
            });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listedTools = await Request("tools/list", new { });
            var eventTool = listedTools.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == "kicad_events_wait");
            Assert.AreEqual(JsonValueKind.Object, eventTool.GetProperty("outputSchema").ValueKind);
            await Call("kicad_instance_attach", new { endpoint, expectedInstanceId = instanceId });
            var beforeCreate = await native.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                new() { Document = document }, token);
            string rootPath = Path.ChangeExtension((await native.HandshakeAsync(token)).ProjectPath, ".kicad_sch");
            var existingDocuments = await native.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                new() { Type = (DocumentType)1 }, token);
            var created = await Call("kicad_schematic_create", new { instanceId, path = rootPath });
            var createdDocument = SchematicJson.Parser.Parse<DocumentSpecifier>(created.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual(existingDocuments.Documents.Single(), createdDocument);
            await Call("kicad_schematic_create", new { instanceId, path = "relative.kicad_sch" }, error: true);
            Assert.AreEqual(beforeCreate, await native.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                new() { Document = document }, token), "Creation through MCP must preserve an existing design.");
            var initialEvent = (await Call("kicad_events_wait", new { instanceId, timeoutSeconds = 5 }))
                .GetProperty("structuredContent");
            Assert.AreEqual("InitialStateRequired", initialEvent.GetProperty("status").GetString());
            Assert.IsFalse(initialEvent.GetProperty("trackingComplete").GetBoolean());
            var eventCursor = SchematicJson.Parser.Parse<AutomationEvent>(initialEvent.GetProperty("notification").GetRawText());
            Assert.AreEqual(instanceId, eventCursor.InstanceId);
            Assert.AreEqual(native.Epoch, eventCursor.ProcessEpoch);
            var quiet = (await Call("kicad_events_wait", new { instanceId, eventEpoch = eventCursor.EventEpoch,
                afterSequence = eventCursor.Sequence, timeoutSeconds = 1 })).GetProperty("structuredContent");
            Assert.AreEqual("Deadline", quiet.GetProperty("status").GetString());
            var invalidCursor = await Call("kicad_events_wait", new { instanceId, eventEpoch = "obsolete-stream", afterSequence = 0 }, error: true);
            Assert.AreEqual("event_stream_changed", invalidCursor.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var missingEpoch = await Call("kicad_events_wait", new { instanceId, afterSequence = 0 }, error: true);
            Assert.AreEqual("missing_event_epoch", missingEpoch.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            int cancelledRequestId = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = cancelledRequestId, method = "tools/call",
                @params = new { name = "kicad_events_wait", arguments = new { instanceId,
                    eventEpoch = eventCursor.EventEpoch, afterSequence = eventCursor.Sequence, timeoutSeconds = 60 } }
            }));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", method = "notifications/cancelled",
                @params = new { requestId = cancelledRequestId, reason = "Native event observation cancellation fixture" }
            }));
            // The SDK deliberately sends no response to a user-cancelled
            // request. Continue with a new call, ignoring any racing late reply.
            var afterCancel = (await Call("kicad_events_wait", new { instanceId, timeoutSeconds = 5 }))
                .GetProperty("structuredContent");
            Assert.AreEqual("InitialStateRequired", afterCancel.GetProperty("status").GetString());
            Assert.AreEqual(native.Epoch, (await native.HandshakeAsync(token)).Epoch);
            var originalPage = await native.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document }, token);
            var changedPage = originalPage.Clone();
            changedPage.Orientation = (PageOrientation)((int)originalPage.Orientation == 1 ? 2 : 1);
            if (changedPage.UserPageSize is { } size)
                (size.XNm, size.YNm) = (size.YNm, size.XNm);
            var waitingEvent = Call("kicad_events_wait", new { instanceId, eventEpoch = eventCursor.EventEpoch,
                afterSequence = eventCursor.Sequence, timeoutSeconds = 5 });
            await native.InvokeAsync<SetPageSettings, PageSettings>(new() { Document = document, PageSettings = changedPage }, token);
            var eventResult = (await waitingEvent).GetProperty("structuredContent");
            Assert.IsTrue(eventResult.GetProperty("status").GetString() is "Change" or "RecoveryRequired");
            var delivered = SchematicJson.Parser.Parse<AutomationEvent>(eventResult.GetProperty("notification").GetRawText());
            Assert.IsGreaterThan(eventCursor.Sequence, delivered.Sequence);
            Assert.AreEqual(changedPage, await native.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document }, token));
            await native.InvokeAsync<SetPageSettings, PageSettings>(new() { Document = document, PageSettings = originalPage }, token);
            var child = document.Clone(); child.SheetPath.Path.Add(new KIID { Value = childId });
            var journal = await native.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
            var inspected = await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = journal.DocumentEpoch, operationId
            });
            var receipt = SchematicJson.Parser.Parse<SchematicOperationReceipt>(
                inspected.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
            Assert.AreEqual(SchematicOperationReceipt.Types.State.Completed, receipt.State);
            Assert.AreEqual(operationId, receipt.OperationId);
            Assert.AreEqual(document, receipt.Document);
            Assert.AreEqual(receipt, await native.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(
                new() { Document = document, DocumentEpoch = journal.DocumentEpoch, OperationId = operationId }, token));
            var exact = await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = journal.DocumentEpoch,
                operationId, expectedRequestJson = SchematicJson.Formatter.Format(retainedOperation)
            });
            var exactReceipt = SchematicJson.Parser.Parse<SchematicOperationReceipt>(
                exact.GetProperty("structuredContent").GetProperty("receipt").GetRawText());
            Assert.IsTrue(exactReceipt.ExpectedRequestVerified);
            Assert.AreEqual(receipt.Result, exactReceipt.Result);
            string recoveryPath = Path.Combine(evidence, $"{operationId}-design-recovery.json");
            var recoveryStore = new DesignRecoveryStore(recoveryPath);
            string recoveryToken = recoveryStore.Read()!.RevisionToken;
            await Call("kicad_design_recovery_observe", new { instanceId, recoveryPath = "relative-recovery.json" }, error: true);
            await Call("kicad_design_recovery_observe", new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath }, error: true);
            await Call("kicad_design_recovery_observe", new { instanceId, recoveryPath, expectedRevisionToken = "obsolete" }, error: true);
            var recoveryObservation = (await Call("kicad_design_recovery_observe", new
                { instanceId, recoveryPath, expectedRevisionToken = recoveryToken })).GetProperty("structuredContent");
            Assert.AreEqual("CompletedNeedsReconciliation", recoveryObservation.GetProperty("disposition").GetString());
            Assert.AreEqual(recoveryToken, recoveryObservation.GetProperty("recoveryRevisionToken").GetString());
            Assert.IsFalse(recoveryObservation.GetProperty("trackingComplete").GetBoolean());
            var observedRecoveryReceipt = SchematicJson.Parser.Parse<SchematicOperationReceipt>(recoveryObservation.GetProperty("receipt").GetRawText());
            Assert.AreEqual(exactReceipt, observedRecoveryReceipt);
            var observedRecoverySnapshot = SchematicJson.Parser.Parse<SchematicHierarchyDataSnapshot>(recoveryObservation.GetProperty("snapshot").GetRawText());
            Assert.AreEqual(journal.Sequence, observedRecoverySnapshot.Revision.Sequence);
            Assert.AreEqual(observedRecoverySnapshot.Data, SchematicDataXml.Read(recoveryObservation.GetProperty("xml").GetString()!));
            Assert.AreEqual(recoveryToken, recoveryStore.Read()!.RevisionToken);
            await Call("kicad_design_recovery_refresh", new { instanceId, recoveryPath,
                expectedRevisionToken = recoveryToken }, error: true);
            Assert.AreEqual(recoveryToken, recoveryStore.Read()!.RevisionToken,
                "Refresh must not discard the unconfirmed retry identity.");
            // Independent no-pending fixture: this is observation persistence, not
            // a claim that the previous pending operation has been reconciled.
            string refreshPath = Path.Combine(evidence, $"{operationId}-native-refresh.json");
            var refreshStore = new DesignRecoveryStore(refreshPath);
            var refreshBefore = refreshStore.Save(recoveryStore.Read()!.State with { PendingMutation = null }, null);
            await Call("kicad_design_recovery_refresh", new { instanceId, recoveryPath = refreshPath,
                expectedRevisionToken = "obsolete" }, error: true);
            await Call("kicad_design_recovery_refresh", new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath = refreshPath,
                expectedRevisionToken = refreshBefore.RevisionToken }, error: true);
            var refreshResult = (await Call("kicad_design_recovery_refresh", new { instanceId, recoveryPath = refreshPath,
                expectedRevisionToken = refreshBefore.RevisionToken })).GetProperty("structuredContent");
            var refreshAfter = refreshStore.Read()!;
            Assert.AreEqual(refreshAfter.RevisionToken, refreshResult.GetProperty("recoveryRevisionToken").GetString());
            Assert.IsTrue(refreshResult.GetProperty("changed").GetBoolean());
            Assert.IsFalse(refreshResult.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual(observedRecoverySnapshot.Data, refreshAfter.State.Observed);
            Assert.AreEqual(journal.Sequence, refreshAfter.State.NativeRevision.Sequence);
            CollectionAssert.AreEqual(refreshBefore.State.DesiredFileBytes, refreshAfter.State.DesiredFileBytes);
            Assert.AreEqual(SchematicDesignXml.Write(refreshBefore.State.Baseline, refreshBefore.State.KnowledgeLibraries),
                SchematicDesignXml.Write(refreshAfter.State.Baseline, refreshAfter.State.KnowledgeLibraries));
            var refreshAgain = (await Call("kicad_design_recovery_refresh", new { instanceId, recoveryPath = refreshPath,
                expectedRevisionToken = refreshAfter.RevisionToken })).GetProperty("structuredContent");
            Assert.IsFalse(refreshAgain.GetProperty("changed").GetBoolean());
            Assert.AreEqual(refreshAfter.RevisionToken, refreshStore.Read()!.RevisionToken);
            var mismatched = retainedOperation.Clone(); mismatched.Description = "Different retained request";
            await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = journal.DocumentEpoch,
                operationId, expectedRequestJson = SchematicJson.Formatter.Format(mismatched)
            }, error: true);
            await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = journal.DocumentEpoch,
                operationId, expectedRequestJson = SchematicJson.Formatter.Format(retainedOperation)
            });
            var absent = await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = journal.DocumentEpoch,
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.AreEqual(SchematicOperationReceipt.Types.State.NotFound, SchematicJson.Parser.Parse<SchematicOperationReceipt>(
                absent.GetProperty("structuredContent").GetProperty("receipt").GetRawText()).State);
            await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), documentEpoch = "obsolete-epoch", operationId
            }, error: true);
            await Call("kicad_schematic_operation_inspect", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(child), documentEpoch = journal.DocumentEpoch, operationId
            }, error: true);
            SchematicHierarchyDataSnapshot? firstHierarchy = null;
            foreach (var target in new[] { document, child, document })
            {
                string documentJson = JsonFormatter.Default.Format(target);
                await Call("kicad_schematic_sheet_activate", new { instanceId, documentJson });
                var saveStateResult = await Call("kicad_schematic_save_state", new { instanceId, documentJson });
                var saveState = SchematicJson.Parser.Parse<SchematicSaveState>(saveStateResult
                    .GetProperty("structuredContent").GetProperty("state").GetRawText());
                Assert.AreEqual(await native.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = target }, token), saveState);
                Assert.IsFalse(saveState.ProjectSettingsChecked);
                // An offscreen loaded sheet can be queried without navigating.
                var otherTarget = target.Equals(document) ? child : document;
                await Call("kicad_schematic_save_state", new { instanceId, documentJson = JsonFormatter.Default.Format(otherTarget) });
                Assert.AreEqual(target, (await native.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                    new() { Type = (DocumentType)1 }, token)).Documents.Single());
                var metadataResult = (await Call("kicad_schematic_metadata", new { instanceId, documentJson }))
                    .GetProperty("structuredContent");
                Assert.IsFalse(metadataResult.GetProperty("trackingComplete").GetBoolean());
                var nativeMetadata = SchematicJson.Parser.Parse<SchematicMetadataSnapshot>(
                    metadataResult.GetProperty("snapshot").GetRawText());
                Assert.IsNotEmpty(nativeMetadata.Metadata.UnrepresentedState);
                Assert.AreEqual(await native.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                    new() { Document = target }, token), nativeMetadata);
                var dataResult = (await Call("kicad_schematic_data", new { instanceId, documentJson }))
                    .GetProperty("structuredContent");
                Assert.AreEqual(instanceId, dataResult.GetProperty("instanceId").GetString());
                Assert.IsFalse(dataResult.GetProperty("trackingComplete").GetBoolean());
                var data = SchematicJson.Parser.Parse<SchematicScreenDataSnapshot>(
                    dataResult.GetProperty("snapshot").GetRawText());
                Assert.AreEqual(await native.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = target }, token), data);
                Assert.AreEqual(data.Data, SchematicDataXml.Read(dataResult.GetProperty("xml").GetString()!));
                var hierarchyResult = (await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson }))
                    .GetProperty("structuredContent");
                var allSheets = SchematicJson.Parser.Parse<SchematicHierarchyDataSnapshot>(hierarchyResult.GetProperty("snapshot").GetRawText());
                if (firstHierarchy is null) firstHierarchy = allSheets.Clone();
                else Assert.AreEqual(firstHierarchy, allSheets, "Whole-hierarchy capture must be independent of the visible sheet.");
                Assert.IsFalse(allSheets.TrackingComplete);
                Assert.IsTrue(allSheets.Data.Instances.Count >= 3);
                Assert.AreEqual(allSheets.Data, SchematicDataXml.Read(hierarchyResult.GetProperty("xml").GetString()!));
                var topology = (await Call("kicad_schematic_hierarchy_validate", new { hierarchyXml = hierarchyResult.GetProperty("xml").GetString() }))
                    .GetProperty("structuredContent");
                Assert.IsTrue(topology.GetProperty("topologyValid").GetBoolean(), topology.GetRawText());
                Assert.AreEqual(allSheets.Data.Instances.Count, topology.GetProperty("instanceCount").GetInt32());
                Assert.IsFalse(topology.GetProperty("completeReconstructionProven").GetBoolean());
                Assert.IsTrue(topology.GetProperty("coverageGaps").GetArrayLength() > 0);
                Assert.AreEqual(data.Data, allSheets.Data.Instances.Single(s => s.Metadata.Document.Equals(target)));
                Assert.AreEqual(allSheets, await native.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                    new() { Document = target }, token));
                Assert.AreEqual(target, (await native.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                    new() { Type = (DocumentType)1 }, token)).Documents.Single(), "Capturing offscreen children must not navigate the editor.");
                Assert.IsTrue(allSheets.Data.Instances.GroupBy(s => s.Metadata.ScreenId.Value).Any(g => g.Count() > 1),
                    "Repeated sheet instances must retain their shared screen identity.");
                var combinedResult = await Call("kicad_schematic_observe", new { instanceId, documentJson });
                var combinedContent = combinedResult.GetProperty("structuredContent");
                Assert.IsFalse(combinedContent.GetProperty("trackingComplete").GetBoolean());
                var combined = SchematicJson.Parser.Parse<SchematicObservation>(combinedContent.GetProperty("observation").GetRawText());
                Assert.AreEqual(combined.Snapshot.Revision, combined.Preview.Revision);
                Assert.AreEqual(target, combined.Preview.Document);
                Assert.AreEqual(data.Data, combined.Snapshot.Data);
                Assert.AreEqual(combined.Snapshot.Data, SchematicDataXml.Read(combinedContent.GetProperty("xml").GetString()!));
                var combinedImage = combinedResult.GetProperty("content").EnumerateArray().Single(c => c.GetProperty("type").GetString() == "image");
                var combinedPng = Convert.FromBase64String(combinedImage.GetProperty("data").GetString()!);
                var directCombined = await native.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = target }, token);
                Assert.IsTrue(directCombined.Preview.Png.Span.SequenceEqual(combinedPng));
                Assert.AreEqual(directCombined.Snapshot, combined.Snapshot);
                var renderPage = await native.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
                    new() { Document = target }, token);
                var viewsRequest = new RenderSchematicViews { Document = target };
                foreach (var (key, divisor) in new[] { ("overview", 1L), ("detail", 2L) })
                    viewsRequest.Views.Add(new SchematicRenderView { Key = key, WidthPixels = 640, HeightPixels = 480,
                        Region = new() { Position = renderPage.PageBounds.Position.Clone(), Size = new()
                        { XNm = renderPage.PageBounds.Size.XNm / divisor, YNm = renderPage.PageBounds.Size.YNm / divisor } } });
                var viewsResult = await Call("kicad_schematic_render_views", new
                {
                    instanceId, requestJson = SchematicJson.Formatter.Format(viewsRequest)
                });
                var viewsContent = viewsResult.GetProperty("structuredContent");
                Assert.IsFalse(viewsContent.GetProperty("trackingComplete").GetBoolean());
                var returnedViews = SchematicJson.Parser.Parse<SchematicViewSet>(viewsContent.GetProperty("viewSet").GetRawText());
                var nativeViews = await native.InvokeAsync<RenderSchematicViews, SchematicViewSet>(viewsRequest, token);
                var images = viewsResult.GetProperty("content").EnumerateArray()
                    .Where(c => c.GetProperty("type").GetString() == "image").ToArray();
                Assert.AreEqual(2, images.Length);
                for (int index = 0; index < images.Length; index++)
                {
                    Assert.AreEqual("image/png", images[index].GetProperty("mimeType").GetString());
                    byte[] independentPng = Convert.FromBase64String(images[index].GetProperty("data").GetString()!);
                    Assert.IsTrue(nativeViews.Views[index].Preview.Png.Span.SequenceEqual(independentPng),
                        "MCP image order and bytes must match the keyed native view order.");
                    await File.WriteAllBytesAsync(Path.Combine(evidence,
                        instanceId + "-mcp-independent-" + target.SheetPath.Path.Last().Value + "-" + index + ".png"), independentPng, token);
                    nativeViews.Views[index].Preview.Png = Google.Protobuf.ByteString.Empty;
                }
                Assert.AreEqual(nativeViews, returnedViews);
                Assert.AreEqual(nativeViews.Snapshot.Data, SchematicDataXml.Read(viewsContent.GetProperty("xml").GetString()!));
                var hiddenRequest = viewsRequest.Clone(); hiddenRequest.Document = otherTarget.Clone();
                var hiddenResult = await Call("kicad_schematic_render_views", new
                {
                    instanceId, requestJson = SchematicJson.Formatter.Format(hiddenRequest)
                });
                var hiddenNative = await native.InvokeAsync<RenderSchematicViews, SchematicViewSet>(hiddenRequest, token);
                Assert.AreEqual(allSheets.Data.Instances.Single(s => s.Metadata.Document.Equals(otherTarget)), hiddenNative.Snapshot.Data);
                var hiddenImages = hiddenResult.GetProperty("content").EnumerateArray()
                    .Where(c => c.GetProperty("type").GetString() == "image").ToArray();
                Assert.AreEqual(hiddenRequest.Views.Count, hiddenImages.Length);
                for (int index = 0; index < hiddenImages.Length; index++)
                {
                    Assert.IsTrue(hiddenNative.Views[index].Preview.Png.Span.SequenceEqual(
                        Convert.FromBase64String(hiddenImages[index].GetProperty("data").GetString()!)));
                    hiddenNative.Views[index].Preview.Png = Google.Protobuf.ByteString.Empty;
                }
                Assert.AreEqual(hiddenNative, SchematicJson.Parser.Parse<SchematicViewSet>(hiddenResult
                    .GetProperty("structuredContent").GetProperty("viewSet").GetRawText()));
                Assert.AreEqual(directCombined, await native.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = target }, token), "MCP independent renders must leave the human canvas untouched.");
                var result = await Call("kicad_schematic_preview", new { instanceId, documentJson });
                var image = result.GetProperty("content").EnumerateArray().Single(c => c.GetProperty("type").GetString() == "image");
                Assert.AreEqual("image/png", image.GetProperty("mimeType").GetString());
                byte[] png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
                var direct = await native.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = target }, token);
                Assert.IsTrue(direct.Png.Span.SequenceEqual(png), "MCP must deliver the exact native PNG bytes.");
                var metadata = result.GetProperty("structuredContent");
                Assert.AreEqual(instanceId, metadata.GetProperty("instanceId").GetString());
                Assert.IsFalse(metadata.GetProperty("trackingComplete").GetBoolean());
                var descriptor = metadata.GetProperty("native");
                Assert.AreEqual(target, JsonParser.Default.Parse<DocumentSpecifier>(descriptor.GetProperty("document").GetRawText()));
                Assert.AreEqual(direct.Revision, JsonParser.Default.Parse<KiCad.Automation.Protocol.DocumentRevision>(descriptor.GetProperty("revision").GetRawText()));
                Assert.AreEqual(direct.Viewport, JsonParser.Default.Parse<SchematicViewport>(descriptor.GetProperty("viewport").GetRawText()));
                await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-mcp-" + target.SheetPath.Path.Last().Value + ".png"), png, token);
            }
            await Call("kicad_schematic_preview", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_render_views", new { instanceId, requestJson = "{}" }, error: true);
            await Call("kicad_schematic_render_views", new { instanceId, requestJson = "invalid json" }, error: true);
            await Call("kicad_schematic_render_views", new { instanceId = Guid.NewGuid().ToString("D"), requestJson = "{}" }, error: true);
            await Call("kicad_schematic_save_state", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_save_state", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_metadata", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_data", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_hierarchy_data", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            var visibleBeforeHiddenRead = await native.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = (DocumentType)1 }, token);
            var hiddenHierarchy = await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson = JsonFormatter.Default.Format(child) });
            var hiddenData = (Kiapi.Schematic.Types.SchematicHierarchyData)SchematicDataXml.Read(hiddenHierarchy.GetProperty("structuredContent").GetProperty("xml").GetString()!);
            Assert.AreEqual(document, hiddenData.Document);
            Assert.IsTrue(hiddenData.Instances.Any(s => s.Metadata.Document.Equals(child)));
            Assert.AreEqual(visibleBeforeHiddenRead, await native.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = (DocumentType)1 }, token));
            var unloadedHierarchyTarget = child.Clone();
            unloadedHierarchyTarget.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson = JsonFormatter.Default.Format(unloadedHierarchyTarget) }, error: true);
            await Call("kicad_schematic_observe", new { instanceId, documentJson = "{}" }, error: true);
            await Call("kicad_schematic_observe", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_observe", new { instanceId, documentJson = JsonFormatter.Default.Format(child) }, error: true);
            await Call("kicad_schematic_data", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_data", new { instanceId, documentJson = JsonFormatter.Default.Format(child) }, error: true);
            await Call("kicad_schematic_metadata", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_metadata", new { instanceId, documentJson = JsonFormatter.Default.Format(child) }, error: true);
            var metadataHeader = new ItemHeader { Document = document };
            var metadataCommit = await native.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = metadataHeader }, token);
            await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_metadata", new { instanceId, documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_data", new { instanceId, documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_observe", new { instanceId, documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await native.InvokeAsync<EndCommit, EndCommitResponse>(new()
            {
                Header = metadataHeader, Id = metadataCommit.Id, Action = (CommitAction)2
            }, token);
            await Call("kicad_schematic_metadata", new { instanceId, documentJson = JsonFormatter.Default.Format(document) });
            await Call("kicad_schematic_hierarchy_data", new { instanceId, documentJson = JsonFormatter.Default.Format(document) });
            await Call("kicad_schematic_data", new { instanceId, documentJson = JsonFormatter.Default.Format(document) });
            await Call("kicad_schematic_observe", new { instanceId, documentJson = JsonFormatter.Default.Format(document) });
            await Call("kicad_schematic_preview", new { instanceId = Guid.NewGuid().ToString("D"), documentJson = JsonFormatter.Default.Format(document) }, error: true);
            await Call("kicad_schematic_preview", new { instanceId, documentJson = JsonFormatter.Default.Format(child) }, error: true);
            await Call("kicad_schematic_preview", new { instanceId, documentJson = JsonFormatter.Default.Format(document) });
            var checkedPresentation = await Call("kicad_schematic_check_presentation", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), minimumTextHeightMm = 1, maximumTextHeightMm = 3
            });
            var formal = checkedPresentation.GetProperty("structuredContent").GetProperty("check");
            Assert.IsFalse(formal.GetProperty("report").GetProperty("coverageComplete").GetBoolean());
            Assert.IsFalse(formal.GetProperty("report").GetProperty("clear").GetBoolean());
            Assert.IsTrue(formal.GetProperty("limitations").GetArrayLength() > 0);
            var smallPolicy = await Call("kicad_schematic_check_presentation", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), minimumTextHeightMm = 0.1, maximumTextHeightMm = 0.5
            });
            Assert.IsTrue(smallPolicy.GetProperty("structuredContent").GetProperty("check").GetProperty("report")
                .GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("rule").GetString() == "text_size"
                    && f.GetProperty("severity").GetString() == "Warning"));
            await Call("kicad_schematic_check_presentation", new
            {
                instanceId, documentJson = JsonFormatter.Default.Format(document), minimumTextHeightMm = 3, maximumTextHeightMm = 1
            }, error: true);
            Assert.AreEqual(journal.Sequence, (await native.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token)).Sequence);
            await VerifyMcpConnectedMove(native, document, electrical, processId, display, evidence, instanceId, Call, token);
            var beforeDisconnect = await native.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token);
            var dirtyBeforeDisconnect = await native.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = document }, token);
            Assert.IsTrue(dirtyBeforeDisconnect.UnsavedSchematicChanges);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            string diagnosticText = await diagnostics;
            Assert.AreEqual(0, process.ExitCode, diagnosticText);
            StringAssert.Contains(diagnosticText, $"canceled request '{cancelledRequestId}' per client notification");
            StringAssert.Contains(diagnosticText, "CanceledException");
            // Shutting down the MCP host must leave the editor available.
            Assert.AreEqual(document, (await native.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token)).Document);
            await VerifyMcpReattachment(start, instanceId, document, beforeDisconnect, dirtyBeforeDisconnect, token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            Directory.Delete(state, true);
        }

        async Task<JsonElement> Call(string name, object arguments, bool error = false)
        {
            var response = await Request("tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            bool actualError = result.TryGetProperty("isError", out var failed) && failed.GetBoolean();
            if (error != actualError)
            {
                string unexpected = Path.Combine(evidence, instanceId + "-mcp-unexpected-" + nextId + ".json");
                await File.WriteAllTextAsync(unexpected, result.GetRawText(), token);
                Assert.AreEqual(error, actualError, name + "; full response retained at " + unexpected);
            }
            return result;
        }

        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                Assert.IsNotNull(line, "MCP exited before responding.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }
}
