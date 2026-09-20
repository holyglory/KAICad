using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Real compiled STDIO process. This does not claim Codex Desktop or editor UI coverage.
[TestClass]
public sealed class McpProcessTests
{
    [TestMethod]
    public async Task StructuralFileCommandWorksWithoutAnMcpSession()
    {
        string state = Directory.CreateTempSubdirectory("kicad-structural-command-").FullName;
        try
        {
            var (circuit, diagram) = StructuralDiagramTests.Fixture();
            string path = Path.Combine(state, "design.xml");
            await File.WriteAllTextAsync(path, EngineeringDesignXml.Write(new(circuit, diagram, [], []), []));
            var request = new KiCad.Automation.Protocol.Structural.StructuralFileRequest
                { SchemaVersion = 1, RepositoryRoot = state, SourcePath = path };
            var read = await Invoke(request);
            Assert.IsTrue(read.Success); Assert.AreEqual(diagram.Id.ToString("D"), read.Document.DocumentId);
            request.Action = KiCad.Automation.Protocol.Structural.StructuralFileAction.SfaSave;
            request.ExpectedSourceToken = read.Document.SourceToken; request.Diagram = read.Document.Diagram.Clone();
            request.Diagram.Blocks[0].Purpose = "Saved through the finite native-editor helper.";
            var saved = await Invoke(request);
            Assert.IsTrue(saved.Success); Assert.AreNotEqual(read.Document.SourceToken, saved.Document.SourceToken);
            var stale = await Invoke(request);
            Assert.IsFalse(stale.Success); Assert.AreEqual("structural_file_changed", stale.ErrorCode);
        }
        finally { Directory.Delete(state, true); }

        async Task<KiCad.Automation.Protocol.Structural.StructuralFileResult> Invoke(
            KiCad.Automation.Protocol.Structural.StructuralFileRequest request)
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(FindAutomationRoot(), "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll"));
            start.ArgumentList.Add("--structural-file");
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
                var response = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(Google.Protobuf.JsonFormatter.Default.Format(request));
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var result = KiCad.Automation.Protocol.Structural.StructuralFileResult.Parser.ParseJson(await response);
                Assert.AreEqual(result.Success ? 0 : 1, process.ExitCode, await diagnostics);
                return result;
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        }
    }

    [TestMethod]
    public async Task InitializeDiscoverAndCallOverStdio()
    {
        string root = FindAutomationRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(root, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll");
        string state = Directory.CreateTempSubdirectory("kicad-mcp-test-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(executable);
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
        using Process process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            JsonElement initialized = await Request(1, "initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "compiled-acceptance-fixture", version = "1" }
            });
            Assert.IsTrue(initialized.GetProperty("result").TryGetProperty("serverInfo", out _));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            JsonElement listed = await Request(2, "tools/list", new { });
            string[] names = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!).ToArray();
            CollectionAssert.Contains(names, "kicad_instances_list");
            CollectionAssert.Contains(names, "kicad_instance_capabilities");
            CollectionAssert.Contains(names, "kicad_pcb_drc_start");
            CollectionAssert.Contains(names, "kicad_pcb_drc_job");
            CollectionAssert.Contains(names, "kicad_pcb_drc_cancel");
            var unknownCapabilities = await Request(3, "tools/call", new { name = "kicad_instance_capabilities",
                arguments = new { instanceId = Guid.NewGuid().ToString("D") } });
            Assert.IsTrue(unknownCapabilities.GetProperty("result").GetProperty("isError").GetBoolean());
            foreach (string suffix in new[] { "start", "list", "wait", "resume", "stop" })
                CollectionAssert.Contains(names, "kicad_design_native_intake_" + suffix);
            var nativeIntakes = await Request(9060, "tools/call", new { name = "kicad_design_native_intake_list",
                arguments = new { instanceId = Guid.NewGuid().ToString("D") } });
            Assert.AreEqual(0, nativeIntakes.GetProperty("result").GetProperty("structuredContent").GetProperty("sessions").GetArrayLength());
            var invalidNativeIntake = await Request(9061, "tools/call", new { name = "kicad_design_native_intake_start",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath = "relative.json" } });
            Assert.AreEqual("invalid_native_intake_target", invalidNativeIntake.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            CollectionAssert.Contains(names, "kicad_instance_reconnect_after_update");
            var invalidReconnect = await Request(1000, "tools/call", new { name = "kicad_instance_reconnect_after_update",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), installationRoot = Path.Combine(state, "absent-installation"),
                    operationId = "invalid", expectedOldEpoch = "old" } });
            Assert.IsTrue(invalidReconnect.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.AreEqual("invalid_operation", invalidReconnect.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString());
            CollectionAssert.Contains(names, "kicad_schematic_open");
            CollectionAssert.Contains(names, "kicad_schematic_create");
            CollectionAssert.Contains(names, "kicad_pcb_open");
            CollectionAssert.Contains(names, "kicad_pcb_create");
            CollectionAssert.Contains(names, "kicad_document_state");
            CollectionAssert.Contains(names, "kicad_document_save");
            CollectionAssert.Contains(names, "kicad_schematic_apply_checked_batch");
            CollectionAssert.Contains(names, "kicad_schematic_checked_state");
            CollectionAssert.Contains(names, "kicad_schematic_checked_batch_receipt");
            CollectionAssert.Contains(names, "kicad_document_operation");
            CollectionAssert.Contains(names, "kicad_pcb_drc_state");
            CollectionAssert.Contains(names, "kicad_pcb_drc_start");
            CollectionAssert.Contains(names, "kicad_pcb_drc_job");
            CollectionAssert.Contains(names, "kicad_pcb_drc_cancel");
            CollectionAssert.Contains(names, "kicad_document_close");
            CollectionAssert.Contains(names, "kicad_schematic_preview");
            CollectionAssert.Contains(names, "kicad_schematic_electrical_state");
            CollectionAssert.Contains(names, "kicad_schematic_measure_placement");
            CollectionAssert.Contains(names, "kicad_structure_open");
            CollectionAssert.Contains(names, "kicad_structure_state");
            var invalidGeometry = await Request(9088, "tools/call", new { name = "kicad_schematic_measure_placement",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), requestJson = "{}" } });
            Assert.IsTrue(invalidGeometry.GetProperty("result").GetProperty("isError").GetBoolean());
            CollectionAssert.Contains(names, "kicad_design_electrical_baseline_initialize");
            CollectionAssert.Contains(names, "kicad_design_connectivity_compare");
            var electricalFixture = SchematicElectricalComparisonTests.Fixture();
            var compare = await Request(9070, "tools/call", new { name = "kicad_design_connectivity_compare", arguments = new
            {
                designXml = SchematicDesignXml.Write(electricalFixture.Design, [electricalFixture.Library]),
                electricalStateJson = SchematicJson.Formatter.Format(electricalFixture.State),
                knowledgeLibraryXml = new[] { ComponentKnowledgeXml.WriteLibrary(electricalFixture.Library) }
            } });
            Assert.IsTrue(compare.GetProperty("result").GetProperty("structuredContent").GetProperty("connectivityEquivalent").GetBoolean());
            var malformedElectrical = await Request(9071, "tools/call", new { name = "kicad_design_connectivity_compare", arguments = new
            {
                designXml = SchematicDesignXml.Write(electricalFixture.Design, [electricalFixture.Library]),
                electricalStateJson = "{broken", knowledgeLibraryXml = new[] { ComponentKnowledgeXml.WriteLibrary(electricalFixture.Library) }
            } });
            Assert.AreEqual("invalid_electrical_snapshot", malformedElectrical.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            CollectionAssert.Contains(names, "kicad_schematic_render_views");
            CollectionAssert.Contains(names, "kicad_schematic_sheet_activate");
            CollectionAssert.Contains(names, "kicad_schematic_move_connected_symbols");
            CollectionAssert.Contains(names, "kicad_schematic_transform_connected_symbols");
            Assert.IsFalse(names.Any(n => n.Contains("route", StringComparison.Ordinal)));
            CollectionAssert.Contains(names, "kicad_schematic_xml_plan");
            JsonElement call = await Request(3, "tools/call", new { name = "kicad_instances_list", arguments = new { } });
            Assert.IsFalse(call.TryGetProperty("error", out _), call.ToString());
            JsonElement result = call.GetProperty("result");
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean());
            string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.AreEqual(0, JsonDocument.Parse(text).RootElement.GetArrayLength());

            CollectionAssert.Contains(names, "kicad_component_guidance_resolve");
            Circuit circuit = CircuitXmlTests.Fixture();
            Guid typeId = Guid.NewGuid();
            var note = new GuidanceStatement(Guid.NewGuid(), "thermal-placement", "placement",
                "Prefer the cooling region; no distance has been specified.", GuidanceStrength.Preference, "", []);
            var library = new ComponentKnowledgeLibrary(Guid.NewGuid(), "fixture-r1",
                [new(typeId, "Example class", null, [note])]);
            var binding = new ComponentKnowledgeBinding(circuit.Components[0].Id, library.Id, library.Revision, typeId, []);
            string circuitXml = CircuitXml.Write(circuit), libraryXml = ComponentKnowledgeXml.WriteLibrary(library);
            string bindingXml = ComponentKnowledgeXml.WriteBinding(binding, library);
            JsonElement knowledge = await Request(4, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml, bindingXml } });
            GuidanceToolResult resolved = ReadGuidance(knowledge);
            Assert.IsTrue(resolved.Valid);
            Assert.AreEqual(circuit.Components[0].Id, resolved.ComponentInstanceId);
            Assert.AreEqual(note.Text, resolved.Resolution!.Effective.Single().Statement.Text);
            Assert.AreEqual(VerificationState.Unverified, resolved.Resolution.Effective.Single().Statement.Verification);

            JsonElement invalid = await Request(5, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml, bindingXml = bindingXml.Replace("fixture-r1", "fixture-r2", StringComparison.Ordinal) } });
            GuidanceToolResult rejected = ReadGuidance(invalid);
            Assert.IsFalse(rejected.Valid);
            Assert.AreEqual("library_revision_mismatch", rejected.ErrorCode);
            Assert.IsNull(rejected.Resolution);
            JsonElement recovered = await Request(6, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml, bindingXml } });
            Assert.IsTrue(ReadGuidance(recovered).Valid, "A rejected input must not poison later requests.");
            var quantityNote = note with { Quantity = new(ParameterKind.AbsoluteMaximum, "V", Maximum: 5m),
                Sources = [new("synthetic-datasheet", "rev-A", 2, "Absolute maximum", "example-package")] };
            var quantityLibrary = library with { Classes = [new(typeId, "Example class", null, [quantityNote])] };
            string quantityXml = ComponentKnowledgeXml.WriteLibrary(quantityLibrary);
            var quantityReply = ReadGuidance(await Request(9085, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml = quantityXml, bindingXml } }));
            Assert.IsTrue(quantityReply.Valid);
            Assert.AreEqual(quantityNote.Quantity, quantityReply.Resolution!.Effective.Single().Statement.Quantity);
            Assert.AreEqual(quantityNote.Sources[0], quantityReply.Resolution.Effective.Single().Statement.Sources[0]);
            Assert.AreEqual(VerificationState.Unverified, quantityReply.Resolution.Effective.Single().Statement.Verification);
            var badQuantity = ReadGuidance(await Request(9086, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml = quantityXml.Replace("maximum=\"5\"", "maximum=\"NaN\"", StringComparison.Ordinal), bindingXml } }));
            Assert.IsFalse(badQuantity.Valid);
            var conflictingLibrary = library with { Classes = [new(typeId, "Example class", null,
                [quantityNote with { Quantity = new(ParameterKind.OperatingLimit, "V", Minimum: 5m, Maximum: 3m) }])] };
            var quantityRecovery = ReadGuidance(await Request(9087, "tools/call", new { name = "kicad_component_guidance_resolve",
                arguments = new { circuitXml, libraryXml = ComponentKnowledgeXml.WriteLibrary(conflictingLibrary), bindingXml } }));
            Assert.IsTrue(quantityRecovery.Valid, "Valid reports structural validity; engineering issues remain explicit.");
            Assert.AreEqual("inverted_range", quantityRecovery.Resolution!.QuantityIssues!.Single().Code);
            CollectionAssert.Contains(names, "kicad_engineering_design_validate");
            var (engineering, engineeringLibrary) = EngineeringDesignXmlTests.Fixture();
            string engineeringXml = EngineeringDesignXml.Write(engineering, [engineeringLibrary]);
            string[] knowledgeLibraryXml = [ComponentKnowledgeXml.WriteLibrary(engineeringLibrary)];
            var engineeringResult = await Request(1001, "tools/call", new { name = "kicad_engineering_design_validate",
                arguments = new { designXml = engineeringXml, knowledgeLibraryXml } });
            var engineeringState = engineeringResult.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(engineeringState.GetProperty("modelValid").GetBoolean());
            Assert.IsTrue(engineeringState.GetProperty("netBindingsResolved").GetBoolean());
            Assert.AreEqual(engineering.Circuit.Id, engineeringState.GetProperty("designId").GetGuid());
            Assert.AreEqual(1, engineeringState.GetProperty("guidance").EnumerateObject().Count());
            var missingLibrary = await Request(1002, "tools/call", new { name = "kicad_engineering_design_validate",
                arguments = new { designXml = engineeringXml, knowledgeLibraryXml = Array.Empty<string>() } });
            var missingState = missingLibrary.GetProperty("result").GetProperty("structuredContent");
            Assert.IsFalse(missingState.GetProperty("modelValid").GetBoolean());
            Assert.AreEqual("missing_knowledge_library", missingState.GetProperty("errorCode").GetString());
            var engineeringRecovery = await Request(1003, "tools/call", new { name = "kicad_engineering_design_validate",
                arguments = new { designXml = engineeringXml, knowledgeLibraryXml } });
            Assert.IsTrue(engineeringRecovery.GetProperty("result").GetProperty("structuredContent").GetProperty("modelValid").GetBoolean());
            var split = UnresolvedNetBindingTests.Split();
            string unresolvedXml = EngineeringDesignXml.Write(new(split.After, split.Pending, [], []), []);
            var unresolved = await Request(1004, "tools/call", new { name = "kicad_engineering_design_validate",
                arguments = new { designXml = unresolvedXml, knowledgeLibraryXml = Array.Empty<string>() } });
            var unresolvedState = unresolved.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(unresolvedState.GetProperty("modelValid").GetBoolean());
            Assert.IsFalse(unresolvedState.GetProperty("netBindingsResolved").GetBoolean());
            Assert.AreEqual(2, unresolvedState.GetProperty("unresolvedNetBindings").GetArrayLength());
            Assert.AreEqual(split.Before.Nets[0].Id, unresolvedState.GetProperty("unresolvedNetBindings")[0].GetProperty("formerNetId").GetGuid());
            CollectionAssert.Contains(names, "kicad_design_bindings_inspect");
            var (linkedDesign, linkedLibrary) = SchematicDesignTests.Fixture();
            string linkedXml = SchematicDesignXml.Write(linkedDesign, [linkedLibrary]);
            string[] linkedLibraries = [ComponentKnowledgeXml.WriteLibrary(linkedLibrary)];
            var links = await Request(2001, "tools/call", new { name = "kicad_design_bindings_inspect",
                arguments = new { designXml = linkedXml, knowledgeLibraryXml = linkedLibraries } });
            var linksState = links.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(linksState.GetProperty("documentParsed").GetBoolean());
            Assert.IsTrue(linksState.GetProperty("bindingReport").GetProperty("identitiesResolved").GetBoolean());
            Assert.AreEqual(2, linksState.GetProperty("bindingReport").GetProperty("coverageGaps").GetArrayLength());
            var brokenLinks = linkedDesign with { SymbolBindings = [] };
            var unresolvedLinks = await Request(2002, "tools/call", new { name = "kicad_design_bindings_inspect",
                arguments = new { designXml = SchematicDesignXml.Write(brokenLinks, [linkedLibrary]), knowledgeLibraryXml = linkedLibraries } });
            var unresolvedLinksState = unresolvedLinks.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(unresolvedLinksState.GetProperty("documentParsed").GetBoolean());
            Assert.IsFalse(unresolvedLinksState.GetProperty("bindingReport").GetProperty("identitiesResolved").GetBoolean());
            var invalidDesign = await Request(2003, "tools/call", new { name = "kicad_design_bindings_inspect",
                arguments = new { designXml = "<invalid>", knowledgeLibraryXml = linkedLibraries } });
            Assert.IsFalse(invalidDesign.GetProperty("result").GetProperty("structuredContent").GetProperty("documentParsed").GetBoolean());
            var linksRecovery = await Request(2004, "tools/call", new { name = "kicad_design_bindings_inspect",
                arguments = new { designXml = linkedXml, knowledgeLibraryXml = linkedLibraries } });
            Assert.IsTrue(linksRecovery.GetProperty("result").GetProperty("structuredContent")
                .GetProperty("bindingReport").GetProperty("identitiesResolved").GetBoolean());
            CollectionAssert.Contains(names, "kicad_design_reconcile_properties");
            var (projectionBase, projectionLibrary) = SchematicModelProjectionTests.Fixture();
            string projectionXml = SchematicDesignXml.Write(projectionBase, [projectionLibrary]);
            string desiredProjectionXml = EngineeringDesignXml.Write(projectionBase.Engineering, [projectionLibrary]);
            string observedProjectionXml = SchematicDataXml.Write(projectionBase.Schematic);
            string[] projectionLibraries = [ComponentKnowledgeXml.WriteLibrary(projectionLibrary)];
            var projection = await Request(3001, "tools/call", new { name = "kicad_design_reconcile_properties", arguments = new
                { baselineDesignXml = projectionXml, desiredEngineeringXml = desiredProjectionXml,
                    observedHierarchyXml = observedProjectionXml, knowledgeLibraryXml = projectionLibraries } });
            var projectionState = projection.GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual(desiredProjectionXml, projectionState.GetProperty("candidateEngineeringXml").GetString());
            Assert.IsFalse(projectionState.GetProperty("unprojectedSnapshotChanges").GetBoolean());
            var invalidProjection = await Request(3002, "tools/call", new { name = "kicad_design_reconcile_properties", arguments = new
                { baselineDesignXml = projectionXml, desiredEngineeringXml = desiredProjectionXml,
                    observedHierarchyXml = SchematicDataXml.Write(new SchematicText()), knowledgeLibraryXml = projectionLibraries } });
            Assert.AreEqual("invalid_design_xml", invalidProjection.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var projectionRecovery = await Request(3003, "tools/call", new { name = "kicad_design_reconcile_properties", arguments = new
                { baselineDesignXml = projectionXml, desiredEngineeringXml = desiredProjectionXml,
                    observedHierarchyXml = observedProjectionXml, knowledgeLibraryXml = projectionLibraries } });
            Assert.AreEqual(desiredProjectionXml, projectionRecovery.GetProperty("result").GetProperty("structuredContent")
                .GetProperty("candidateEngineeringXml").GetString());
            CollectionAssert.Contains(names, "kicad_design_plan_placement");
            var (placementBase, placementLibrary) = SchematicPlacementPlanTests.Fixture();
            var desiredPlacement = placementBase.Engineering with { Circuit = placementBase.Engineering.Circuit with
                { Symbols = placementBase.Engineering.Circuit.Symbols.Select(s => s with
                    { Placement = s.Placement! with { XMillimeters = s.Placement.XMillimeters + 1 } }).ToArray() } };
            string placementXml = SchematicDesignXml.Write(placementBase, [placementLibrary]);
            string desiredPlacementXml = EngineeringDesignXml.Write(desiredPlacement, [placementLibrary]);
            string observedPlacementXml = SchematicDataXml.Write(placementBase.Schematic);
            string[] placementLibraries = [ComponentKnowledgeXml.WriteLibrary(placementLibrary)];
            var invalidPlacement = await Request(4001, "tools/call", new { name = "kicad_design_plan_placement", arguments = new
                { baselineDesignXml = placementXml, desiredEngineeringXml = desiredPlacementXml,
                    observedHierarchyXml = SchematicDataXml.Write(new SchematicText()), knowledgeLibraryXml = placementLibraries } });
            Assert.AreEqual("invalid_design_xml", invalidPlacement.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var placement = await Request(4002, "tools/call", new { name = "kicad_design_plan_placement", arguments = new
                { baselineDesignXml = placementXml, desiredEngineeringXml = desiredPlacementXml,
                    observedHierarchyXml = observedPlacementXml, knowledgeLibraryXml = placementLibraries } });
            var placementState = placement.GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual(0, placementState.GetProperty("issues").GetArrayLength());
            Assert.AreEqual(1, placementState.GetProperty("moves").GetArrayLength());
            Assert.AreEqual(2, placementState.GetProperty("moves")[0].GetProperty("symbolIds").GetArrayLength());
            Assert.AreEqual(1000000L, placementState.GetProperty("moves")[0].GetProperty("deltaXNm").GetInt64());
            Assert.AreEqual(desiredPlacementXml, placementState.GetProperty("reconciledEngineeringXml").GetString());
            Assert.IsTrue(placementState.GetProperty("coverageGaps").GetArrayLength() > 0);
            CollectionAssert.Contains(names, "kicad_schematic_hierarchy_reconcile");
            var hierarchyBase = SchematicHierarchyTopologyTests.Fixture();
            var hierarchyDesired = hierarchyBase.Clone(); var hierarchyNative = hierarchyBase.Clone();
            foreach (var instance in hierarchyDesired.Instances)
                instance.Metadata.TextVariables.Add("XML_NOTE", "Place near connector");
            foreach (var instance in hierarchyNative.Instances)
                instance.Metadata.TextVariables.Add("NATIVE_NOTE", "Leave service access");
            string hierarchyBaselineXml = SchematicDataXml.Write(hierarchyBase);
            string hierarchyDesiredXml = SchematicDataXml.Write(hierarchyDesired);
            string hierarchyNativeXml = SchematicDataXml.Write(hierarchyNative);
            var invalidHierarchyMerge = await Request(4010, "tools/call", new { name = "kicad_schematic_hierarchy_reconcile",
                arguments = new { baselineXml = "<invalid>", desiredXml = hierarchyDesiredXml, nativeXml = hierarchyNativeXml } });
            Assert.IsTrue(invalidHierarchyMerge.GetProperty("result").GetProperty("isError").GetBoolean());
            var hierarchyMerge = await Request(4011, "tools/call", new { name = "kicad_schematic_hierarchy_reconcile",
                arguments = new { baselineXml = hierarchyBaselineXml, desiredXml = hierarchyDesiredXml, nativeXml = hierarchyNativeXml } });
            var hierarchyMergeState = hierarchyMerge.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(hierarchyMergeState.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(hierarchyMergeState.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual(1, hierarchyMergeState.GetProperty("operations").GetArrayLength());
            var hierarchyCombined = (SchematicHierarchyData)SchematicDataXml.Read(hierarchyMergeState.GetProperty("mergedXml").GetString()!);
            Assert.IsTrue(hierarchyCombined.Instances.All(s => s.Metadata.TextVariables.ContainsKey("XML_NOTE")
                && s.Metadata.TextVariables.ContainsKey("NATIVE_NOTE")));
            foreach (var instance in hierarchyNative.Instances) instance.Metadata.TextVariables.Add("XML_NOTE", "Competing native instruction");
            var hierarchyConflict = await Request(4012, "tools/call", new { name = "kicad_schematic_hierarchy_reconcile",
                arguments = new { baselineXml = hierarchyBaselineXml, desiredXml = hierarchyDesiredXml, nativeXml = SchematicDataXml.Write(hierarchyNative) } });
            var hierarchyConflictState = hierarchyConflict.GetProperty("result").GetProperty("structuredContent");
            Assert.IsFalse(hierarchyConflictState.GetProperty("canPlan").GetBoolean());
            Assert.AreEqual(0, hierarchyConflictState.GetProperty("operations").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, hierarchyConflictState.GetProperty("mergedXml").ValueKind);
            Assert.IsTrue(hierarchyConflictState.GetProperty("conflicts").GetArrayLength() > 0);
            Assert.IsNotNull(hierarchyConflictState.GetProperty("conflicts")[0].GetProperty("baselineXml").GetString());
            var sheetChoices = hierarchyConflictState.GetProperty("conflicts").EnumerateArray()
                .ToDictionary(c => c.GetProperty("instancePath").GetString()!, _ => "xml");
            string conflictToken = hierarchyConflictState.GetProperty("snapshotToken").GetString()!;
            var staleHierarchyChoice = await Request(4013, "tools/call", new { name = "kicad_schematic_hierarchy_reconcile",
                arguments = new { baselineXml = hierarchyBaselineXml, desiredXml = hierarchyDesiredXml,
                    nativeXml = SchematicDataXml.Write(hierarchyNative), choices = sheetChoices, expectedSnapshotToken = "stale" } });
            Assert.IsTrue(staleHierarchyChoice.GetProperty("result").GetProperty("isError").GetBoolean());
            var chosenHierarchy = await Request(4014, "tools/call", new { name = "kicad_schematic_hierarchy_reconcile",
                arguments = new { baselineXml = hierarchyBaselineXml, desiredXml = hierarchyDesiredXml,
                    nativeXml = SchematicDataXml.Write(hierarchyNative), choices = sheetChoices, expectedSnapshotToken = conflictToken } });
            var chosenHierarchyState = chosenHierarchy.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(chosenHierarchyState.GetProperty("canPlan").GetBoolean());
            var chosenHierarchyData = (SchematicHierarchyData)SchematicDataXml.Read(chosenHierarchyState.GetProperty("mergedXml").GetString()!);
            Assert.IsTrue(chosenHierarchyData.Instances.All(s => s.Metadata.TextVariables["XML_NOTE"] == "Place near connector"));
            Assert.IsFalse(chosenHierarchyState.GetProperty("liveMutationAuthorized").GetBoolean());
            CollectionAssert.Contains(names, "kicad_design_recovery_plan");
            CollectionAssert.Contains(names, "kicad_design_recovery_resolve");
            CollectionAssert.Contains(names, "kicad_design_nets_reconcile");
            CollectionAssert.Contains(names, "kicad_design_sync_plan");
            CollectionAssert.Contains(names, "kicad_design_sync_apply");
            var invalidApply = await Request(4083, "tools/call", new { name = "kicad_design_sync_apply",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath = Path.Combine(state, "missing-recovery.json"),
                    designPath = Path.Combine(state, "design.xml"), expectedRevisionToken = "stale", operationId = Guid.NewGuid().ToString("D") } });
            Assert.IsTrue(invalidApply.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.AreEqual("missing_design_recovery", invalidApply.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            string syncRecoveryPath = Path.Combine(state, "designs", "sync-recovery.json");
            var syncFixture = SchematicSynchronizationPlanTests.Fixture();
            var syncStore = new DesignRecoveryStore(syncRecoveryPath);
            var syncSaved = syncStore.Save(syncFixture, null);
            byte[] syncOriginal = await File.ReadAllBytesAsync(syncRecoveryPath, timeout.Token);
            var syncPlan = await Request(4080, "tools/call", new { name = "kicad_design_sync_plan",
                arguments = new { instanceId = syncFixture.InstanceId.ToString("D"), recoveryPath = syncRecoveryPath,
                    expectedRevisionToken = syncSaved.RevisionToken } });
            Assert.IsFalse(syncPlan.GetProperty("result").GetProperty("isError").GetBoolean(),
                syncPlan.GetProperty("result").GetProperty("structuredContent").GetProperty("errorMessage").GetString());
            var syncData = syncPlan.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(syncData.GetProperty("canPrepare").GetBoolean());
            Assert.IsFalse(syncData.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.IsFalse(syncData.GetProperty("designFileWritten").GetBoolean());
            Assert.IsFalse(syncData.GetProperty("baselineAdvanced").GetBoolean());
            Assert.AreEqual(SchematicDesignXml.Write(syncFixture.Baseline, syncFixture.KnowledgeLibraries),
                syncData.GetProperty("candidateDesignXml").GetString());
            string candidateXml = syncData.GetProperty("candidateDesignXml").GetString()!;
            var candidateDesign = SchematicDesignXml.Read(candidateXml, syncFixture.KnowledgeLibraries);
            candidateDesign.Schematic.Instances[0].Metadata.TitleBlock.Title = "Stored production MCP candidate";
            candidateXml = SchematicDesignXml.Write(candidateDesign, syncFixture.KnowledgeLibraries);
            string candidateHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(candidateXml)));
            var candidateCommit = await Request(4084, "tools/call", new { name = "kicad_design_candidate_commit",
                arguments = new { instanceId = syncFixture.InstanceId.ToString("D"), recoveryPath = syncRecoveryPath,
                    expectedRevisionToken = syncSaved.RevisionToken, candidateXml, expectedCandidateSha256 = candidateHash,
                    operationId = Guid.NewGuid().ToString("D") } });
            var candidateCommitResult = candidateCommit.GetProperty("result");
            Assert.IsFalse(candidateCommitResult.TryGetProperty("isError", out var candidateCommitError) && candidateCommitError.GetBoolean());
            var candidateCommitState = candidateCommit.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(candidateCommitState.GetProperty("desiredCandidateStored").GetBoolean());
            Assert.IsFalse(candidateCommitState.GetProperty("designFileWritten").GetBoolean());
            Assert.IsFalse(candidateCommitState.GetProperty("nativeMutationCommitted").GetBoolean());
            var committedRecoveryBytes = await File.ReadAllBytesAsync(syncRecoveryPath, timeout.Token);
            CollectionAssert.AreNotEqual(syncOriginal, committedRecoveryBytes);
            Assert.AreEqual(candidateCommitState.GetProperty("recoveryRevisionToken").GetString(), syncStore.Read()!.RevisionToken);
            var staleSync = await Request(4081, "tools/call", new { name = "kicad_design_sync_plan",
                arguments = new { instanceId = syncFixture.InstanceId.ToString("D"), recoveryPath = syncRecoveryPath,
                    expectedRevisionToken = "stale" } });
            Assert.AreEqual("design_recovery_changed", staleSync.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var wrongSync = await Request(4082, "tools/call", new { name = "kicad_design_sync_plan",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath = syncRecoveryPath,
                    expectedRevisionToken = syncSaved.RevisionToken } });
            Assert.AreEqual("recovery_instance_mismatch", wrongSync.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new RecoveryTools().PlanSynchronization(
                syncFixture.InstanceId.ToString("D"), syncRecoveryPath, syncSaved.RevisionToken, new CancellationToken(true)));
            CollectionAssert.AreEqual(committedRecoveryBytes, await File.ReadAllBytesAsync(syncRecoveryPath, timeout.Token));
            string netRecoveryPath = Path.Combine(state, "designs", "net-recovery.json");
            var netFixture = SchematicNetReconciliationTests.Fixture();
            var netStore = new DesignRecoveryStore(netRecoveryPath);
            var netSaved = netStore.Save(netFixture, null);
            byte[] netOriginalBytes = await File.ReadAllBytesAsync(netRecoveryPath, timeout.Token);
            var netPlan = await Request(4070, "tools/call", new { name = "kicad_design_nets_reconcile",
                arguments = new { instanceId = netFixture.InstanceId.ToString("D"), recoveryPath = netRecoveryPath,
                    expectedRevisionToken = netSaved.RevisionToken } });
            var netPlanState = netPlan.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(netPlanState.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(netPlanState.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual(EngineeringDesignXml.Write(netFixture.Baseline.Engineering, netFixture.KnowledgeLibraries),
                netPlanState.GetProperty("candidateEngineeringXml").GetString());
            Assert.AreEqual(netSaved.RevisionToken, netStore.Read()!.RevisionToken);
            var staleNetPlan = await Request(4071, "tools/call", new { name = "kicad_design_nets_reconcile",
                arguments = new { instanceId = netFixture.InstanceId.ToString("D"), recoveryPath = netRecoveryPath,
                    expectedRevisionToken = "stale" } });
            Assert.AreEqual("design_recovery_changed", staleNetPlan.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var foreignNetPlan = await Request(4072, "tools/call", new { name = "kicad_design_nets_reconcile",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), recoveryPath = netRecoveryPath,
                    expectedRevisionToken = netSaved.RevisionToken } });
            Assert.AreEqual("recovery_instance_mismatch", foreignNetPlan.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            using (var cancelledNetPlan = new CancellationTokenSource())
            {
                cancelledNetPlan.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => new RecoveryTools().ReconcileNets(
                    netFixture.InstanceId.ToString("D"), netRecoveryPath, netSaved.RevisionToken, cancelledNetPlan.Token));
            }
            CollectionAssert.AreEqual(netOriginalBytes, await File.ReadAllBytesAsync(netRecoveryPath, timeout.Token));
            var incompleteNetSaved = netStore.Save(netFixture with { BaselineElectrical = null }, netSaved.RevisionToken);
            var incompleteNetPlan = await Request(4073, "tools/call", new { name = "kicad_design_nets_reconcile",
                arguments = new { instanceId = netFixture.InstanceId.ToString("D"), recoveryPath = netRecoveryPath,
                    expectedRevisionToken = incompleteNetSaved.RevisionToken } });
            Assert.IsTrue(incompleteNetPlan.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.AreEqual("missing_electrical_baseline", incompleteNetPlan.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            Assert.AreEqual(incompleteNetSaved.RevisionToken, netStore.Read()!.RevisionToken);
            string recoveryPath = Path.Combine(state, "designs", "design-recovery.json");
            var recoveryFixture = DesignRecoveryStoreTests.Fixture();
            recoveryFixture = recoveryFixture with
            {
                Baseline = recoveryFixture.Baseline with { Schematic = hierarchyBase, SheetBindings = [], SymbolBindings = [] },
                Observed = hierarchyNative,
                DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(
                    recoveryFixture.Baseline with { Schematic = hierarchyDesired, SheetBindings = [], SymbolBindings = [] }, recoveryFixture.KnowledgeLibraries))
            };
            var recoveryStore = new DesignRecoveryStore(recoveryPath); var recoverySaved = recoveryStore.Save(recoveryFixture, null);
            string recoveryInstance = recoveryFixture.InstanceId.ToString("D");
            var savedPlan = await Request(4020, "tools/call", new { name = "kicad_design_recovery_plan",
                arguments = new { instanceId = recoveryInstance, recoveryPath } });
            var savedPlanState = savedPlan.GetProperty("result").GetProperty("structuredContent");
            Assert.IsFalse(savedPlanState.GetProperty("canPlan").GetBoolean());
            var savedChoices = savedPlanState.GetProperty("conflicts").EnumerateArray()
                .ToDictionary(c => c.GetProperty("instancePath").GetString()!, _ => "xml");
            string savedSnapshot = savedPlanState.GetProperty("snapshotToken").GetString()!;
            var wrongRecovery = await Request(4021, "tools/call", new { name = "kicad_design_recovery_resolve", arguments = new
                { instanceId = Guid.NewGuid().ToString("D"), recoveryPath, expectedRevisionToken = recoverySaved.RevisionToken,
                    expectedSnapshotToken = savedSnapshot, choices = savedChoices } });
            Assert.IsTrue(wrongRecovery.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.AreEqual(recoverySaved.RevisionToken, recoveryStore.Read()!.RevisionToken);
            var staleRecovery = await Request(4022, "tools/call", new { name = "kicad_design_recovery_resolve", arguments = new
                { instanceId = recoveryInstance, recoveryPath, expectedRevisionToken = "stale",
                    expectedSnapshotToken = savedSnapshot, choices = savedChoices } });
            Assert.IsTrue(staleRecovery.GetProperty("result").GetProperty("isError").GetBoolean());
            using (var cancelledRecovery = new CancellationTokenSource())
            {
                cancelledRecovery.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => new RecoveryTools().Resolve(recoveryInstance,
                    recoveryPath, recoverySaved.RevisionToken, savedSnapshot, savedChoices, cancelledRecovery.Token));
                Assert.AreEqual(recoverySaved.RevisionToken, recoveryStore.Read()!.RevisionToken);
            }
            using (var heldRecoveryLock = new FileStream(recoveryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedRecovery = await Request(4025, "tools/call", new { name = "kicad_design_recovery_resolve", arguments = new
                    { instanceId = recoveryInstance, recoveryPath, expectedRevisionToken = recoverySaved.RevisionToken,
                        expectedSnapshotToken = savedSnapshot, choices = savedChoices } });
                Assert.IsTrue(lockedRecovery.GetProperty("result").GetProperty("isError").GetBoolean());
                Assert.AreEqual(recoverySaved.RevisionToken, recoveryStore.Read()!.RevisionToken);
            }
            var resolvedRecovery = await Request(4023, "tools/call", new { name = "kicad_design_recovery_resolve", arguments = new
                { instanceId = recoveryInstance, recoveryPath, expectedRevisionToken = recoverySaved.RevisionToken,
                    expectedSnapshotToken = savedSnapshot, choices = savedChoices } });
            var resolvedRecoveryState = resolvedRecovery.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(resolvedRecoveryState.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(resolvedRecoveryState.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual(resolvedRecoveryState.GetProperty("recoveryRevisionToken").GetString(), new DesignRecoveryStore(recoveryPath).Read()!.RevisionToken);
            CollectionAssert.AreEqual(recoverySaved.State.DesiredFileBytes, recoveryStore.Read()!.State.DesiredFileBytes);
            var reopenedRecovery = await Request(4024, "tools/call", new { name = "kicad_design_recovery_plan",
                arguments = new { instanceId = recoveryInstance, recoveryPath } });
            Assert.AreEqual(resolvedRecoveryState.GetProperty("recoveryRevisionToken").GetString(),
                reopenedRecovery.GetProperty("result").GetProperty("structuredContent").GetProperty("recoveryRevisionToken").GetString());
            CollectionAssert.Contains(names, "kicad_design_intake_start");
            string watchedDesign = Path.Combine(state, "designs", "design.xml");
            await File.WriteAllBytesAsync(watchedDesign, recoveryFixture.DesiredFileBytes, timeout.Token);
            var startedIntake = await Request(4030, "tools/call", new { name = "kicad_design_intake_start",
                arguments = new { instanceId = recoveryInstance, recoveryPath, designPath = watchedDesign } });
            string intakeId = startedIntake.GetProperty("result").GetProperty("structuredContent").GetProperty("intakeId").GetString()!;
            CollectionAssert.Contains(names, "kicad_design_intake_list");
            var recoveredIntakes = (await Request(4060, "tools/call", new { name = "kicad_design_intake_list",
                arguments = new { instanceId = recoveryInstance } })).GetProperty("result").GetProperty("structuredContent").GetProperty("sessions");
            Assert.AreEqual(1, recoveredIntakes.GetArrayLength());
            Assert.AreEqual(intakeId, recoveredIntakes[0].GetProperty("intakeId").GetString());
            var otherIntakes = (await Request(4061, "tools/call", new { name = "kicad_design_intake_list",
                arguments = new { instanceId = Guid.NewGuid().ToString("D") } })).GetProperty("result").GetProperty("structuredContent").GetProperty("sessions");
            Assert.AreEqual(0, otherIntakes.GetArrayLength());
            var invalidIntakeList = await Request(4062, "tools/call", new { name = "kicad_design_intake_list",
                arguments = new { instanceId = "" } });
            Assert.IsTrue(invalidIntakeList.GetProperty("result").GetProperty("isError").GetBoolean());
            var duplicateIntake = await Request(4031, "tools/call", new { name = "kicad_design_intake_start",
                arguments = new { instanceId = recoveryInstance, recoveryPath, designPath = watchedDesign } });
            Assert.IsTrue(duplicateIntake.GetProperty("result").GetProperty("isError").GetBoolean());
            var firstIntake = await Request(4032, "tools/call", new { name = "kicad_design_intake_wait",
                arguments = new { instanceId = recoveryInstance, intakeId, afterSequence = 0 } });
            var firstIntakeState = firstIntake.GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual("Watching", firstIntakeState.GetProperty("phase").GetString());
            string replacementDesign = Path.Combine(state, "designs", "replacement.xml");
            await File.WriteAllBytesAsync(replacementDesign, [0xff], timeout.Token); File.Move(replacementDesign, watchedDesign, true);
            var intakeChange = await Request(4033, "tools/call", new { name = "kicad_design_intake_wait", arguments = new
                { instanceId = recoveryInstance, intakeId, afterSequence = firstIntakeState.GetProperty("sequence").GetUInt64() } });
            Assert.AreEqual("InvalidDesign", intakeChange.GetProperty("result").GetProperty("structuredContent").GetProperty("phase").GetString());
            CollectionAssert.AreEqual(new byte[] { 0xff }, recoveryStore.Read()!.State.DesiredFileBytes);
            Assert.IsNull(recoveryStore.Read()!.State.HierarchyResolution);
            var wrongStop = await Request(4034, "tools/call", new { name = "kicad_design_intake_stop",
                arguments = new { instanceId = Guid.NewGuid().ToString("D"), intakeId } });
            Assert.IsTrue(wrongStop.GetProperty("result").GetProperty("isError").GetBoolean());
            var stoppedIntake = await Request(4035, "tools/call", new { name = "kicad_design_intake_stop",
                arguments = new { instanceId = recoveryInstance, intakeId } });
            Assert.AreEqual("Stopped", stoppedIntake.GetProperty("result").GetProperty("structuredContent").GetProperty("phase").GetString());
            string stoppedToken = recoveryStore.Read()!.RevisionToken;
            await File.WriteAllBytesAsync(watchedDesign, recoveryFixture.DesiredFileBytes, timeout.Token);
            Assert.AreEqual(stoppedToken, recoveryStore.Read()!.RevisionToken);
            string missingDesign = Path.Combine(state, "designs", "missing.xml");
            var pausedStart = await Request(4036, "tools/call", new { name = "kicad_design_intake_start",
                arguments = new { instanceId = recoveryInstance, recoveryPath, designPath = missingDesign } });
            string pausedIntakeId = pausedStart.GetProperty("result").GetProperty("structuredContent").GetProperty("intakeId").GetString()!;
            var pausedIntake = await Request(4037, "tools/call", new { name = "kicad_design_intake_wait",
                arguments = new { instanceId = recoveryInstance, intakeId = pausedIntakeId, afterSequence = 0 } });
            var pausedState = pausedIntake.GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual("Paused", pausedState.GetProperty("phase").GetString());
            await File.WriteAllBytesAsync(missingDesign, recoveryFixture.DesiredFileBytes, timeout.Token);
            var resumedIntake = await Request(4038, "tools/call", new { name = "kicad_design_intake_resume", arguments = new
                { instanceId = recoveryInstance, intakeId = pausedIntakeId, expectedSequence = pausedState.GetProperty("sequence").GetUInt64() } });
            Assert.IsFalse(resumedIntake.GetProperty("result").TryGetProperty("isError", out var resumeError) && resumeError.GetBoolean());
            var resumedStatus = await Request(4039, "tools/call", new { name = "kicad_design_intake_wait", arguments = new
                { instanceId = recoveryInstance, intakeId = pausedIntakeId, afterSequence = pausedState.GetProperty("sequence").GetUInt64() } });
            Assert.AreEqual("Watching", resumedStatus.GetProperty("result").GetProperty("structuredContent").GetProperty("phase").GetString());
            await Request(4040, "tools/call", new { name = "kicad_design_intake_stop", arguments = new { instanceId = recoveryInstance, intakeId = pausedIntakeId } });
            var stoppedIntakes = (await Request(4063, "tools/call", new { name = "kicad_design_intake_list",
                arguments = new { instanceId = recoveryInstance } })).GetProperty("result").GetProperty("structuredContent").GetProperty("sessions");
            Assert.AreEqual(0, stoppedIntakes.GetArrayLength());
            var sheetId = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
            var screen = new SchematicScreenData { Metadata = new() { ScreenId = sheetId, Document = new() { SheetPath = new() } } };
            screen.Metadata.Document.SheetPath.Path.Add(sheetId.Clone());
            var noteItem = new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "original" } };
            screen.Items.Add(Any.Pack(noteItem));
            string baselineXml = SchematicDataXml.Write(screen);
            noteItem.Text.Text_ = "desired"; screen.Items[0] = Any.Pack(noteItem);
            string desiredXml = SchematicDataXml.Write(screen);
            noteItem.Text.Text_ = "native"; screen.Items[0] = Any.Pack(noteItem);
            string nativeXml = SchematicDataXml.Write(screen);
            var conflictCall = await Request(7, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml, desiredXml, nativeXml } });
            var conflictPlan = conflictCall.GetProperty("result").GetProperty("structuredContent");
            Assert.IsFalse(conflictPlan.GetProperty("canPlan").GetBoolean());
            Assert.AreEqual(0, conflictPlan.GetProperty("operations").GetArrayLength());
            Assert.AreEqual("competing_edit", conflictPlan.GetProperty("conflicts")[0].GetProperty("reason").GetString());
            foreach (string version in new[] { "baseline", "xml", "native" })
                Assert.AreEqual(JsonValueKind.Object, conflictPlan.GetProperty("conflicts")[0].GetProperty(version).ValueKind);
            var choiceCall = await Request(8, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml, desiredXml, nativeXml, choices = new Dictionary<string, string> { [noteItem.Id.Value] = "xml" } } });
            var choicePlan = choiceCall.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(choicePlan.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(choicePlan.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual(1, choicePlan.GetProperty("operations").GetArrayLength());
            Assert.AreEqual(SchematicDataXml.Read(desiredXml), SchematicDataXml.Read(choicePlan.GetProperty("mergedXml").GetString()!));
            var badXml = await Request(9, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = "<broken", desiredXml, nativeXml } });
            Assert.IsTrue(badXml.GetProperty("result").GetProperty("isError").GetBoolean());
            var afterBadXml = await Request(10, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml, desiredXml = baselineXml, nativeXml = baselineXml } });
            var unchangedPlan = afterBadXml.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(unchangedPlan.GetProperty("canPlan").GetBoolean());
            Assert.AreEqual(0, unchangedPlan.GetProperty("operations").GetArrayLength());
            var otherSheet = (SchematicScreenData)SchematicDataXml.Read(baselineXml);
            otherSheet.Metadata.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") });
            var wrongTarget = await Request(11, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml, desiredXml = baselineXml, nativeXml = SchematicDataXml.Write(otherSheet) } });
            var rejectedTarget = wrongTarget.GetProperty("result").GetProperty("structuredContent");
            Assert.IsFalse(rejectedTarget.GetProperty("canPlan").GetBoolean());
            Assert.AreEqual("target_changed", rejectedTarget.GetProperty("conflicts")[0].GetProperty("reason").GetString());
            Assert.AreEqual(0, rejectedTarget.GetProperty("operations").GetArrayLength());
            var newerFormat = (SchematicScreenData)SchematicDataXml.Read(baselineXml);
            newerFormat.Metadata.WriterNativeFormatVersion = SchematicItemDelta.SupportedWriterFormatVersion + 1;
            var unsupportedFormat = await Request(12, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml, desiredXml = SchematicDataXml.Write(newerFormat), nativeXml = baselineXml } });
            var rejectedFormat = unsupportedFormat.GetProperty("result");
            Assert.IsTrue(rejectedFormat.GetProperty("isError").GetBoolean());
            using var formatError = JsonDocument.Parse(rejectedFormat.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual("unsupported_native_format", formatError.RootElement.GetProperty("code").GetString());
            CollectionAssert.Contains(names, "kicad_schematic_hierarchy_validate");
            var hierarchy = SchematicHierarchyTopologyTests.Fixture();
            var hierarchyValid = await Request(13, "tools/call", new { name = "kicad_schematic_hierarchy_validate",
                arguments = new { hierarchyXml = SchematicDataXml.Write(hierarchy) } });
            Assert.IsTrue(hierarchyValid.GetProperty("result").GetProperty("structuredContent").GetProperty("topologyValid").GetBoolean());
            hierarchy.Instances.RemoveAt(1);
            var hierarchyMissing = await Request(14, "tools/call", new { name = "kicad_schematic_hierarchy_validate",
                arguments = new { hierarchyXml = SchematicDataXml.Write(hierarchy) } });
            Assert.IsFalse(hierarchyMissing.GetProperty("result").GetProperty("structuredContent").GetProperty("topologyValid").GetBoolean());
            var hierarchyBadXml = await Request(15, "tools/call", new { name = "kicad_schematic_hierarchy_validate", arguments = new { hierarchyXml = "<invalid>" } });
            Assert.IsTrue(hierarchyBadXml.GetProperty("result").GetProperty("isError").GetBoolean());
            var hierarchyRecovered = await Request(16, "tools/call", new { name = "kicad_schematic_hierarchy_validate",
                arguments = new { hierarchyXml = SchematicDataXml.Write(SchematicHierarchyTopologyTests.Fixture()) } });
            Assert.IsTrue(hierarchyRecovered.GetProperty("result").GetProperty("structuredContent").GetProperty("topologyValid").GetBoolean());

            CollectionAssert.Contains(names, "kicad_schematic_hierarchy_plan");
            var currentHierarchy = SchematicHierarchyTopologyTests.Fixture();
            var desiredHierarchy = currentHierarchy.Clone();
            foreach (var hierarchyScreen in desiredHierarchy.Instances.Skip(1))
            {
                var hierarchyNote = hierarchyScreen.Items[0].Unpack<SchematicText>();
                hierarchyNote.Text.Text_ = "MCP shared-sheet edit"; hierarchyScreen.Items[0] = Any.Pack(hierarchyNote);
            }
            string currentHierarchyXml = SchematicDataXml.Write(currentHierarchy);
            var plannedHierarchy = await Request(17, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = SchematicDataXml.Write(desiredHierarchy) } });
            var hierarchyPlan = plannedHierarchy.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(hierarchyPlan.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(hierarchyPlan.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.IsFalse(hierarchyPlan.GetProperty("completeReconstructionProven").GetBoolean());
            Assert.AreEqual(1, hierarchyPlan.GetProperty("operations").GetArrayLength());
            Assert.AreEqual(2, hierarchyPlan.GetProperty("operations")[0].GetProperty("targetDocument").GetProperty("sheetPath").GetProperty("path").GetArrayLength());
            Assert.AreEqual(2, hierarchyPlan.GetProperty("coverageGaps").GetArrayLength());
            desiredHierarchy.Instances[1].Items[0] = currentHierarchy.Instances[1].Items[0].Clone();
            var conflictHierarchy = await Request(18, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = SchematicDataXml.Write(desiredHierarchy) } });
            Assert.IsTrue(conflictHierarchy.GetProperty("result").GetProperty("isError").GetBoolean());
            var malformedHierarchy = await Request(19, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = "<invalid>" } });
            Assert.IsTrue(malformedHierarchy.GetProperty("result").GetProperty("isError").GetBoolean());
            var recoveredHierarchy = await Request(20, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = currentHierarchyXml } });
            Assert.AreEqual(0, recoveredHierarchy.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());
            var invalidLabelModel = currentHierarchy.Clone();
            invalidLabelModel.Instances[0].Items.Add(Any.Pack(new HierarchicalLabel
            {
                Id = new() { Value = Guid.NewGuid().ToString("D") },
                Text = new() { Text_ = "SIGNAL", Attributes = new() { Multiline = true } }
            }));
            var invalidLabelResult = await Request(21, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = SchematicDataXml.Write(invalidLabelModel) } });
            Assert.IsTrue(invalidLabelResult.GetProperty("result").GetProperty("isError").GetBoolean());
            using var labelError = JsonDocument.Parse(invalidLabelResult.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual("unsupported_schematic_delta", labelError.RootElement.GetProperty("code").GetString());
            var afterLabelError = await Request(22, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = currentHierarchyXml } });
            Assert.AreEqual(0, afterLabelError.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());
            var variableModel = currentHierarchy.Clone();
            foreach (var variableScreen in variableModel.Instances)
                variableScreen.Metadata.TextVariables["NOTE"] = "電源 & timing\nKeep close to CPU";
            var variablePlan = await Request(23, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = SchematicDataXml.Write(variableModel) } });
            var variableOperations = variablePlan.GetProperty("result").GetProperty("structuredContent").GetProperty("operations");
            Assert.AreEqual(1, variableOperations.GetArrayLength());
            Assert.AreEqual("電源 & timing\nKeep close to CPU", variableOperations[0].GetProperty("replaceTextVariables")
                .GetProperty("variables").GetProperty("NOTE").GetString());
            variableModel.Instances[^1].Metadata.TextVariables["NOTE"] = "Conflicting sheet copy";
            var conflictingVariables = await Request(24, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = SchematicDataXml.Write(variableModel) } });
            Assert.IsTrue(conflictingVariables.GetProperty("result").GetProperty("isError").GetBoolean());
            var afterVariableError = await Request(25, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = currentHierarchyXml, desiredXml = currentHierarchyXml } });
            Assert.AreEqual(0, afterVariableError.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());
            var chainModel = currentHierarchy.Clone();
            foreach (var chainScreen in chainModel.Instances)
                chainScreen.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH",
                    From = new() { Reference = "U1", Pin = "1" }, To = new() { Reference = "U2", Pin = "2" },
                    MemberNets = { "/SIGNAL" } });
            string chainXml = SchematicDataXml.Write(chainModel);
            var chainNoop = await Request(26, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = chainXml, desiredXml = chainXml } });
            Assert.AreEqual(0, chainNoop.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());
            foreach (var chainScreen in chainModel.Instances) chainScreen.Metadata.NetChains[0].NetClass = "Changed";
            var chainEdit = await Request(27, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = chainXml, desiredXml = SchematicDataXml.Write(chainModel) } });
            var chainOperations = chainEdit.GetProperty("result").GetProperty("structuredContent").GetProperty("operations");
            Assert.AreEqual(1, chainOperations.GetArrayLength());
            Assert.AreEqual("Changed", chainOperations[0].GetProperty("replaceNetChains").GetProperty("definitions")[0]
                .GetProperty("netClass").GetString());
            foreach (var chainScreen in chainModel.Instances) chainScreen.Metadata.NetChains[0].Committed = true;
            var chainInvalid = await Request(28, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = chainXml, desiredXml = SchematicDataXml.Write(chainModel) } });
            Assert.IsTrue(chainInvalid.GetProperty("result").GetProperty("isError").GetBoolean());
            var afterChainError = await Request(29, "tools/call", new { name = "kicad_schematic_hierarchy_plan",
                arguments = new { currentXml = chainXml, desiredXml = chainXml } });
            Assert.AreEqual(0, afterChainError.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());

            var chainBaseline = (SchematicScreenData)SchematicDataXml.Read(baselineXml);
            chainBaseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Original" });
            var chainDesired = chainBaseline.Clone(); chainDesired.Metadata.NetChains[0].NetClass = "Xml";
            var chainNative = chainBaseline.Clone(); chainNative.Metadata.NetChains[0].NetClass = "Native";
            var chainResolution = await Request(30, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(chainBaseline), desiredXml = SchematicDataXml.Write(chainDesired),
                    nativeXml = SchematicDataXml.Write(chainNative), netChainChoices = new Dictionary<string, string> { ["PATH"] = "xml" } } });
            var resolvedChainPlan = chainResolution.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(resolvedChainPlan.GetProperty("canPlan").GetBoolean());
            var resolvedChain = (SchematicScreenData)SchematicDataXml.Read(resolvedChainPlan.GetProperty("mergedXml").GetString()!);
            Assert.AreEqual("Xml", resolvedChain.Metadata.NetChains.Single().NetClass);
            var badChainChoice = await Request(31, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(chainBaseline), desiredXml = SchematicDataXml.Write(chainDesired),
                    nativeXml = SchematicDataXml.Write(chainNative), netChainChoices = new Dictionary<string, string> { ["UNKNOWN"] = "xml" } } });
            Assert.IsTrue(badChainChoice.GetProperty("result").GetProperty("isError").GetBoolean());
            var unresolvedChain = await Request(32, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(chainBaseline), desiredXml = SchematicDataXml.Write(chainDesired),
                    nativeXml = SchematicDataXml.Write(chainNative) } });
            Assert.IsFalse(unresolvedChain.GetProperty("result").GetProperty("structuredContent").GetProperty("canPlan").GetBoolean());

            var variantBaseline = (SchematicScreenData)SchematicDataXml.Read(baselineXml);
            variantBaseline.Metadata.VariantDescriptions.Add("Assembly", "original");
            var variantXml = variantBaseline.Clone(); variantXml.Metadata.VariantDescriptions["Assembly"] = "XML";
            var variantNative = variantBaseline.Clone(); variantNative.Metadata.VariantDescriptions["Assembly"] = "native";
            var chosenVariant = await Request(33, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(variantBaseline), desiredXml = SchematicDataXml.Write(variantXml),
                    nativeXml = SchematicDataXml.Write(variantNative), variantChoices = new Dictionary<string, string> { ["Assembly"] = "xml" } } });
            var variantPlan = chosenVariant.GetProperty("result").GetProperty("structuredContent");
            Assert.IsTrue(variantPlan.GetProperty("canPlan").GetBoolean());
            Assert.IsFalse(variantPlan.GetProperty("liveMutationAuthorized").GetBoolean());
            Assert.AreEqual("XML", ((SchematicScreenData)SchematicDataXml.Read(variantPlan.GetProperty("mergedXml").GetString()!))
                .Metadata.VariantDescriptions["Assembly"]);
            var wrongVariant = await Request(34, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(variantBaseline), desiredXml = SchematicDataXml.Write(variantXml),
                    nativeXml = SchematicDataXml.Write(variantNative), variantChoices = new Dictionary<string, string> { ["assembly"] = "xml" } } });
            Assert.IsTrue(wrongVariant.GetProperty("result").GetProperty("isError").GetBoolean());
            var recoveredVariant = await Request(35, "tools/call", new { name = "kicad_schematic_xml_plan",
                arguments = new { baselineXml = SchematicDataXml.Write(variantBaseline), desiredXml = SchematicDataXml.Write(variantXml),
                    nativeXml = SchematicDataXml.Write(variantNative), variantChoices = new Dictionary<string, string> { ["Assembly"] = "native" } } });
            Assert.AreEqual(0, recoveredVariant.GetProperty("result").GetProperty("structuredContent").GetProperty("operations").GetArrayLength());

            CollectionAssert.Contains(names, "kicad_instance_pending_launches");
            string launchId = Guid.NewGuid().ToString("D");
            Directory.CreateDirectory(Path.Combine(state, "launches"));
            await File.WriteAllTextAsync(Path.Combine(state, "launches", launchId + ".json"),
                JsonSerializer.Serialize(new UnverifiedInstanceLaunch(launchId, Path.Combine(state, "interrupted.kicad_pro"),
                    NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(launchId), "api.sock")),
                    null, DateTimeOffset.UtcNow)), timeout.Token);
            var pendingLaunches = await Request(36, "tools/call", new { name = "kicad_instance_pending_launches", arguments = new { } });
            var launches = JsonSerializer.Deserialize<UnverifiedInstanceLaunch[]>(pendingLaunches.GetProperty("result")
                .GetProperty("content")[0].GetProperty("text").GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.AreEqual(1, launches.Length);
            Assert.AreEqual(launchId, launches[0].InstanceId);
            Assert.IsNull(launches[0].ProcessId);
            var verifiedList = await Request(37, "tools/call", new { name = "kicad_instances_list", arguments = new { } });
            using var verified = JsonDocument.Parse(verifiedList.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(0, verified.RootElement.GetArrayLength());
            CollectionAssert.Contains(names, "kicad_instance_saved_sessions");
            string savedId = Guid.NewGuid().ToString("D");
            await File.WriteAllTextAsync(Path.Combine(state, savedId + ".json"),
                JsonSerializer.Serialize(new InstanceRecord(savedId, Path.Combine(state, "saved.kicad_pro"),
                    NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(savedId), "api.sock")),
                    "historical-epoch", null, DateTimeOffset.UtcNow)), timeout.Token);
            var savedList = await Request(38, "tools/call", new { name = "kicad_instance_saved_sessions", arguments = new { } });
            var savedViews = JsonSerializer.Deserialize<InstanceView[]>(savedList.GetProperty("result")
                .GetProperty("content")[0].GetProperty("text").GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.AreEqual(savedId, savedViews.Single().InstanceId);
            var stillDetached = await Request(39, "tools/call", new { name = "kicad_instances_list", arguments = new { } });
            using var detached = JsonDocument.Parse(stillDetached.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(0, detached.RootElement.GetArrayLength());

            var eventTool = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == "kicad_events_wait");
            Assert.AreEqual(JsonValueKind.Object, eventTool.GetProperty("outputSchema").ValueKind);
            foreach (var eventCase in new (int Id, object Arguments, string Code)[]
            {
                (40, new { instanceId = savedId }, "unknown_instance"),
                (41, new { instanceId = savedId, timeoutSeconds = 0 }, "invalid_deadline"),
                (42, new { instanceId = savedId, afterSequence = 0UL }, "missing_event_epoch")
            })
            {
                var rejectedEvent = (await Request(eventCase.Id, "tools/call",
                    new { name = "kicad_events_wait", arguments = eventCase.Arguments })).GetProperty("result");
                Assert.IsTrue(rejectedEvent.GetProperty("isError").GetBoolean());
                var failure = rejectedEvent.GetProperty("structuredContent");
                Assert.AreEqual("Failed", failure.GetProperty("status").GetString());
                Assert.AreEqual(eventCase.Code, failure.GetProperty("errorCode").GetString());
                Assert.IsFalse(string.IsNullOrWhiteSpace(failure.GetProperty("errorMessage").GetString()));
                Assert.AreEqual(JsonValueKind.Null, failure.GetProperty("notification").ValueKind);
            }
            var afterEventErrors = await Request(43, "tools/call", new { name = "kicad_instances_list", arguments = new { } });
            using var stillUsable = JsonDocument.Parse(afterEventErrors.GetProperty("result")
                .GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(0, stillUsable.RootElement.GetArrayLength());

            // Leave one watcher and one paused worker owned by the host at EOF.
            // A clean exit must dispose both without requiring another tool call.
            var shutdownWatching = (await Request(4050, "tools/call", new { name = "kicad_design_intake_start",
                arguments = new { instanceId = recoveryInstance, recoveryPath, designPath = watchedDesign } }))
                .GetProperty("result").GetProperty("structuredContent");
            var shutdownWatchingState = (await Request(4051, "tools/call", new { name = "kicad_design_intake_wait",
                arguments = new { instanceId = recoveryInstance, intakeId = shutdownWatching.GetProperty("intakeId").GetString(), afterSequence = 0 } }))
                .GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual("Watching", shutdownWatchingState.GetProperty("phase").GetString());
            string pausedRecoveryPath = Path.Combine(state, "designs", "shutdown-recovery.json");
            var pausedRecoveryStore = new DesignRecoveryStore(pausedRecoveryPath);
            var pausedRecovery = pausedRecoveryStore.Save(recoveryFixture, null);
            var shutdownPaused = (await Request(4052, "tools/call", new { name = "kicad_design_intake_start",
                arguments = new { instanceId = recoveryInstance, recoveryPath = pausedRecoveryPath,
                    designPath = Path.Combine(state, "designs", "shutdown-missing.xml") } }))
                .GetProperty("result").GetProperty("structuredContent");
            var shutdownPausedState = (await Request(4053, "tools/call", new { name = "kicad_design_intake_wait",
                arguments = new { instanceId = recoveryInstance, intakeId = shutdownPaused.GetProperty("intakeId").GetString(), afterSequence = 0 } }))
                .GetProperty("result").GetProperty("structuredContent");
            Assert.AreEqual("Paused", shutdownPausedState.GetProperty("phase").GetString());
            string shutdownRecoveryToken = recoveryStore.Read()!.RevisionToken;
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await diagnostics);
            Assert.AreEqual(shutdownRecoveryToken, recoveryStore.Read()!.RevisionToken);
            Assert.AreEqual(pausedRecovery.RevisionToken, pausedRecoveryStore.Read()!.RevisionToken);
        }
        finally
        {
            // Only this test-owned MCP process is disposable, never a KiCad editor.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            Directory.Delete(state, true);
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "MCP terminated before replying.");
                using JsonDocument response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }

    private static GuidanceToolResult ReadGuidance(JsonElement message) =>
        JsonSerializer.Deserialize<GuidanceToolResult>(message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            })!;

    private static string FindAutomationRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "KiCad.Automation.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run this source test from the automation checkout.");
    }
}
