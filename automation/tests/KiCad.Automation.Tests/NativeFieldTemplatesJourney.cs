using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyFieldTemplateRemoval(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        string project = (await client.HandshakeAsync(token)).ProjectPath;
        async Task<JsonNode> SaveProject()
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            return JsonNode.Parse(await File.ReadAllTextAsync(project, token))!;
        }
        async Task Finish(bool accept)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup")) await Task.Delay(50, limit.Token);
        }
        Task Capture(string phase) => NativeKeyboard.CaptureAsync(display,
            Path.Combine(evidence, processId + "-field-template-" + phase + ".png"), token);
        JsonArray Fields(JsonNode value) => value["schematic"]!["drawing"]!["field_names"]!.AsArray();
        var originalProject = await SaveProject();
        Assert.HasCount(0, Fields(originalProject), "The isolated fixture starts with no project field templates.");
        var original = await Read();
        await NativeSetupUi.Open(client, document, display, processId, token);
        await NativeSetupUi.SelectPage(display, processId, 75, token);
        await Capture("empty");
        // These are ordinary native toolbar controls. Adding a row opens its
        // name editor; the downstream saved project proves the action happened.
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
            clickFromLeft: 290, clickFromBottom: 75);
        // The row's editor is opened after native layout/focus handling. Target
        // the stable row explicitly before replacing its initial field name.
        await NativeSetupUi.StableGeometry(display, processId, token);
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
            clickFromLeft: 350, clickFromTop: 67);
        NativeKeyboard.SchematicShortcut(display, processId, "F2", "Schematic Setup", false, false);
        await NativeSetupUi.StableGeometry(display, processId, token);
        await Capture("name-editor");
        NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true, false);
        foreach (char c in "AutomationTemplate")
            NativeKeyboard.SchematicShortcut(display, processId, c.ToString(), "Schematic Setup", false, false);
        NativeKeyboard.SchematicShortcut(display, processId, "Tab", "Schematic Setup", false, false);
        await Capture("added");
        await Finish(true);
        var withTemplate = await SaveProject();
        Assert.HasCount(1, Fields(withTemplate));
        Assert.AreEqual("AutomationTemplate", Fields(withTemplate)[0]!["name"]!.GetValue<string>());
        var created = await Read();
        Assert.AreEqual(original.Revision.Sequence + 1, created.Revision.Sequence);
        Assert.AreEqual(original.Data, created.Data, "Project templates must not change existing component fields or other snapshot data.");

        foreach (bool accept in new[] { false, true })
        {
            var before = await Read();
            await NativeSetupUi.Open(client, document, display, processId, token);
            await NativeSetupUi.SelectPage(display, processId, 75, token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 350, clickFromTop: 67);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromLeft: 400, clickFromBottom: 75);
            await Capture("removed-" + accept);
            // Transfer an empty list to the shared draft before acceptance.
            await NativeSetupUi.SelectPage(display, processId, 34, token);
            await NativeSetupUi.SelectPage(display, processId, 75, token);
            await Finish(accept);
            var after = await Read();
            Assert.AreEqual(before.Revision.Sequence + (accept ? 1UL : 0UL), after.Revision.Sequence);
            Assert.AreEqual(before.Data, after.Data);
            var saved = await SaveProject();
            Assert.IsTrue(JsonNode.DeepEquals(accept ? originalProject["schematic"] : withTemplate["schematic"], saved["schematic"]),
                "Removing the last project field template must affect only that list; Cancel retains it.");
            if (!accept) continue;
            foreach (var (key, expected) in new[] { ("z", withTemplate), ("y", originalProject) })
            {
                var prior = await Read();
                NativeKeyboard.SchematicShortcut(display, processId, key);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                SchematicScreenDataSnapshot restored;
                do
                {
                    restored = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, limit.Token);
                    if (restored.Revision.Equals(prior.Revision)) await Task.Delay(50, limit.Token);
                } while (restored.Revision.Equals(prior.Revision));
                Assert.AreEqual(original.Data, restored.Data);
                Assert.IsTrue(JsonNode.DeepEquals(expected["schematic"], (await SaveProject())["schematic"]));
            }
        }
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        Assert.IsTrue(JsonNode.DeepEquals(originalProject["schematic"], (await SaveProject())["schematic"]));
        Assert.AreEqual(original.Data, (await Read()).Data);
    }
}
