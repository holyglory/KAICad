using System.Text.Json;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>
/// Managed checks for the structured symbol-library admission boundary.  The
/// STDIO case starts the compiled MCP server but deliberately uses an
/// untracked instance, so it proves tool discovery and refusal semantics
/// without starting KiCad or a native build.
/// </summary>
[TestClass]
public sealed class SymbolLibraryTypedMcpProcessTests
{
    [TestMethod]
    public void TypedSymbolLibraryInputBindsDocumentAndProtobufDefinition()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "kicad-symbol-typed-contract");
        var input = new SymbolLibraryTypedInput
        {
            Action = SymbolLibraryTypedAction.Validate,
            Document = Document("Demo", projectPath, "Device", "R_TYPED"),
            Definition = new SchematicSymbol
            {
                Id = new() { LibraryNickname = "Device", EntryName = "R_TYPED" },
                Type = SchematicSymbolType.SstNormal,
                UnitCount = 1,
                PinsUseLocalCoordinates = true
            }
        };

        SymbolLibraryTypedPayload payload = input.ToPayload();
        Assert.AreEqual(SymbolLibraryTypedAction.Validate, payload.Action);
        Assert.AreEqual(DocumentType.DoctypeSymbol, payload.Document.Type);
        Assert.AreEqual("Demo", payload.Document.Project.Name);
        Assert.AreEqual("Device", payload.Document.LibId.LibraryNickname);
        Assert.AreEqual("R_TYPED", payload.Definition!.Id!.EntryName);

        var mismatch = new SymbolLibraryTypedInput
        {
            Action = SymbolLibraryTypedAction.Validate,
            Document = Document("Demo", projectPath, "Device", "R_TYPED"),
            Definition = new SchematicSymbol
            {
                Id = new() { LibraryNickname = "Other", EntryName = "R_TYPED" },
                Type = SchematicSymbolType.SstNormal,
                UnitCount = 1
            }
        };
        AutomationException error = Assert.ThrowsExactly<AutomationException>(() => mismatch.ToPayload());
        Assert.AreEqual("invalid_symbol_library_typed_input", error.Code);
    }

    [TestMethod]
    public void TypedSymbolLibraryMutationRequiresCheckedIdentityForWrites()
    {
        var input = new SymbolLibraryTypedInput
        {
            Action = SymbolLibraryTypedAction.Write,
            Document = Document("Demo", "/tmp/kicad-symbol-typed-contract", "Device", "R_TYPED"),
            Definition = new SchematicSymbol
            {
                Id = new() { LibraryNickname = "Device", EntryName = "R_TYPED" },
                Type = SchematicSymbolType.SstNormal,
                UnitCount = 1
            },
            OperationId = Guid.NewGuid().ToString("D")
        };
        AutomationException missingEpoch = Assert.ThrowsExactly<AutomationException>(() => input.ToPayload());
        Assert.AreEqual("invalid_symbol_library_typed_input", missingEpoch.Code);

        input.ProcessEpoch = Guid.NewGuid().ToString("D");
        SymbolLibraryTypedPayload payload = input.ToPayload();
        Assert.AreEqual(SymbolLibraryTypedAction.Write, payload.Action);
        Assert.AreEqual(input.ProcessEpoch, payload.ProcessEpoch);
        Assert.AreEqual(input.OperationId, payload.OperationId);
    }

    [TestMethod]
    public async Task TypedSymbolLibrarySchemaAndMalformedAdmissionAreProvenOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("kicad-symbol-library-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tools = listed.GetProperty("tools");
            static bool HasObjectType(JsonElement schema)
            {
                JsonElement type = schema.GetProperty("type");
                return type.ValueKind == JsonValueKind.String
                    ? type.GetString() == "object"
                    : type.EnumerateArray().Any(item => item.GetString() == "object");
            }
            JsonElement typed = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "kicad_symbol_library_mutation_typed");
            JsonElement properties = typed.GetProperty("inputSchema").GetProperty("properties");
            Assert.IsTrue(properties.TryGetProperty("instanceId", out _), typed.GetRawText());
            Assert.IsTrue(properties.TryGetProperty("input", out JsonElement inputSchema), typed.GetRawText());
            JsonElement inputProperties = inputSchema.GetProperty("properties");
            foreach (string property in new[]
            {
                "action", "document", "processEpoch", "operationId", "symbolName", "definition",
                "pinDisplay", "expectedState", "expectedSnapshotSha256", "expectedModifyHash", "overwrite"
            })
                Assert.IsTrue(inputProperties.TryGetProperty(property, out _), property);
            Assert.IsTrue(HasObjectType(inputProperties.GetProperty("document")));
            Assert.IsTrue(HasObjectType(inputProperties.GetProperty("definition")));
            Assert.IsTrue(HasObjectType(inputProperties.GetProperty("expectedState")));

            Assert.IsTrue(tools.EnumerateArray().Any(tool =>
                tool.GetProperty("name").GetString() == "kicad_symbol_library_validate"),
                "The legacy JSON symbol validator must remain discoverable.");

            JsonElement refusal = await mcp.Tool("kicad_symbol_library_mutation_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_symbol_library_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());

            JsonElement mismatched = await mcp.Tool("kicad_symbol_library_mutation_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"),
                input = new
                {
                    action = "validate",
                    document = new
                    {
                        type = "DOCTYPE_SYMBOL",
                        project = new { name = "Demo", path = root },
                        libId = new { libraryNickname = "Device", entryName = "R_TYPED" }
                    },
                    definition = new
                    {
                        id = new { libraryNickname = "Other", entryName = "R_TYPED" },
                        type = "SstNormal",
                        unitCount = 1
                    }
                }
            });
            Assert.IsTrue(mismatched.GetProperty("isError").GetBoolean(), mismatched.GetRawText());
            Assert.AreEqual("invalid_symbol_library_typed_input",
                mismatched.GetProperty("structuredContent").GetProperty("errorCode").GetString(), mismatched.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static NativeDocumentInput Document(string name, string path, string library, string entry) => new()
    {
        Type = NativeDocumentType.Symbol,
        Project = new NativeProjectInput { Name = name, Path = path },
        LibId = new NativeLibraryIdentifierInput { LibraryNickname = library, EntryName = entry }
    };
}
