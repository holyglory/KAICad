using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Protocol = KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

// Real compiled STDIO process. This does not claim Codex Desktop or editor UI coverage.
[TestClass]
public sealed class McpProcessTests
{
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
            CollectionAssert.Contains(names, "kicad_service_capabilities");
            CollectionAssert.Contains(names, "kicad_pcb_drc_start");
            CollectionAssert.Contains(names, "kicad_pcb_drc_job");
            CollectionAssert.Contains(names, "kicad_pcb_drc_cancel");
            CollectionAssert.Contains(names, "kicad_diagram_physical_allocation");
            CollectionAssert.Contains(names, "kicad_diagram_physical_allocation_set");
            CollectionAssert.Contains(names, "kicad_simulation_start");
            CollectionAssert.Contains(names, "kicad_simulation_job");
            CollectionAssert.Contains(names, "kicad_simulation_wait");
            CollectionAssert.Contains(names, "kicad_simulation_cancel");
            // Deck admission runs before any instance lookup, so a script deck is refused
            // even when no native editor could ever receive it.
            var scriptDeck = await Request(9089, "tools/call", new { name = "kicad_simulation_start", arguments = new
            {
                instanceId = Guid.NewGuid().ToString("D"), expectedInstanceEpoch = Guid.NewGuid().ToString("D"), document = new { },
                netlist = "Divider\nR1 in 0 1k\n.control\nshell touch pwned\n.endc\n.end\n", operationId = Guid.NewGuid()
            } });
            Assert.IsTrue(scriptDeck.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.AreEqual("simulation_deck_rejected", scriptDeck.GetProperty("result").GetProperty("structuredContent").GetProperty("errorCode").GetString());
            CollectionAssert.Contains(names, "kicad_pcb_items_read");
            CollectionAssert.Contains(names, "kicad_pcb_items_create");
            CollectionAssert.Contains(names, "kicad_pcb_items_update");
            CollectionAssert.Contains(names, "kicad_pcb_guide_create");
            CollectionAssert.Contains(names, "kicad_pcb_guide_svg_create");
            CollectionAssert.Contains(names, "kicad_pcb_route_candidate_from_guide");
            CollectionAssert.Contains(names, "kicad_pcb_route_candidate_validate");
            CollectionAssert.Contains(names, "kicad_pcb_render_3d");
            CollectionAssert.Contains(names, "kicad_pcb_route_geometry");
            CollectionAssert.Contains(names, "kicad_pcb_route_preview");
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
            // A checked object batch that is unreadable, carries an object type this server does not know, or names no
            // attached KiCad is refused before anything is sent, and the agent is told why. The LocalLabel written the way
            // the tool description shows is read, so its refusal comes from the instance lookup; the same batch with an
            // unknown @type is refused as unreadable and names that type. Checks against an observed state need a real
            // editor: NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo drives the same tools there.
            string checkedBatch = SchematicJson.Formatter.Format(new Protocol.CheckedSchematicBatch
            {
                Batch = new() { OperationId = Guid.NewGuid().ToString("D"), Operations = { new Protocol.SchematicItemOperation
                    { Create = Any.Pack(new LocalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") } }) } } }
            });
            string unknownType = checkedBatch.Replace("kiapi.schematic.types.LocalLabel", "kiapi.schematic.types.NoSuchObject", StringComparison.Ordinal);
            Assert.AreNotEqual(checkedBatch, unknownType, "The batch carries the typed LocalLabel.");
            foreach (var (requestId, tool, requestJson, code, detail) in new[]
            {
                (9090, "kicad_schematic_apply_checked_batch", "{\"batch\":", "invalid_checked_batch", ""),
                (9091, "kicad_schematic_apply_checked_batch", checkedBatch, "unknown_instance", ""),
                (9092, "kicad_schematic_checked_batch_receipt", checkedBatch, "unknown_instance", ""),
                (9093, "kicad_schematic_apply_checked_batch", unknownType, "invalid_checked_batch", "kiapi.schematic.types.NoSuchObject")
            })
            {
                var refused = (await Request(requestId, "tools/call", new { name = tool,
                    arguments = new { instanceId = Guid.NewGuid().ToString("D"), requestJson } })).GetProperty("result");
                Assert.IsTrue(refused.GetProperty("isError").GetBoolean(), refused.GetRawText());
                var refusal = refused.GetProperty("structuredContent");
                Assert.AreEqual(code, refusal.GetProperty("errorCode").GetString(), refused.GetRawText());
                Assert.IsFalse(refusal.GetProperty("mutationSubmitted").GetBoolean(), refused.GetRawText());
                Assert.AreEqual("not_submitted", refusal.GetProperty("outcome").GetString(), refused.GetRawText());
                StringAssert.Contains(refusal.GetProperty("errorMessage").GetString(), detail, refused.GetRawText());
            }
            StringAssert.Contains(listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "kicad_schematic_apply_checked_batch").GetProperty("description").GetString(),
                "\"@type\":\"type.googleapis.com/kiapi.schematic.types.LocalLabel\"", "The tool shows an agent how to write a typed object.");
            CollectionAssert.Contains(names, "kicad_document_operation");
            CollectionAssert.Contains(names, "kicad_pcb_drc_state");
            CollectionAssert.Contains(names, "kicad_pcb_drc_start");
            CollectionAssert.Contains(names, "kicad_pcb_drc_job");
            CollectionAssert.Contains(names, "kicad_pcb_drc_cancel");
            CollectionAssert.Contains(names, "kicad_document_close");
            CollectionAssert.Contains(names, "kicad_schematic_preview");
            CollectionAssert.Contains(names, "kicad_schematic_electrical_state");
            CollectionAssert.Contains(names, "kicad_schematic_measure_placement");
            // Flat structural diagrams are discarded, not converted (decision n9af098253fec71da): no flat
            // editor tool and no conversion tool is advertised; the per-level diagram tools remain.
            CollectionAssert.DoesNotContain(names, "kicad_structure_open");
            CollectionAssert.DoesNotContain(names, "kicad_structure_state");
            CollectionAssert.DoesNotContain(names, "kicad_diagram_migrate");
            Assert.IsFalse(names.Any(name => name.StartsWith("kicad_structure_", StringComparison.Ordinal)), string.Join(", ", names));
            CollectionAssert.Contains(names, "kicad_diagram_open");
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
            CollectionAssert.Contains(names, "kicad_pcb_route_candidate_from_guide");
            CollectionAssert.Contains(names, "kicad_pcb_route_candidate_validate");
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
            CollectionAssert.Contains(names, "kicad_design_recovery_release_exited");
            // A synchronization left pending in a recovery record is released only on a proven exit of exactly the KiCad
            // process epoch that holds it. With no attached KiCad, the only proof is a saved registration of that epoch:
            // written on this machine in an earlier boot it proves the exit; written on another computer (a different
            // machine ID, whose boot always differs) it proves nothing. The release keeps the whole operation in a receipt
            // and clears only the pending operation. The native journey NativeSessionTests.NativeCrashReleasesTheExitedOperation
            // proves release, resume and roll-back against killed KiCad processes.
            using (var released = new DesignPublicationRecoveryTests.Fixture())
            {
                var held = released.Saved;
                string heldInstance = held.State.InstanceId.ToString("D"), heldEpoch = held.State.PendingNativeState!.ProcessEpoch;
                string heldOperation = held.State.PendingPublication!.OperationId.ToString("D");
                string nativeFile = held.State.PendingNativeSave!.ExpectedState.FileBaselines.Single().Path;
                await File.WriteAllTextAsync(nativeFile, "(kicad_sch (version 20250114))", timeout.Token);
                async Task<JsonElement> Release(int id, string token, string operation, string continuation) =>
                    (await Request(id, "tools/call", new { name = "kicad_design_recovery_release_exited", arguments = new
                        { instanceId = heldInstance, recoveryPath = released.RecordPath, expectedRevisionToken = token, operationId = operation, continuation } }))
                    .GetProperty("result");
                string Code(JsonElement result) => result.GetProperty("structuredContent").GetProperty("errorCode").GetString()!;
                byte[] heldBytes = await File.ReadAllBytesAsync(released.RecordPath, timeout.Token);
                Assert.AreEqual("invalid_continuation", Code(await Release(4090, held.RevisionToken, heldOperation, "discard")));
                Assert.AreEqual("released_operation_mismatch", Code(await Release(4091, held.RevisionToken, Guid.NewGuid().ToString("D"), "resume")));
                var unproven = await Release(4092, held.RevisionToken, heldOperation, "resume");
                Assert.AreEqual("operation_exit_unproven", Code(unproven), unproven.GetRawText());
                StringAssert.Contains(unproven.GetProperty("structuredContent").GetProperty("errorMessage").GetString(), heldEpoch);
                var live = ProcessIdentity.Record(Environment.ProcessId)!;
                async Task Registration(ProcessStartIdentity identity, string? registry = null, string? epoch = null) => await File.WriteAllTextAsync(
                    Path.Combine(registry ?? state, heldInstance + ".json"),
                    JsonSerializer.Serialize(new InstanceRecord(heldInstance, Path.Combine(state, "released-project", "fixture.kicad_pro"),
                        NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(heldInstance), "api.sock")), epoch ?? heldEpoch,
                        Environment.ProcessId, DateTimeOffset.UtcNow, identity)), timeout.Token);
                await Registration(live with { MachineId = "0123456789abcdef0123456789abcdef", BootId = Guid.NewGuid().ToString("D") });
                Assert.AreEqual("operation_exit_unproven", Code(await Release(4093, held.RevisionToken, heldOperation, "resume")),
                    "A registration written on another computer never proves the exit.");
                // The recovery store takes only an exit an instance registry proved (a ProvenInstanceExit, which nothing else
                // creates), and only the exit of exactly the process epoch that holds the operation: here a registry proves
                // that another epoch of the instance ended on this machine (its registration names an earlier boot).
                string otherRegistry = Directory.CreateDirectory(Path.Combine(state, "other-epoch-registry")).FullName;
                string otherEpoch = Guid.NewGuid().ToString("D");
                await Registration(live with { BootId = Guid.NewGuid().ToString("D") }, otherRegistry, otherEpoch);
                var otherProcess = await ProvenInstanceExit.ProveAsync(new InstanceRegistry(new NngTransport(), otherRegistry), heldInstance, otherEpoch,
                    timeout.Token) ?? throw new AssertFailedException("The registry proves the other epoch's exit.");
                Assert.AreEqual(otherEpoch, otherProcess.Exit.Epoch);
                Assert.AreEqual("operation_exit_unproven", Assert.ThrowsExactly<AutomationException>(() =>
                    new DesignRecoveryStore(released.RecordPath).ReleaseExitedOperation(held, otherProcess)).Code);
                Directory.Delete(otherRegistry, true);
                CollectionAssert.AreEqual(heldBytes, await File.ReadAllBytesAsync(released.RecordPath, timeout.Token), "A refused release changes nothing.");
                Assert.IsFalse(Directory.Exists(DesignReleasedOperations.Directory(released.RecordPath)), "A refused release keeps no receipt.");

                await Registration(live with { BootId = Guid.NewGuid().ToString("D") });
                var release = await Release(4094, held.RevisionToken, heldOperation, "resume");
                Assert.IsFalse(release.TryGetProperty("isError", out var releaseError) && releaseError.GetBoolean(), release.GetRawText());
                var view = release.GetProperty("structuredContent");
                Assert.AreEqual("released", view.GetProperty("outcome").GetString(), "No KiCad runs for the instance, so nothing continues yet.");
                Assert.IsTrue(view.GetProperty("releasedNow").GetBoolean());
                Assert.AreEqual(heldEpoch, view.GetProperty("releasedEpoch").GetString());
                Assert.AreEqual(InstanceExit.ProcessAbsentEvidence, view.GetProperty("exit").GetProperty("evidence").GetString());
                CollectionAssert.AreEqual(new[] { nativeFile }, view.GetProperty("replacedFiles").EnumerateArray().Select(f => f.GetString()).ToArray());
                var after = new DesignRecoveryStore(released.RecordPath).Read()!;
                Assert.AreEqual(view.GetProperty("recoveryRevisionToken").GetString(), after.RevisionToken);
                Assert.IsFalse(after.State.HasPendingWork);
                Assert.IsNull(after.State.PendingNativeState);
                Assert.IsNull(after.State.PendingNativeSave);
                Assert.AreEqual(SchematicDesignXml.Write(held.State.Baseline, held.State.KnowledgeLibraries),
                    SchematicDesignXml.Write(after.State.Baseline, after.State.KnowledgeLibraries), "The release never advances the baseline.");
                CollectionAssert.AreEqual(held.State.DesiredFileBytes, after.State.DesiredFileBytes);
                Assert.AreEqual(held.State.NativeRevision, after.State.NativeRevision);
                var receipt = DesignReleasedOperations.Read(view.GetProperty("receiptPath").GetString()!);
                Assert.AreEqual(held.State.PendingPublication.OperationId, receipt.OperationId);
                Assert.AreEqual(held.RevisionToken, receipt.ReleasedFromRevisionToken);
                Assert.AreEqual(held.State.PendingNativeState, receipt.NativeState(), "The receipt keeps the operation's native state.");
                Assert.AreEqual(held.State.PendingNativeSave, receipt.NativeSave(), "The receipt keeps the save KiCad was cut off in.");
                CollectionAssert.AreEqual(held.State.PendingPublication.CandidateFileBytes, receipt.PendingPublication!.CandidateFileBytes);
                CollectionAssert.AreEqual(held.State.PendingPublication.ExpectedFileBytes, receipt.PendingPublication.ExpectedFileBytes);
                Assert.IsTrue(receipt.Files.Single().Replaced);
                Assert.AreEqual(heldEpoch, receipt.Exit.Epoch);
                // Called again after the release: nothing is released twice, and it still waits for a running KiCad.
                var again = (await Release(4095, after.RevisionToken, heldOperation, "roll-back")).GetProperty("structuredContent");
                Assert.AreEqual("released", again.GetProperty("outcome").GetString(), again.GetRawText());
                Assert.IsFalse(again.GetProperty("releasedNow").GetBoolean());
                Assert.AreEqual(after.RevisionToken, new DesignRecoveryStore(released.RecordPath).Read()!.RevisionToken);
                // Until the operation is continued the record stays on the ended KiCad's document session: a save that attaches
                // it to another session, as kicad_design_recovery_reattach does, is refused, since the KiCad started again may
                // hold part of the operation's result. The native journey shows the reattach tool refusing it. Only the
                // continuation that ExitedOperationRelease journals moves the record, once; ordinary saves work again after it.
                var store = new DesignRecoveryStore(released.RecordPath);
                DesignRecoveryState Session(DesignRecoveryState recovery) => recovery with
                    { NativeRevision = new(Guid.NewGuid().ToString("D"), 1), ObservedElectrical = null, HierarchyResolution = null };
                var moved = Session(after.State);
                var refusedReattach = Assert.ThrowsExactly<AutomationException>(() => store.Save(moved, after.RevisionToken));
                Assert.AreEqual("released_operation_requires_continuation", refusedReattach.Code);
                StringAssert.Contains(refusedReattach.Message, heldOperation);
                StringAssert.Contains(refusedReattach.Message, "kicad_design_recovery_release_exited");
                Assert.AreEqual(after.RevisionToken, store.Read()!.RevisionToken, "A refused reattachment changes nothing.");
                var continued = store.ContinueReleasedOperation(moved, after.RevisionToken, receipt);
                Assert.AreEqual(moved.NativeRevision, continued.State.NativeRevision);
                Assert.AreEqual("released_operation_not_open", Assert.ThrowsExactly<AutomationException>(() =>
                    store.ContinueReleasedOperation(Session(continued.State), continued.RevisionToken, receipt)).Code, "An operation is continued once.");
                Assert.AreNotEqual(continued.RevisionToken, store.Save(Session(continued.State), continued.RevisionToken).RevisionToken,
                    "Once continued, the record can be attached like any other.");
                File.Delete(Path.Combine(state, heldInstance + ".json"));
            }
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

    // Decision n39ac0ccc5c9270f2 / ledger pbfcccd896f17cf27 through the compiled server. kicad_design_sync_plan
    // classifies a saved connection-only revision with the handshake this server recorded when it attached the
    // instance and never contacts KiCad for it; with no attached instance it is today's plan; reattaching refreshes
    // the record. The automatic worker and apply take their own live handshakes and classify the same revision the
    // same way. No KiCad build advertises schematic.connection-realization.v1 yet (CN-1 §8.3), so a scripted editor
    // on the real NNG transport stands in for one; NativeSessionTests (VerifyRecordedHandshakePlanning) proves the
    // unadvertised case against a real KiCad. With the capability the planner plans the connection's realization
    // (CN-1 §4.3), and the scripted editor refuses the realization's measurement, so the worker and apply must both
    // stop with that measurement's code (CN-1 §13) after sending the editor the same requests.
    [TestMethod]
    public async Task SyncPreviewClassifiesWithTheAttachedInstancesHandshakeOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("kicad-mcp-handshake-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var state = ConnectionOnlyRevision(project);
            string instanceId = state.InstanceId.ToString("D");
            using var editor = new ScriptedNngEditor(root, state, Path.Combine(project, "fixture.kicad_pro"));
            string handshake = Protocol.GetAutomationSession.Descriptor.FullName, capture = Protocol.ReadCheckedSchematicState.Descriptor.FullName,
                observe = Protocol.ReadSchematicElectricalState.Descriptor.FullName, measure = Protocol.MeasureSchematicPlacement.Descriptor.FullName;
            var advertising = editor.Session(advertises: true);
            Assert.AreEqual(SchematicConnectedAdditionKind.Admitted,
                SchematicConnectedAddition.Classify(state, DesignRecoveryStore.ReadDesired(state), advertising).Kind,
                "The saved revision only adds a connection over drawn pins.");
            var today = SchematicSynchronizationPlanner.Plan(state);
            var realizing = SchematicSynchronizationPlanner.Plan(state, advertising);
            Assert.IsTrue(today.CanPrepare, today.ErrorMessage);
            Assert.IsFalse(today.NativeConnectionRealizationRequired);
            Assert.IsTrue(realizing.CanPrepare, realizing.ErrorCode + ": " + realizing.ErrorMessage);
            Assert.IsTrue(realizing.NativeConnectionRealizationRequired,
                "With the capability an admitted connection-only revision plans a native realization (CN-1 §4.3): " + realizing.ErrorMessage);

            int records = 0;
            (string Recovery, string Design, string Token) Record()
            {
                string folder = Directory.CreateDirectory(Path.Combine(root, "records", (++records).ToString())).FullName;
                string recovery = Path.Combine(folder, "recovery.json"), design = Path.Combine(folder, "design.xml");
                File.WriteAllBytes(design, state.DesiredFileBytes);
                return (recovery, design, new DesignRecoveryStore(recovery).Save(state, null).RevisionToken);
            }
            await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(root, "mcp-state"), Path.Combine(root, "mcp.stderr.log"), timeout.Token);
            var preview = Record();
            async Task<JsonElement> Preview() => (await mcp.Tool("kicad_design_sync_plan", new { instanceId,
                recoveryPath = preview.Recovery, expectedRevisionToken = preview.Token })).GetProperty("structuredContent");
            // The worker discovers the editor, refreshes its observation, plans with its own handshake and applies.
            async Task<(string? Code, string[] Requests, DesignRecoveryState Record)> Worker()
            {
                var target = Record(); int from = editor.Requests.Length;
                var started = await mcp.Tool("kicad_design_automatic_sync_start", new { instanceId, recoveryPath = target.Recovery,
                    designPath = target.Design, expectedRecoveryRevision = target.Token });
                Assert.IsFalse(started.TryGetProperty("isError", out var failed) && failed.GetBoolean(), started.GetRawText());
                string sessionId = started.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
                var status = started.GetProperty("structuredContent").GetProperty("status");
                while (status.GetProperty("phase").GetString() is not ("Paused" or "Watching" or "Stopped" or "InvalidDesign"))
                    status = (await mcp.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId,
                        afterSequence = status.GetProperty("sequence").GetUInt64() })).GetProperty("structuredContent").GetProperty("status");
                var stopped = await mcp.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId });
                Assert.AreEqual("Stopped", stopped.GetProperty("structuredContent").GetProperty("status").GetProperty("phase").GetString(), stopped.GetRawText());
                Assert.AreEqual("Paused", status.GetProperty("phase").GetString(), status.GetRawText());
                return (status.GetProperty("errorCode").GetString(), editor.Requests[from..], new DesignRecoveryStore(target.Recovery).Read()!.State);
            }
            async Task<(string? Code, string[] Requests, DesignRecoveryState Record)> Apply()
            {
                var target = Record(); int from = editor.Requests.Length;
                var result = await mcp.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = target.Recovery,
                    designPath = target.Design, expectedRevisionToken = target.Token, operationId = Guid.NewGuid().ToString("D") });
                Assert.IsTrue(result.GetProperty("isError").GetBoolean(), "The scripted editor cannot complete an application: " + result.GetRawText());
                return (result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), editor.Requests[from..],
                    new DesignRecoveryStore(target.Recovery).Read()!.State);
            }
            string[] workerStart = [handshake, capture, handshake, observe];

            // Without an attached instance the preview is today's plan, and nothing contacts the editor.
            var unattached = await Preview();
            RequirePreviewPlan(today, unattached);
            Assert.IsEmpty(editor.Requests);

            // Attached to an editor that advertises realization, the preview classifies with the recorded handshake.
            editor.Advertises = true;
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = editor.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachFailed) && attachFailed.GetBoolean(), attached.GetRawText());
            CollectionAssert.AreEqual(new[] { handshake }, editor.Requests, "Attaching reads one handshake.");
            var recorded = await Preview();
            RequirePreviewPlan(realizing, recorded);
            Assert.AreNotEqual(unattached.GetRawText(), recorded.GetRawText(), "The recorded handshake decides the classification.");
            Assert.HasCount(1, editor.Requests, "The preview never contacts KiCad.");

            // The worker and apply, each with its own advertising handshake, classify the revision the same way.
            var worker = await Worker();
            var apply = await Apply();
            CollectionAssert.AreEqual(workerStart, worker.Requests.Take(4).ToArray(), string.Join(", ", worker.Requests));
            // Both hand the realization to its measurement of the captured checkpoint (CN-1 §9.1). The scripted editor
            // refuses the measurement, so both stop with the same code and nothing is journaled, drawn or saved.
            CollectionAssert.AreEqual(apply.Requests, worker.Requests[4..], "The worker's application sends exactly apply's requests: " + string.Join(", ", worker.Requests));
            CollectionAssert.AreEqual(new[] { handshake, capture }, apply.Requests.Take(2).ToArray(), string.Join(", ", apply.Requests));
            Assert.IsGreaterThan(2, apply.Requests.Length, "After the capture the realization measures the checkpoint: " + string.Join(", ", apply.Requests));
            Assert.IsTrue(apply.Requests.Skip(2).All(r => r == measure),
                "After the capture the realization only measures the checkpoint: " + string.Join(", ", apply.Requests));
            Assert.AreEqual(SchematicConnectionErrors.RealizationMeasurementUnsupported, apply.Code, "Apply stops at the refused measurement.");
            Assert.AreEqual(apply.Code, worker.Code, "The worker pauses with apply's code.");
            Assert.IsFalse(worker.Record.HasPendingWork, "Nothing is journaled before the editor has been measured.");
            Assert.IsFalse(apply.Record.HasPendingWork, "Nothing is journaled before the editor has been measured.");
            Console.WriteLine($"Advertised handshake: the planner plans a native realization; "
                + $"worker {worker.Code} after [{string.Join(", ", worker.Requests)}]; apply {apply.Code} after [{string.Join(", ", apply.Requests)}].");

            // The preview reads the record made at attach, not the live editor: after the editor stops advertising the
            // preview is unchanged until the instance is reattached, which refreshes the record.
            editor.Advertises = false;
            int beforeReattach = editor.Requests.Length;
            Assert.AreEqual(recorded.GetRawText(), (await Preview()).GetRawText(), "Only attachment records a handshake.");
            Assert.HasCount(beforeReattach, editor.Requests, "The preview never contacts KiCad.");
            var reattached = await mcp.Tool("kicad_instance_reattach", new { instanceId });
            Assert.IsFalse(reattached.TryGetProperty("isError", out var reattachFailed) && reattachFailed.GetBoolean(), reattached.GetRawText());
            Assert.AreEqual(unattached.GetRawText(), (await Preview()).GetRawText(),
                "Reattaching refreshes the recorded handshake; without the capability the preview is today's plan.");

            // Without the capability the worker and apply take today's general path: both journal the XML publication
            // and refuse it, because the editor does not show the new connection.
            worker = await Worker();
            apply = await Apply();
            CollectionAssert.AreEqual(workerStart, worker.Requests.Take(4).ToArray(), string.Join(", ", worker.Requests));
            CollectionAssert.AreEqual(new[] { handshake, capture, capture }, apply.Requests, string.Join(", ", apply.Requests));
            CollectionAssert.AreEqual(apply.Requests, worker.Requests[4..], "The worker's application sends exactly apply's requests: " + string.Join(", ", worker.Requests));
            foreach (var (name, result) in new[] { ("worker", worker), ("apply", apply) })
            {
                Assert.AreEqual("native_sync_connectivity_mismatch", result.Code, name);
                Assert.IsNotNull(result.Record.PendingPublication, $"The {name} journals the general path's XML publication.");
                Assert.IsNull(result.Record.PendingLayout, $"The {name} must not plan a connection realization.");
                Assert.IsNull(result.Record.PendingMutation, $"The {name} has no native batch to send.");
            }
            Console.WriteLine($"Unadvertised handshake after reattach: worker {worker.Code} after [{string.Join(", ", worker.Requests)}]; "
                + $"apply {apply.Code} after [{string.Join(", ", apply.Requests)}].");
            CollectionAssert.DoesNotContain(editor.Requests, Protocol.CheckedSchematicBatch.Descriptor.FullName, "Nothing is drawn.");
            CollectionAssert.DoesNotContain(editor.Requests, Protocol.CheckedSaveDocument.Descriptor.FullName, "Nothing is saved.");
            Assert.AreEqual(preview.Token, new DesignRecoveryStore(preview.Recovery).Read()!.RevisionToken, "The preview writes nothing.");
        }
        finally { Directory.Delete(root, true); }
    }

    // The public preview reports exactly the planner's own plan for the same record and handshake.
    private static void RequirePreviewPlan(SchematicSynchronizationPlan expected, JsonElement preview)
    {
        Assert.AreEqual(expected.CanPrepare, preview.GetProperty("canPrepare").GetBoolean(), preview.GetRawText());
        Assert.AreEqual(expected.ErrorCode, preview.GetProperty("errorCode").GetString(), preview.GetRawText());
        Assert.AreEqual(expected.CandidateXml, preview.GetProperty("candidateDesignXml").GetString(), preview.GetRawText());
        CollectionAssert.AreEqual(expected.NativeOperations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(),
            preview.GetProperty("nativeOperationsJson").EnumerateArray().Select(o => o.GetString()).ToArray(), preview.GetRawText());
        Assert.AreEqual(expected.NativeConnectivityValidationRequired, preview.GetProperty("nativeConnectivityValidationRequired").GetBoolean(),
            preview.GetRawText());
    }

    // The planning fixture with one saved XML revision that only joins two drawn pins (as in
    // AutomaticDesignSynchronizationTests), moved into a real project folder where an automatic worker keeps its
    // history. The editor checkpoint carries one exact native revision, and every sheet reports the connection
    // grid realization draws on (CN-1 §6.1).
    private static DesignRecoveryState ConnectionOnlyRevision(string projectDirectory)
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var schematic = state.Baseline.Schematic.Clone();
        schematic.Document.Project.Path = projectDirectory;
        foreach (var screen in schematic.Instances)
        {
            if (screen.Metadata.Document?.Project is { } owner) owner.Path = projectDirectory;
            screen.Metadata.Formatting = SchematicFormattingTests.Formatting();
        }
        var revision = new Protocol.DocumentRevision { Epoch = Guid.NewGuid().ToString("D"), Sequence = state.NativeRevision.Sequence };
        var baselineElectrical = state.BaselineElectrical!.Clone();
        baselineElectrical.Hierarchy.Data = schematic.Clone(); baselineElectrical.Hierarchy.Revision = revision.Clone();
        var observedElectrical = state.ObservedElectrical!.Clone();
        observedElectrical.Hierarchy.Data = schematic.Clone(); observedElectrical.Hierarchy.Revision = revision.Clone();
        state = state with { Baseline = state.Baseline with { Schematic = schematic }, Observed = schematic.Clone(),
            NativeRevision = new(revision.Epoch, revision.Sequence), BaselineElectrical = baselineElectrical, ObservedElectrical = observedElectrical };
        var circuit = state.Baseline.Engineering.Circuit;
        var design = state.Baseline with { Engineering = state.Baseline.Engineering with { Circuit = circuit with { Nets =
            [.. circuit.Nets, new CircuitNet(Guid.NewGuid(), "SIG", [new(circuit.Components[0].Id, "1"), new(circuit.Components[1].Id, "1")])] } } };
        return state with { DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
    }

    /// <summary>An editor on the real NNG request/reply transport. It answers its handshake, the checked checkpoint
    /// capture and the electrical observation of one saved state, refuses every other request, and records each
    /// request by message name. It stands in only for a KiCad that advertises schematic.connection-realization.v1,
    /// which no native build does yet (CN-1 §8.3).</summary>
    private sealed class ScriptedNngEditor : IDisposable
    {
        private readonly Nng.Socket socket;
        private readonly Task serving;
        private readonly object gate = new();
        private readonly List<string> requests = [];
        private readonly string instanceId, projectPath, eventEndpoint, epoch = Guid.NewGuid().ToString("D");
        private readonly Protocol.CheckedSchematicState checkpoint;
        private readonly Protocol.SchematicElectricalState electrical;
        private volatile bool advertises;
        internal string Endpoint { get; }
        internal bool Advertises { get => advertises; set => advertises = value; }
        internal string[] Requests { get { lock (gate) return [.. requests]; } }

        internal ScriptedNngEditor(string directory, DesignRecoveryState state, string projectPath)
        {
            instanceId = state.InstanceId.ToString("D"); this.projectPath = projectPath;
            Endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "editor.sock"));
            // Nothing publishes here: the worker's event subscription waits, which is all this check needs.
            eventEndpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "events.sock"));
            electrical = state.ObservedElectrical!.Clone();
            checkpoint = new Protocol.CheckedSchematicState
            {
                Electrical = electrical.Clone(),
                State = new() { Document = state.Baseline.Schematic.Document.Clone(), ProcessEpoch = epoch,
                    NativeIdentity = Guid.NewGuid().ToString("D"), Revision = electrical.Hierarchy.Revision.Clone(),
                    Scope = Protocol.DocumentLifecycleScope.DlsSchematicHierarchy, ProjectSettingsIncluded = true,
                    StateSha256 = new string('a', 64) }
            };
            string file = Path.Combine(Path.GetDirectoryName(projectPath)!, "fixture.kicad_sch");
            checkpoint.State.NativeFiles.Add(file);
            checkpoint.State.FileBaselines.Add(new Protocol.NativeFileBaselineState { Path = file, BaselinePath = file,
                BaselineKnown = true, BaselineExists = true, BaselineSha256 = new string('b', 64), BaselineBytes = 1,
                CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string('b', 64), CurrentBytes = 1,
                Status = Protocol.NativeFileBaselineStatus.NfbsUnchanged });
            Nng.Check(Nng.nng_rep0_open(out socket));
            try
            {
                Nng.Check(Nng.nng_setopt_size(socket, "recv-size-max", 8 * 1024 * 1024));
                Nng.Check(Nng.nng_listen(socket, Endpoint, IntPtr.Zero, 0));
            }
            catch { Nng.nng_close(socket); throw; }
            serving = Task.Factory.StartNew(Serve, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal Protocol.AutomationSession Session(bool advertises)
        {
            var session = new Protocol.AutomationSession { ProtocolVersion = 1, InstanceId = instanceId, ProjectPath = projectPath,
                Epoch = epoch, EventEndpoint = eventEndpoint, EventEpoch = epoch };
            session.Capabilities.Add("session.info");
            if (advertises) session.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
            return session;
        }

        private void Serve()
        {
            // Closing the socket ends the blocked receive.
            while (true)
            {
                nuint size = 0;
                if (Nng.nng_recv(socket, out IntPtr buffer, ref size, 1) != 0) return;
                byte[] request = new byte[checked((int)size)];
                try { System.Runtime.InteropServices.Marshal.Copy(buffer, request, 0, request.Length); }
                finally { Nng.nng_free(buffer, size); }
                byte[] reply = Answer(request);
                if (Nng.nng_send(socket, reply, (nuint)reply.Length, 0) != 0) return;
            }
        }

        private byte[] Answer(byte[] bytes)
        {
            var message = Kiapi.Common.ApiRequest.Parser.ParseFrom(bytes).Message;
            lock (gate) requests.Add(message.TypeUrl[(message.TypeUrl.LastIndexOf('/') + 1)..]);
            Google.Protobuf.IMessage? reply = message.Is(Protocol.GetAutomationSession.Descriptor) ? Session(advertises)
                : message.Is(Protocol.ReadCheckedSchematicState.Descriptor) ? checkpoint.Clone()
                : message.Is(Protocol.ReadSchematicElectricalState.Descriptor) ? electrical.Clone() : null;
            var response = new Kiapi.Common.ApiResponse
            {
                Header = new() { KicadToken = epoch },
                Status = new() { Status = (Kiapi.Common.ApiStatusCode)(reply is null ? 3 : 1),
                    ErrorMessage = reply is null ? "Refused by the scripted editor" : "" }
            };
            if (reply is not null) response.Message = Any.Pack(reply);
            return Google.Protobuf.MessageExtensions.ToByteArray(response);
        }

        public void Dispose()
        {
            Nng.nng_close(socket);
            try { serving.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
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
