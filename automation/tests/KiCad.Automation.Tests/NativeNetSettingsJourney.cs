using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        string Prefix(string phase) => Path.Combine(evidence, processId + "-net-settings-" + phase);
        static bool DocumentTypeMatches(JsonElement value, DocumentType expectedType)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numericType))
                return numericType == (int)expectedType;
            return value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), expectedType.ToString(), StringComparison.Ordinal);
        }
        static void AssertDocumentSpecifierIdentity(JsonElement reported, DocumentSpecifier expected, string phase)
        {
            Assert.IsTrue(DocumentTypeMatches(reported.GetProperty("type"), expected.Type),
                $"{phase}: response changed the document type.");
            var reportedProject = reported.GetProperty("project");
            Assert.AreEqual(expected.Project.Name, reportedProject.GetProperty("name").GetString(),
                $"{phase}: response changed the project name.");
            Assert.AreEqual(expected.Project.Path, reportedProject.GetProperty("path").GetString(),
                $"{phase}: response changed the project path.");
            switch (expected.IdentifierCase)
            {
                case DocumentSpecifier.IdentifierOneofCase.BoardFilename:
                    Assert.AreEqual(expected.BoardFilename, reported.GetProperty("boardFilename").GetString(),
                        $"{phase}: response changed the board filename.");
                    break;
                case DocumentSpecifier.IdentifierOneofCase.LibId:
                    var reportedLibId = reported.GetProperty("libId");
                    Assert.AreEqual(expected.LibId.LibraryNickname, reportedLibId.GetProperty("libraryNickname").GetString(),
                        $"{phase}: response changed the library nickname.");
                    Assert.AreEqual(expected.LibId.EntryName, reportedLibId.GetProperty("entryName").GetString(),
                        $"{phase}: response changed the library entry name.");
                    break;
                case DocumentSpecifier.IdentifierOneofCase.SheetPath:
                    var reportedSheetPath = reported.GetProperty("sheetPath");
                    Assert.AreEqual(expected.SheetPath.PathHumanReadable,
                        reportedSheetPath.GetProperty("pathHumanReadable").GetString(),
                        $"{phase}: response changed the sheet path display.");
                    var expectedPath = expected.SheetPath.Path.Select(id => id.Value).ToArray();
                    var actualPath = reportedSheetPath.GetProperty("path").EnumerateArray()
                        .Select(id => id.GetProperty("value").GetString()).ToArray();
                    CollectionAssert.AreEqual(expectedPath, actualPath,
                        $"{phase}: response changed the sheet path identity.");
                    break;
            }
        }
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        async Task<JsonElement> ReadProjectNetClasses(DocumentLifecycleState expected, string phase)
        {
            string stateDirectory = Directory.CreateTempSubdirectory("kicad-project-netclasses-mcp-").FullName;
            try
            {
                await using var mcp = await StdioMcpFixture.StartAsync(
                    stateDirectory, Path.Combine(evidence, processId + "-project-netclasses-mcp-" + phase + ".stderr.log"), token);
                var session = await client.HandshakeAsync(token);
                RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
                { endpoint = client.Endpoint, expectedInstanceId = session.InstanceId }));
                return await mcp.Tool("kicad_project_net_classes", new
                {
                    instanceId = session.InstanceId,
                    documentJson = SchematicJson.Formatter.Format(document),
                    expectedStateJson = SchematicJson.Formatter.Format(expected)
                });
            }
            finally
            {
                if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
            }
        }
        async Task<JsonElement> ReadProjectTextVariables(DocumentLifecycleState expected, string phase)
        {
            string stateDirectory = Directory.CreateTempSubdirectory("kicad-project-text-variables-mcp-").FullName;
            try
            {
                await using var mcp = await StdioMcpFixture.StartAsync(
                    stateDirectory, Path.Combine(evidence, processId + "-project-text-variables-mcp-" + phase + ".stderr.log"), token);
                var session = await client.HandshakeAsync(token);
                RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
                { endpoint = client.Endpoint, expectedInstanceId = session.InstanceId }));
                return await mcp.Tool("kicad_project_text_variables", new
                {
                    instanceId = session.InstanceId,
                    project = new { name = document.Project.Name, path = document.Project.Path },
                    document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(document)),
                    expectedStateJson = SchematicJson.Formatter.Format(expected)
                });
            }
            finally
            {
                if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
            }
        }
        async Task<JsonElement> SetProjectTextVariables(DocumentLifecycleState expected,
            IReadOnlyDictionary<string, string> variables, string mergeMode, string operationId, string phase,
            string? expectedVariablesSha256 = null, string? projectName = null, string? projectPath = null)
        {
            string stateDirectory = Directory.CreateTempSubdirectory("kicad-project-text-variables-set-mcp-").FullName;
            try
            {
                await using var mcp = await StdioMcpFixture.StartAsync(
                    stateDirectory, Path.Combine(evidence, processId + "-project-text-variables-set-mcp-" + phase + ".stderr.log"), token);
                var session = await client.HandshakeAsync(token);
                RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
                { endpoint = client.Endpoint, expectedInstanceId = session.InstanceId }));
                return await mcp.Tool("kicad_project_text_variables_set", new
                {
                    instanceId = session.InstanceId,
                    project = new { name = projectName ?? document.Project.Name, path = projectPath ?? document.Project.Path },
                    document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(document)),
                    expectedStateJson = SchematicJson.Formatter.Format(expected),
                    variables, mergeMode, operationId, expectedVariablesSha256
                });
            }
            finally
            {
                if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
            }
        }
        static void AssertProjectNetClasses(JsonElement reply, DocumentLifecycleState expected, string phase)
        {
            RequireToolSuccess(reply);
            var structured = reply.GetProperty("structuredContent");
            Assert.IsTrue(structured.GetProperty("supported").GetBoolean(), $"{phase}: native netclass support is required.");
            Assert.IsTrue(structured.GetProperty("nativeQuery").GetBoolean(), $"{phase}: the result must come from KiCad.");
            Assert.IsFalse(structured.GetProperty("boardMutation").GetBoolean(), $"{phase}: reading netclasses must not mutate the board.");
            Assert.AreEqual(SchematicJson.Formatter.Format(expected), structured.GetProperty("beforeState").GetString(),
                $"{phase}: netclass read must use the supplied lifecycle checkpoint.");
            Assert.AreEqual(structured.GetProperty("beforeState").GetString(), structured.GetProperty("afterState").GetString(),
                $"{phase}: a read must leave the lifecycle checkpoint unchanged.");
            var classes = structured.GetProperty("response").GetProperty("netClasses").EnumerateArray().ToArray();
            Assert.IsNotEmpty(classes, $"{phase}: KiCad must return at least the Default netclass.");
            var names = classes.Select(netClass => netClass.GetProperty("name").GetString()).ToArray();
            Assert.AreEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count(),
                $"{phase}: native netclass names must be unique.");
            CollectionAssert.Contains(names!, "Default", $"{phase}: KiCad must return its Default netclass.");
        }
        async Task AssertProjectTextVariables(JsonElement reply, DocumentLifecycleState expected, string phase)
        {
            RequireToolSuccess(reply);
            var structured = reply.GetProperty("structuredContent");
            Assert.IsTrue(structured.GetProperty("supported").GetBoolean(), $"{phase}: native text-variable support is required.");
            Assert.IsTrue(structured.GetProperty("nativeQuery").GetBoolean(), $"{phase}: the result must come from KiCad.");
            Assert.IsFalse(structured.GetProperty("mutation").GetBoolean(), $"{phase}: reading text variables must not mutate the project.");
            var reportedDocument = structured.GetProperty("document");
            Assert.IsTrue(DocumentTypeMatches(reportedDocument.GetProperty("type"), expected.Document.Type),
                $"{phase}: text-variable read must report the exact lifecycle document type.");
            Assert.AreEqual(expected.Document.Project.Name, reportedDocument.GetProperty("project").GetProperty("name").GetString(),
                $"{phase}: text-variable read must report the exact project owner name.");
            Assert.AreEqual(expected.Document.Project.Path, reportedDocument.GetProperty("project").GetProperty("path").GetString(),
                $"{phase}: text-variable read must report the exact project owner path.");
            var reportedProject = structured.GetProperty("project");
            Assert.IsTrue(DocumentTypeMatches(reportedProject.GetProperty("type"), DocumentType.DoctypeProject),
                $"{phase}: text-variable read must identify the project owner.");
            Assert.AreEqual(expected.Document.Project.Name, reportedProject.GetProperty("project").GetProperty("name").GetString(),
                $"{phase}: text-variable read must report the exact project owner.");
            Assert.AreEqual(expected.Document.Project.Path, reportedProject.GetProperty("project").GetProperty("path").GetString(),
                $"{phase}: text-variable read must report the exact project owner path.");
            Assert.AreEqual(SchematicJson.Formatter.Format(expected), structured.GetProperty("beforeState").GetString(),
                $"{phase}: text-variable read must use the supplied lifecycle checkpoint.");
            Assert.AreEqual(structured.GetProperty("beforeState").GetString(), structured.GetProperty("afterState").GetString(),
                $"{phase}: a text-variable read must leave the lifecycle checkpoint unchanged.");
            var project = new DocumentSpecifier { Type = DocumentType.DoctypeProject, Project = document.Project.Clone() };
            var native = await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
                new() { Document = project }, token);
            var variables = structured.GetProperty("variables");
            Assert.AreEqual(native.Variables.Count, variables.EnumerateObject().Count(),
                $"{phase}: MCP must return every native project text variable exactly once.");
            foreach (var pair in native.Variables)
                Assert.AreEqual(pair.Value, variables.GetProperty(pair.Key).GetString(),
                    $"{phase}: MCP changed native map value for {pair.Key}.");
        }
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicNetSettings settings)
        {
            var value = settings.Clone(); value.LabelAssignments.Clear();
            var batch = new ApplySchematicItemBatch { Document = document, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"),
                Description = "Project net settings XML round trip" };
            batch.Operations.Add(new SchematicItemOperation { SetNetSettings = value });
            return batch;
        }
        async Task Same(SchematicScreenData expected, SchematicScreenData actual, string phase)
        {
            if (!expected.Equals(actual))
            {
                await File.WriteAllTextAsync(Prefix(phase + "-expected.xml"), SchematicDataXml.Write(expected), token);
                await File.WriteAllTextAsync(Prefix(phase + "-actual.xml"), SchematicDataXml.Write(actual), token);
                Assert.Fail(NativeSnapshotDifference.Describe(expected, actual));
            }
            Assert.AreEqual(expected, SchematicDataXml.Read(SchematicDataXml.Write(actual)));
        }
        async Task Capture(string phase)
        {
            await NativeKeyboard.CaptureAsync(display, Prefix(phase + ".png"), token);
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(observation.Preview.Revision, observation.Snapshot.Revision);
            await File.WriteAllTextAsync(Prefix(phase + ".xml"), SchematicDataXml.Write(observation.Snapshot.Data), token);
        }
        async Task Reopen(SchematicScreenData expected, string phase, bool verifyReconnect = true)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This is the Linux native save fixture.");
            var beforeSave = await ObserveLifecycleState(client, document, token);
            var persisted = beforeSave.NativeFiles.ToDictionary(path => path, File.ReadAllBytes);
            string projectFile = Path.Combine(document.Project.Path, document.Project.Name + ".kicad_pro");
            foreach (string protectedPath in new[] { projectFile, beforeSave.NativeFiles.First(path => path.EndsWith(".kicad_sch", StringComparison.Ordinal)) })
            {
                var mode = File.GetUnixFileMode(protectedPath);
                try
                {
                    File.SetUnixFileMode(protectedPath, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    var failure = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                        client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, limit.Token));
                    Assert.AreEqual(3, failure.Status);
                    Assert.AreEqual(beforeSave, await ObserveLifecycleState(client, document, token),
                        "Rejected schematic/project persistence must preserve state and dirty flags.");
                    foreach (var (path, bytes) in persisted)
                        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
                }
                finally { File.SetUnixFileMode(protectedPath, mode); }
            }
            await SaveCheckedThroughMcp(client, document, evidence, token, verifyReconnect);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
            await Same(expected, (await Read()).Data, phase);
        }
        async Task UndoRedo(SchematicScreenData original, SchematicScreenData changed)
        {
            foreach (var (key, expected) in new[] { ("z", original), ("y", changed) })
            {
                var before = await Read();
                await FocusedSchematicShortcut(client, document, processId, display, key, token);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                int delay = 25;
                SchematicScreenDataSnapshot after;
                do
                {
                    after = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                        new() { Document = document }, limit.Token);
                    if (after.Revision.Equals(before.Revision))
                    { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
                } while (after.Revision.Equals(before.Revision));
                await Same(expected, after.Data, "history-" + key);
            }
        }

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var original = await Read();
        Assert.IsNotNull(original.Data.Metadata.NetSettings);
        var originalLifecycle = await ObserveLifecycleState(client, document, token);
        AssertProjectNetClasses(await ReadProjectNetClasses(originalLifecycle, "initial"), originalLifecycle, "initial");
        await AssertProjectTextVariables(await ReadProjectTextVariables(originalLifecycle, "initial"), originalLifecycle, "initial");
        var projectDocument = new DocumentSpecifier { Type = DocumentType.DoctypeProject, Project = document.Project.Clone() };
        var originalTextVariables = (await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
            new() { Document = projectDocument }, token)).Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var changedTextVariables = new Dictionary<string, string>(originalTextVariables, StringComparer.Ordinal)
        { ["AUTOMATION_TEXT_VARIABLE"] = "checked-native-value" };
        string textVariablesOperation = Guid.NewGuid().ToString("D");
        var textVariablesSet = await SetProjectTextVariables(originalLifecycle, changedTextVariables, "MMM_REPLACE",
            textVariablesOperation, "set");
        RequireToolSuccess(textVariablesSet);
        var textVariablesSetData = textVariablesSet.GetProperty("structuredContent");
        Assert.IsTrue(textVariablesSetData.GetProperty("committed").GetBoolean());
        Assert.IsFalse(textVariablesSetData.GetProperty("replayed").GetBoolean());
        Assert.IsFalse(textVariablesSetData.GetProperty("revisionTracked").GetBoolean());
        Assert.IsFalse(textVariablesSetData.GetProperty("completeLifecycle").GetBoolean());
        AssertDocumentSpecifierIdentity(textVariablesSetData.GetProperty("project"), projectDocument, "set project");
        AssertDocumentSpecifierIdentity(textVariablesSetData.GetProperty("document"), document, "set document");
        Assert.AreEqual(changedTextVariables.Count, textVariablesSetData.GetProperty("afterVariables").EnumerateObject().Count());
        foreach (var pair in changedTextVariables)
            Assert.AreEqual(pair.Value, textVariablesSetData.GetProperty("afterVariables").GetProperty(pair.Key).GetString());
        var nativeChangedTextVariables = (await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
            new() { Document = projectDocument }, token)).Variables;
        Assert.AreEqual(changedTextVariables.Count, nativeChangedTextVariables.Count);
        Assert.AreEqual(changedTextVariables["AUTOMATION_TEXT_VARIABLE"], nativeChangedTextVariables["AUTOMATION_TEXT_VARIABLE"]);
        var textVariablesLifecycle = await ObserveLifecycleState(client, document, token);
        var publicTextVariables = await ReadProjectTextVariables(textVariablesLifecycle, "after-text-variable-set");
        await AssertProjectTextVariables(publicTextVariables, textVariablesLifecycle, "after-text-variable-set");
        Assert.AreEqual("checked-native-value",
            publicTextVariables.GetProperty("structuredContent").GetProperty("variables")
                .GetProperty("AUTOMATION_TEXT_VARIABLE").GetString());

        var foreignProject = await SetProjectTextVariables(originalLifecycle, changedTextVariables, "MMM_REPLACE",
            Guid.NewGuid().ToString("D"), "foreign-project", projectName: document.Project.Name + "-foreign");
        Assert.IsTrue(foreignProject.GetProperty("isError").GetBoolean(), foreignProject.GetRawText());
        Assert.AreEqual("invalid_project_text_variables_target",
            foreignProject.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
            foreignProject.GetRawText());

        // The project owner admits the complete map by digest. A new operation
        // carrying the old digest must be refused without changing the native
        // project, even while the lifecycle checkpoint is still unchanged.
        var staleTextVariables = await SetProjectTextVariables(originalLifecycle, changedTextVariables, "MMM_REPLACE",
            Guid.NewGuid().ToString("D"), "stale-map", textVariablesSetData.GetProperty("beforeVariablesSha256").GetString());
        Assert.IsTrue(staleTextVariables.GetProperty("isError").GetBoolean(), staleTextVariables.GetRawText());
        Assert.AreEqual("project_text_variables_stale",
            staleTextVariables.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
            staleTextVariables.GetRawText());
        var unchangedAfterStale = await ReadProjectTextVariables(textVariablesLifecycle, "stale-map-preserved");
        await AssertProjectTextVariables(unchangedAfterStale, textVariablesLifecycle, "stale-map-preserved");
        Assert.AreEqual("checked-native-value",
            unchangedAfterStale.GetProperty("structuredContent").GetProperty("variables")
                .GetProperty("AUTOMATION_TEXT_VARIABLE").GetString());

        var textVariablesReplay = await SetProjectTextVariables(originalLifecycle, changedTextVariables, "MMM_REPLACE",
            textVariablesOperation, "replay", textVariablesSetData.GetProperty("beforeVariablesSha256").GetString());
        RequireToolSuccess(textVariablesReplay);
        Assert.IsTrue(textVariablesReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean());
        Assert.IsTrue(textVariablesReplay.GetProperty("structuredContent").GetProperty("committed").GetBoolean());

        // Save/reload is performed while the changed map is still installed;
        // project variables are owned by the project file, so the public read
        // after reload is the persistence evidence for this mutation.
        var expectedWithTextVariables = original.Data.Clone();
        expectedWithTextVariables.Metadata.TextVariables.Clear();
        foreach (var pair in changedTextVariables)
            expectedWithTextVariables.Metadata.TextVariables[pair.Key] = pair.Value;
        await Reopen(expectedWithTextVariables, "text-variables-reopened", verifyReconnect: false);
        var textVariablesReloadedLifecycle = await ObserveLifecycleState(client, document, token);
        var reloadedTextVariables = await ReadProjectTextVariables(textVariablesReloadedLifecycle, "text-variables-reloaded");
        await AssertProjectTextVariables(reloadedTextVariables, textVariablesReloadedLifecycle, "text-variables-reloaded");
        Assert.AreEqual("checked-native-value",
            reloadedTextVariables.GetProperty("structuredContent").GetProperty("variables")
                .GetProperty("AUTOMATION_TEXT_VARIABLE").GetString());

        await SetProjectTextVariables(textVariablesReloadedLifecycle, originalTextVariables, "MMM_REPLACE",
            Guid.NewGuid().ToString("D"), "restore");
        var restoredTextVariablesLifecycle = await ObserveLifecycleState(client, document, token);
        var restoredTextVariables = await ReadProjectTextVariables(restoredTextVariablesLifecycle, "restored");
        await AssertProjectTextVariables(restoredTextVariables, restoredTextVariablesLifecycle, "restored");
        var desired = original.Data.Clone(); var settings = desired.Metadata.NetSettings;
        settings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 50800 };
        settings.DefaultClass.Board.TrackWidth = new() { ValueNm = 450000 };
        var power = settings.DefaultClass.Clone(); power.Name = "AutomationPower"; power.Priority = 2;
        power.Board.Clearance = null; power.Board.TuningProfile = "power";
        power.Schematic.Color = new() { R = 1, A = 1 };
        settings.Classes.Add(power);
        settings.Patterns.Add(new SchematicNetClassPattern { Pattern = "POWER*", NetClass = power.Name });
        settings.NetColors.Add("/POWER", new() { R = 1, A = 1 });
        settings.ChainNetclasses.Add("unassigned-power-chain", power.Name);
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(original, desired.Metadata.NetSettings);
        foreach (int kind in Enumerable.Range(0, 6))
        {
            var invalid = batch.Clone(); invalid.OperationId = Guid.NewGuid().ToString("D");
            var value = invalid.Operations[0].SetNetSettings;
            switch (kind)
            {
                case 0: value.DefaultClass = null; break;
                case 1: value.Classes.Add(value.Classes[0].Clone()); break;
                case 2: value.DefaultClass.Schematic.LineStyle = (StrokeLineStyle)99; break;
                case 3: value.NetColors["/POWER"].R = double.NaN; break;
                case 4: value.LabelAssignments.Add("foreign-label", new() { Names = { power.Name } }); break;
                case 5:
                    invalid.Operations[0].SetNetSettings = SchematicNetSettings.Parser.ParseFrom(
                        value.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 1 }).ToArray());
                    break;
            }
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
            Assert.AreEqual(original, await Read(), "Rejected settings must preserve the native revision and all owners.");
        }
        var failed = batch.Clone(); failed.OperationId = Guid.NewGuid().ToString("D");
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(original, await Read(), "A failed later operation must roll back project settings.");
        var result = await Apply(batch); Assert.IsTrue(result.NetSettingsChanged);
        var applied = await Read(); Assert.AreEqual(original.Revision.Sequence + 1, applied.Revision.Sequence);
        await Same(desired, applied.Data, "applied");
        var staleProjectNetClasses = await ReadProjectNetClasses(originalLifecycle, "stale");
        Assert.IsTrue(staleProjectNetClasses.GetProperty("isError").GetBoolean(),
            "A project netclass read with a pre-mutation lifecycle state must be refused.");
        Assert.AreEqual("project_settings_state_changed",
            staleProjectNetClasses.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
            staleProjectNetClasses.GetRawText());
        var staleProjectTextVariables = await ReadProjectTextVariables(originalLifecycle, "stale");
        Assert.IsTrue(staleProjectTextVariables.GetProperty("isError").GetBoolean(),
            "A project text-variable read with a pre-mutation lifecycle state must be refused.");
        Assert.AreEqual("project_settings_state_changed",
            staleProjectTextVariables.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
            staleProjectTextVariables.GetRawText());
        var appliedLifecycle = await ObserveLifecycleState(client, document, token);
        AssertProjectNetClasses(await ReadProjectNetClasses(appliedLifecycle, "after-settings-change"), appliedLifecycle, "after-settings-change");
        await AssertProjectTextVariables(await ReadProjectTextVariables(appliedLifecycle, "after-settings-change"),
            appliedLifecycle, "after-settings-change");
        Assert.AreEqual(result, await Apply(batch));
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(applied, await Read());
        Assert.IsFalse((await Apply(Batch(applied, desired.Metadata.NetSettings))).NetSettingsChanged);
        Assert.AreEqual(applied, await Read());
        await Capture("api-applied"); await UndoRedo(original.Data, desired);
        await Reopen(desired, "reopened");

        var twiceExpected = desired.Clone();
        twiceExpected.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 76200 };
        var twice = Batch(await Read(), original.Data.Metadata.NetSettings);
        twice.Operations.Add(new SchematicItemOperation { SetNetSettings = twiceExpected.Metadata.NetSettings.Clone() });
        twice.Operations[^1].SetNetSettings.LabelAssignments.Clear();
        await Apply(twice); await Same(twiceExpected, (await Read()).Data, "two-operations");
        await UndoRedo(desired, twiceExpected);
        await Apply(Batch(await Read(), original.Data.Metadata.NetSettings));
        await Reopen(original.Data, "restored", verifyReconnect: false);

        foreach (bool accept in new[] { false, true })
        {
            var before = await Read(); var expected = before.Data.Clone();
            expected.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = 50800 };
            await NativeSetupUi.Open(client, document, display, processId, token);
            await NativeSetupUi.SelectPage(display, processId, 223, token);
            await NativeKeyboard.CaptureAsync(display, Prefix($"manual-before-{accept}.png"), token);
            // The schematic panel shows name, wire width, bus width, color and
            // line style. This targets the Default row's wire-width cell.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 580, clickFromTop: 65);
            NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true, false);
            foreach (char character in "2 mil")
                NativeKeyboard.SchematicShortcut(display, processId, character.ToString(), "Schematic Setup", false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "Tab", "Schematic Setup", false, false);
            await NativeKeyboard.CaptureAsync(display, Prefix($"manual-edited-{accept}.png"), token);
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            var observed = await Read();
            Assert.AreEqual(before.Revision.Sequence + (accept ? 1UL : 0UL), observed.Revision.Sequence);
            await Same(accept ? expected : before.Data, observed.Data, "manual-" + accept);
            if (!accept) continue;
            await UndoRedo(before.Data, expected); await Reopen(expected, "manual-reopened", verifyReconnect: false);
        }
        await Apply(Batch(await Read(), original.Data.Metadata.NetSettings));
        await Reopen(original.Data, "final-restored");

        // Exercise the public task-oriented wrapper after the legacy checked-batch
        // coverage has restored its fixture.  The wrapper deliberately receives the
        // complete checked schematic observation, then must retain the same native
        // receipt for an identical replay.  Keep this in one attached STDIO session
        // so the exact public request/response boundary is the evidence under test.
        string typedStateDirectory = Directory.CreateTempSubdirectory("kicad-net-settings-typed-mcp-").FullName;
        try
        {
            await using var typedMcp = await StdioMcpFixture.StartAsync(
                typedStateDirectory,
                Prefix("typed-mcp.stderr.log"), token);
            var typedSession = await client.HandshakeAsync(token);
            string typedInstanceId = typedSession.InstanceId;
            RequireToolSuccess(await typedMcp.Tool("kicad_instance_attach", new
            {
                endpoint = client.Endpoint,
                expectedInstanceId = typedInstanceId
            }));

            string typedDocumentJson = SchematicJson.Formatter.Format(document);
            async Task<CheckedSchematicState> ObserveTypedState()
            {
                var reply = await typedMcp.Tool("kicad_schematic_checked_state", new
                { instanceId = typedInstanceId, documentJson = typedDocumentJson });
                RequireToolSuccess(reply);
                return SchematicJson.Parser.Parse<CheckedSchematicState>(
                    reply.GetProperty("structuredContent").GetRawText());
            }
            static CheckedSchematicBatchReceipt TypedReceipt(JsonElement reply) =>
                SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(
                    reply.GetProperty("structuredContent").GetProperty("receipt").GetRawText());

            var typedDesired = original.Data.Clone();
            long typedWireWidth = typedDesired.Metadata.NetSettings.DefaultClass.Schematic.WireWidth.ValueNm == 101600
                ? 127000 : 101600;
            long typedTrackWidth = typedDesired.Metadata.NetSettings.DefaultClass.Board.TrackWidth.ValueNm == 500000
                ? 450000 : 500000;
            typedDesired.Metadata.NetSettings.DefaultClass.Schematic.WireWidth = new() { ValueNm = typedWireWidth };
            typedDesired.Metadata.NetSettings.DefaultClass.Board.TrackWidth = new() { ValueNm = typedTrackWidth };
            typedDesired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(typedDesired));
            var typedBefore = await ObserveTypedState();
            string typedSettingsJson = SchematicJson.Formatter.Format(typedDesired.Metadata.NetSettings);
            string typedOperationId = Guid.NewGuid().ToString("D");
            var typedArguments = new
            {
                instanceId = typedInstanceId,
                documentJson = typedDocumentJson,
                expectedStateJson = SchematicJson.Formatter.Format(typedBefore),
                netSettingsJson = typedSettingsJson,
                operationId = typedOperationId
            };

            var typedCommit = await typedMcp.Tool("kicad_schematic_set_net_settings", typedArguments);
            RequireToolSuccess(typedCommit);
            var typedCommitData = typedCommit.GetProperty("structuredContent");
            Assert.IsTrue(typedCommitData.GetProperty("mutationSubmitted").GetBoolean(), typedCommit.GetRawText());
            Assert.IsFalse(typedCommitData.GetProperty("replayed").GetBoolean(), typedCommit.GetRawText());
            var typedReceipt = TypedReceipt(typedCommit);
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, typedReceipt.Status, typedCommit.GetRawText());
            Assert.AreEqual(typedBefore.State, typedReceipt.ObservedBefore, typedCommit.GetRawText());
            await Same(typedDesired, (await Read()).Data, "typed-mcp-applied");
            var typedAfter = await ObserveTypedState();
            Assert.AreEqual(typedReceipt.ObservedAfter, typedAfter.State, typedCommit.GetRawText());

            // Reusing every argument, including the operation ID and old checked
            // state, must return the retained receipt without another native edit.
            var typedReplay = await typedMcp.Tool("kicad_schematic_set_net_settings", typedArguments);
            RequireToolSuccess(typedReplay);
            var typedReplayData = typedReplay.GetProperty("structuredContent");
            Assert.IsTrue(typedReplayData.GetProperty("replayed").GetBoolean(), typedReplay.GetRawText());
            Assert.IsFalse(typedReplayData.GetProperty("mutationSubmitted").GetBoolean(), typedReplay.GetRawText());
            Assert.AreEqual(typedReceipt, TypedReceipt(typedReplay), typedReplay.GetRawText());
            Assert.AreEqual(typedAfter, await ObserveTypedState(), typedReplay.GetRawText());

            // This journey already owns the checked save/reload path; use it to
            // prove the typed wrapper's native edit is persisted before testing a
            // stale checkpoint against a later legacy mutation.
            await Reopen(typedDesired, "typed-mcp-reopened", verifyReconnect: false);
            var typedSaved = await ObserveTypedState();
            var typedRestore = Batch(await Read(), original.Data.Metadata.NetSettings);
            var typedRestoreResult = await Apply(typedRestore);
            Assert.IsTrue(typedRestoreResult.NetSettingsChanged, "The legacy restore must advance the native settings after the typed save/reload.");
            await Same(original.Data, (await Read()).Data, "typed-mcp-legacy-restore");

            var typedStale = await typedMcp.Tool("kicad_schematic_set_net_settings", new
            {
                instanceId = typedInstanceId,
                documentJson = typedDocumentJson,
                expectedStateJson = SchematicJson.Formatter.Format(typedSaved),
                netSettingsJson = typedSettingsJson,
                operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(typedStale.GetProperty("isError").GetBoolean(), typedStale.GetRawText());
            Assert.AreEqual("stale_document_state",
                typedStale.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                typedStale.GetRawText());
            Assert.IsFalse(typedStale.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
                typedStale.GetRawText());
            await Same(original.Data, (await Read()).Data, "typed-mcp-stale-preserved");
        }
        finally
        {
            if (Directory.Exists(typedStateDirectory)) Directory.Delete(typedStateDirectory, recursive: true);
        }
        await Reopen(original.Data, "typed-mcp-final-restored", verifyReconnect: false);

        // Qualify the public project-level wrapper itself.  This is deliberately
        // separate from the complete schematic-settings tool above: callers send
        // only NetClassesResponse, while the wrapper must merge or replace those
        // classes inside the checked native net-settings transaction and retain
        // every other declared project setting.
        string projectNetClassesStateDirectory = Directory.CreateTempSubdirectory(
            "kicad-project-netclasses-public-mcp-").FullName;
        try
        {
            await using var projectNetClassesMcp = await StdioMcpFixture.StartAsync(
                projectNetClassesStateDirectory,
                Prefix("project-netclasses-public-mcp.stderr.log"), token);
            var projectNetClassesSession = await client.HandshakeAsync(token);
            string projectNetClassesInstanceId = projectNetClassesSession.InstanceId;
            RequireToolSuccess(await projectNetClassesMcp.Tool("kicad_instance_attach", new
            {
                endpoint = client.Endpoint,
                expectedInstanceId = projectNetClassesInstanceId
            }));
            var publicTools = await projectNetClassesMcp.ListTools(null);
            var publicToolNames = publicTools.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            CollectionAssert.Contains(publicToolNames.ToArray(), "kicad_project_net_classes_set");

            // Keep the public contract honest for board-only callers: project
            // netclass writes still require the explicit schematic root, even
            // though project-level reads and text variables are document-bound
            // and may use either supported lifecycle document type.
            var pcbOnlyDocument = document.Clone();
            pcbOnlyDocument.Type = DocumentType.DoctypePcb;
            pcbOnlyDocument.BoardFilename = document.Project.Name + ".kicad_pcb";
            var pcbOnlyRefusal = await projectNetClassesMcp.Tool("kicad_project_net_classes_set", new
            {
                instanceId = projectNetClassesInstanceId,
                documentJson = SchematicJson.Formatter.Format(pcbOnlyDocument),
                expectedStateJson = "{}",
                netClassesJson = SchematicJson.Formatter.Format(new NetClassesResponse()),
                mergeMode = "MMM_REPLACE",
                operationId = Guid.NewGuid().ToString("D"),
                expectedNetClassesSha256 = new string('0', 64)
            });
            Assert.IsTrue(pcbOnlyRefusal.GetProperty("isError").GetBoolean(), pcbOnlyRefusal.GetRawText());
            Assert.AreEqual("project_net_classes_schematic_required",
                pcbOnlyRefusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(),
                pcbOnlyRefusal.GetRawText());

            string publicDocumentJson = SchematicJson.Formatter.Format(document);
            async Task<CheckedSchematicState> CapturePublicState()
            {
                var reply = await projectNetClassesMcp.Tool("kicad_schematic_checked_state", new
                {
                    instanceId = projectNetClassesInstanceId,
                    documentJson = publicDocumentJson
                });
                RequireToolSuccess(reply);
                return SchematicJson.Parser.Parse<CheckedSchematicState>(
                    reply.GetProperty("structuredContent").GetRawText());
            }
            async Task<JsonElement> ReadPublicNetClasses(CheckedSchematicState expected, string phase)
            {
                var reply = await projectNetClassesMcp.Tool("kicad_project_net_classes", new
                {
                    instanceId = projectNetClassesInstanceId,
                    documentJson = publicDocumentJson,
                    expectedStateJson = SchematicJson.Formatter.Format(expected.State)
                });
                RequireToolSuccess(reply);
                var data = reply.GetProperty("structuredContent");
                Assert.IsTrue(data.GetProperty("supported").GetBoolean(), phase);
                Assert.IsTrue(data.GetProperty("nativeQuery").GetBoolean(), phase);
                Assert.IsFalse(data.GetProperty("boardMutation").GetBoolean(), phase);
                Assert.AreEqual(SchematicJson.Formatter.Format(expected.State),
                    data.GetProperty("beforeState").GetString(), phase);
                Assert.AreEqual(data.GetProperty("beforeState").GetString(),
                    data.GetProperty("afterState").GetString(), phase);
                return reply;
            }
            static NetClassesResponse PublicNetClasses(JsonElement reply) =>
                SchematicJson.Parser.Parse<NetClassesResponse>(reply.GetProperty("structuredContent")
                    .GetProperty("response").GetRawText());
            static string PublicNetClassesDigest(JsonElement reply) =>
                reply.GetProperty("structuredContent").GetProperty("netClassesSha256").GetString()!;
            SchematicNetSettings PublicSettings(CheckedSchematicState state) =>
                state.Electrical.Hierarchy.Data.Instances
                    .Single(screen => screen.Metadata.Document.Equals(document)).Metadata.NetSettings;
            static string[] PublicNetClassNames(CheckedSchematicState state) =>
                state.Electrical.Hierarchy.Data.Instances
                    .SelectMany(screen => screen.Metadata.NetSettings is null
                        ? Array.Empty<string>()
                        : screen.Metadata.NetSettings.Classes.Select(value => value.Name)
                            .Append(screen.Metadata.NetSettings.DefaultClass.Name))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            static void AssertUnrelatedSettings(SchematicNetSettings expected,
                SchematicNetSettings actual, string phase)
            {
                Assert.AreEqual(expected.Patterns, actual.Patterns,
                    phase + ": netclass mutation changed unrelated pattern assignments.");
                Assert.AreEqual(expected.NetColors, actual.NetColors,
                    phase + ": netclass mutation changed unrelated net colors.");
                Assert.AreEqual(expected.ChainNetclasses, actual.ChainNetclasses,
                    phase + ": netclass mutation changed unrelated chain netclasses.");
                Assert.AreEqual(expected.LabelAssignments, actual.LabelAssignments,
                    phase + ": netclass mutation changed observed label assignments.");
            }

            // Seed one unrelated class and independent project settings through the
            // existing checked native seam.  The public wrapper must retain these
            // settings through both merge and replace, while replace removes only
            // the old class by name.
            var seed = await Read();
            var seededSettings = seed.Data.Metadata.NetSettings.Clone();
            seededSettings.LabelAssignments.Clear();
            var existing = seededSettings.DefaultClass.Clone();
            existing.Name = "AutomationExisting";
            existing.Priority = 2;
            seededSettings.Classes.Add(existing);
            await Apply(Batch(seed, seededSettings));
            var seededSnapshot = await Read();
            var seededChecked = await CapturePublicState();
            Assert.AreEqual(seededSnapshot.Revision.Sequence, seededChecked.State.Revision.Sequence,
                "The public checked state must pair with the seeded native revision.");
            var seededQuery = await ReadPublicNetClasses(seededChecked, "public-seeded-read");
            string seededDigest = PublicNetClassesDigest(seededQuery);
            CollectionAssert.AreEquivalent(new[] { "AutomationExisting", "Default" },
                PublicNetClasses(seededQuery).NetClasses.Select(value => value.Name).ToArray());

            var mergeDefault = seededSettings.DefaultClass.Clone();
            // KiCad persists schematic widths in integer mils.  Keep the public
            // netclass fixture on values that survive the native JSON
            // save/load boundary exactly; arbitrary nanometre distances are
            // correctly rejected by the checked native adapter.
            mergeDefault.Schematic.WireWidth = new() { ValueNm = 127000 };
            var mergeClass = seededSettings.DefaultClass.Clone();
            mergeClass.Name = "AutomationMerge";
            mergeClass.Priority = 4;
            mergeClass.Schematic.WireWidth = new() { ValueNm = 254000 };
            var mergeRequest = new NetClassesResponse();
            mergeRequest.NetClasses.Add(mergeDefault);
            mergeRequest.NetClasses.Add(mergeClass);
            string mergeOperation = Guid.NewGuid().ToString("D");
            object MergeArguments(string expectedState, string operation, string digest) => new
            {
                instanceId = projectNetClassesInstanceId,
                documentJson = publicDocumentJson,
                expectedStateJson = expectedState,
                netClassesJson = SchematicJson.Formatter.Format(mergeRequest),
                mergeMode = "MMM_MERGE",
                operationId = operation,
                expectedNetClassesSha256 = digest
            };
            var mergeReply = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                MergeArguments(SchematicJson.Formatter.Format(seededChecked), mergeOperation, seededDigest));
            RequireToolSuccess(mergeReply);
            var mergeData = mergeReply.GetProperty("structuredContent");
            Assert.IsTrue(mergeData.GetProperty("mutationSubmitted").GetBoolean(), mergeReply.GetRawText());
            Assert.IsFalse(mergeData.GetProperty("replayed").GetBoolean(), mergeReply.GetRawText());
            Assert.IsFalse(mergeData.GetProperty("designFilesSaved").GetBoolean(), mergeReply.GetRawText());
            var mergeReceipt = SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(
                mergeData.GetProperty("receipt").GetRawText());
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, mergeReceipt.Status, mergeReply.GetRawText());
            Assert.AreEqual(seededChecked.State, mergeReceipt.ObservedBefore, mergeReply.GetRawText());
            var mergedChecked = await CapturePublicState();
            var mergedSnapshot = await Read();
            Assert.AreEqual(mergeReceipt.ObservedAfter, mergedChecked.State, mergeReply.GetRawText());
            Assert.AreEqual(seededChecked.State.Revision.Sequence + 1, mergedChecked.State.Revision.Sequence);
            CollectionAssert.AreEquivalent(new[] { "AutomationExisting", "AutomationMerge", "Default" },
                PublicNetClassNames(mergedChecked), "MMM_MERGE must retain the unrelated existing class.");
            Assert.AreEqual(127000L, PublicSettings(mergedChecked).DefaultClass.Schematic.WireWidth.ValueNm);
            AssertUnrelatedSettings(seededSettings, PublicSettings(mergedChecked), "MMM_MERGE");
            Assert.AreEqual(mergedChecked.Electrical.Hierarchy.Data.Instances
                .Single(screen => screen.Metadata.Document.Equals(document)).Metadata.NetSettings,
                mergedSnapshot.Data.Metadata.NetSettings);
            string mergedDigest = PublicNetClassesDigest(
                await ReadPublicNetClasses(mergedChecked, "public-merged-read"));

            // A retry with every original argument must return the process-owned
            // receipt and leave the revision unchanged.
            var mergeReplay = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                MergeArguments(SchematicJson.Formatter.Format(seededChecked), mergeOperation, seededDigest));
            RequireToolSuccess(mergeReplay);
            Assert.IsTrue(mergeReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
                mergeReplay.GetRawText());
            Assert.IsFalse(mergeReplay.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
                mergeReplay.GetRawText());
            Assert.AreEqual(mergedChecked, await CapturePublicState(),
                "Exact project netclass replay must not create another native revision.");

            var staleDigest = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                MergeArguments(SchematicJson.Formatter.Format(mergedChecked), Guid.NewGuid().ToString("D"),
                    new string('0', 64)));
            Assert.IsTrue(staleDigest.GetProperty("isError").GetBoolean(), staleDigest.GetRawText());
            Assert.AreEqual("stale_project_net_classes", staleDigest.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), staleDigest.GetRawText());
            Assert.AreEqual(mergedChecked, await CapturePublicState(),
                "A stale netclass digest must not mutate the native project.");

            // Advance the native state outside the public wrapper, then submit the
            // old checked state with its valid digest.  This reaches the checked
            // transaction's stale-state refusal rather than the digest guard.
            await Apply(Batch(mergedSnapshot, seededSettings));
            var restoredSeeded = await Read();
            var staleState = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                MergeArguments(SchematicJson.Formatter.Format(mergedChecked), Guid.NewGuid().ToString("D"),
                    mergedDigest));
            Assert.IsTrue(staleState.GetProperty("isError").GetBoolean(), staleState.GetRawText());
            Assert.AreEqual("stale_document_state", staleState.GetProperty("structuredContent")
                .GetProperty("errorCode").GetString(), staleState.GetRawText());
            Assert.IsFalse(staleState.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
                staleState.GetRawText());
            Assert.AreEqual(restoredSeeded, await Read(),
                "A stale checked state must preserve the newer native settings.");

            // Replace is exercised from a fresh checked checkpoint.  It removes
            // AutomationExisting while keeping Default, the requested class, and
            // every unrelated setting seeded above.
            var replaceBefore = await Read();
            var replaceBeforeChecked = await CapturePublicState();
            var replaceBeforeQuery = await ReadPublicNetClasses(replaceBeforeChecked, "public-replace-read");
            string replaceDigest = PublicNetClassesDigest(replaceBeforeQuery);
            var replaceDefault = PublicSettings(replaceBeforeChecked).DefaultClass.Clone();
            replaceDefault.Board.TrackWidth = new() { ValueNm = 654300 };
            var replacement = replaceDefault.Clone();
            replacement.Name = "AutomationReplacement";
            replacement.Priority = 5;
            var replaceRequest = new NetClassesResponse();
            replaceRequest.NetClasses.Add(replaceDefault);
            replaceRequest.NetClasses.Add(replacement);
            string replaceOperation = Guid.NewGuid().ToString("D");
            object ReplaceArguments(string expectedState, string operation, string digest) => new
            {
                instanceId = projectNetClassesInstanceId,
                documentJson = publicDocumentJson,
                expectedStateJson = expectedState,
                netClassesJson = SchematicJson.Formatter.Format(replaceRequest),
                mergeMode = "MMM_REPLACE",
                operationId = operation,
                expectedNetClassesSha256 = digest
            };
            var replaceReply = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                ReplaceArguments(SchematicJson.Formatter.Format(replaceBeforeChecked), replaceOperation, replaceDigest));
            RequireToolSuccess(replaceReply);
            var replaceData = replaceReply.GetProperty("structuredContent");
            Assert.IsTrue(replaceData.GetProperty("mutationSubmitted").GetBoolean(), replaceReply.GetRawText());
            Assert.IsFalse(replaceData.GetProperty("replayed").GetBoolean(), replaceReply.GetRawText());
            var replaceAfterChecked = await CapturePublicState();
            var replaceAfter = await Read();
            CollectionAssert.AreEquivalent(new[] { "AutomationReplacement", "Default" },
                PublicNetClassNames(replaceAfterChecked), "MMM_REPLACE must remove the old named class.");
            Assert.AreEqual(654300L, PublicSettings(replaceAfterChecked).DefaultClass.Board.TrackWidth.ValueNm);
            AssertUnrelatedSettings(seededSettings, PublicSettings(replaceAfterChecked), "MMM_REPLACE");
            Assert.AreEqual(replaceAfterChecked.Electrical.Hierarchy.Data.Instances
                .Single(screen => screen.Metadata.Document.Equals(document)).Metadata.NetSettings,
                replaceAfter.Data.Metadata.NetSettings);

            var replaceReplay = await projectNetClassesMcp.Tool("kicad_project_net_classes_set",
                ReplaceArguments(SchematicJson.Formatter.Format(replaceBeforeChecked), replaceOperation, replaceDigest));
            RequireToolSuccess(replaceReplay);
            Assert.IsTrue(replaceReplay.GetProperty("structuredContent").GetProperty("replayed").GetBoolean(),
                replaceReplay.GetRawText());
            Assert.IsFalse(replaceReplay.GetProperty("structuredContent").GetProperty("mutationSubmitted").GetBoolean(),
                replaceReplay.GetRawText());
            Assert.AreEqual(replaceAfterChecked, await CapturePublicState(),
                "Exact replace replay must not create another native revision.");

            // The same native history and checked-save path used by the rest of
            // this journey proves the public wrapper is undoable and persisted.
            await UndoRedo(replaceBefore.Data, replaceAfter.Data);
            await Reopen(replaceAfter.Data, "public-netclasses-reopened", verifyReconnect: false);
            var reloadedPublic = await CapturePublicState();
            CollectionAssert.AreEquivalent(new[] { "AutomationReplacement", "Default" },
                PublicNetClassNames(reloadedPublic), "The public replace must survive save/reload.");
            var reloadedPublicQuery = await ReadPublicNetClasses(reloadedPublic, "public-netclasses-reloaded-read");
            CollectionAssert.AreEquivalent(new[] { "AutomationReplacement", "Default" },
                PublicNetClasses(reloadedPublicQuery).NetClasses.Select(value => value.Name).ToArray());
            AssertUnrelatedSettings(seededSettings, PublicSettings(reloadedPublic), "public-netclasses-reload");
        }
        finally
        {
            if (Directory.Exists(projectNetClassesStateDirectory))
                Directory.Delete(projectNetClassesStateDirectory, recursive: true);
        }
        await Apply(Batch(await Read(), original.Data.Metadata.NetSettings));
        await Reopen(original.Data, "public-netclasses-final-restored", verifyReconnect: false);
    }
}
