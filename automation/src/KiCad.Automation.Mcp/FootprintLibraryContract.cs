using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Managed admission and readback checks for the legacy footprint-library API.
///
/// The native footprint editor predates document lifecycle revisions, so its
/// operation receipts are the only durable identity boundary available to this
/// surface. Keep the boundary fail-closed: a native response for another
/// operation or a non-completed receipt must never be exposed as a successful
/// MCP mutation.
/// </summary>
internal static class FootprintLibraryContract
{
    private const int MaximumDefinitionItems = 16_384;

    /// <summary>
    /// Typed evidence for the detached definition envelope.  The native response's
    /// boolean is retained as <see cref="NativeDefinitionComplete"/>, but callers
    /// must use <see cref="Complete"/> before advertising complete content: it also
    /// requires valid digest syntax, a decodable nested item envelope and the
    /// mandatory footprint properties.  Native writer/provider equality is checked
    /// separately by the persistence verifier; these flags do not claim that a
    /// digest is the digest for another response.
    /// </summary>
    internal sealed record FootprintDefinitionCoverage(
        bool NativeDefinitionComplete,
        bool Complete,
        bool CanonicalWriterHashValid,
        bool ProviderIdentityHashValid,
        bool NestedItemsDecoded,
        bool PadsDecoded,
        bool PropertiesPresent,
        bool ItemsKnownAndDecoded,
        int ItemCount,
        int GeometryItemCount,
        int PadCount,
        int PropertyCount,
        bool PersistedReadbackVerified,
        bool CanonicalHashMatched,
        string[] Limitations)
    {
        public string Status => Complete ? "complete" : "incomplete";

        internal FootprintDefinitionCoverage WithPersistence(bool persistedReadbackVerified,
            bool canonicalHashMatched) => this with
        {
            PersistedReadbackVerified = persistedReadbackVerified,
            CanonicalHashMatched = canonicalHashMatched,
            Complete = Complete && (!persistedReadbackVerified || canonicalHashMatched)
        };
    }

    /// <summary>
    /// Computes conservative, typed coverage from the native definition envelope.
    /// Unknown or malformed Any children are evidence gaps even when the native peer
    /// marked the response complete.
    /// </summary>
    internal static FootprintDefinitionCoverage InspectDefinitionCoverage(
        NativeCapabilityFootprintReadDefinitionResponse? response)
    {
        var limitations = response?.Limitations?.ToList() ?? [];
        if (response is null)
        {
            limitations.Add("native definition response is missing");
            return IncompleteCoverage(limitations);
        }

        bool canonicalHash = ValidSha256(response.CanonicalSha256);
        bool providerHash = ValidSha256(response.ProviderSha256);
        if (!canonicalHash)
            limitations.Add("canonical native-writer SHA-256 is missing or malformed");
        if (!providerHash)
            limitations.Add("provider identity SHA-256 is missing or malformed");

        FootprintInstance? instance = response.Footprint;
        Footprint? definition = instance?.Definition;
        if (instance is null || definition is null)
        {
            limitations.Add("native definition envelope is missing its FootprintInstance or nested definition");
            return new(response.DefinitionComplete, false, canonicalHash, providerHash,
                false, false, false, false, 0, 0, 0, 0, false, false,
                limitations.Distinct(StringComparer.Ordinal).ToArray());
        }

        bool properties = definition.Id is not null
            && !string.IsNullOrWhiteSpace(definition.Id.LibraryNickname)
            && !string.IsNullOrWhiteSpace(definition.Id.EntryName)
            && definition.ReferenceField is not null
            && definition.ValueField is not null
            && definition.DatasheetField is not null
            && definition.DescriptionField is not null;
        int propertyCount = (definition.Id is null ? 0 : 1)
            + (definition.Attributes is null ? 0 : 1)
            + (definition.Overrides is null ? 0 : 1)
            + (definition.ReferenceField is null ? 0 : 1)
            + (definition.ValueField is null ? 0 : 1)
            + (definition.DatasheetField is null ? 0 : 1)
            + (definition.DescriptionField is null ? 0 : 1)
            + definition.NetTies.Count + definition.PrivateLayers.Count
            + (definition.Jumpers is null ? 0 : 1);
        if (!properties)
            limitations.Add("nested footprint properties are incomplete");

        if (definition.Items.Count > MaximumDefinitionItems)
            limitations.Add($"nested footprint item count exceeds {MaximumDefinitionItems}");

        int geometryCount = 0;
        int padCount = 0;
        bool nestedComplete = definition.Items.Count <= MaximumDefinitionItems;
        bool padsComplete = true;
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Any? item in definition.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.TypeUrl) || item.Value.Length == 0)
            {
                nestedComplete = false;
                limitations.Add("nested footprint item is missing a typed protobuf Any payload");
                continue;
            }

            try
            {
                if (item.Is(Pad.Descriptor))
                {
                    Pad pad = item.Unpack<Pad>();
                    padCount++;
                    geometryCount++;
                    // KiCad intentionally leaves NPTH pad numbers empty (for
                    // example mounting holes).  Number is required only for pad
                    // kinds that participate in electrical pin identity.
                    padsComplete &= pad.PadStack is not null
                        && pad.PadStack.Layers.Count > 0
                        && pad.PadStack.CopperLayers.Count > 0
                        && (pad.Type == PadType.PtNpth || !string.IsNullOrWhiteSpace(pad.Number));
                    if (pad.Id is not null && !CanonicalUuid(pad.Id.Value))
                        padsComplete = false;
                    if (pad.Id is not null && !itemIds.Add(pad.Id.Value))
                        nestedComplete = false;
                    if (!padsComplete)
                        limitations.Add("nested footprint pad geometry or identity is incomplete");
                }
                else if (item.Is(BoardGraphicShape.Descriptor))
                {
                    BoardGraphicShape shape = item.Unpack<BoardGraphicShape>();
                    geometryCount++;
                    nestedComplete &= shape.Shape is not null
                        && shape.Shape.GeometryCase != GraphicShape.GeometryOneofCase.None;
                    if (shape.Id is not null && !CanonicalUuid(shape.Id.Value))
                        nestedComplete = false;
                    if (shape.Id is not null && !itemIds.Add(shape.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(BoardText.Descriptor))
                {
                    BoardText text = item.Unpack<BoardText>();
                    geometryCount++;
                    nestedComplete &= text.Id is null || CanonicalUuid(text.Id.Value);
                    if (text.Id is not null && !itemIds.Add(text.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(BoardTextBox.Descriptor))
                {
                    BoardTextBox textBox = item.Unpack<BoardTextBox>();
                    geometryCount++;
                    nestedComplete &= textBox.Id is null || CanonicalUuid(textBox.Id.Value);
                    if (textBox.Id is not null && !itemIds.Add(textBox.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(Barcode.Descriptor))
                {
                    Barcode barcode = item.Unpack<Barcode>();
                    geometryCount++;
                    nestedComplete &= barcode.Id is null || CanonicalUuid(barcode.Id.Value);
                    if (barcode.Id is not null && !itemIds.Add(barcode.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(Dimension.Descriptor))
                {
                    Dimension dimension = item.Unpack<Dimension>();
                    geometryCount++;
                    nestedComplete &= dimension.Id is null || CanonicalUuid(dimension.Id.Value);
                    if (dimension.Id is not null && !itemIds.Add(dimension.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(ReferenceImage.Descriptor))
                {
                    ReferenceImage image = item.Unpack<ReferenceImage>();
                    geometryCount++;
                    nestedComplete &= image.Id is null || CanonicalUuid(image.Id.Value);
                    if (image.Id is not null && !itemIds.Add(image.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(Footprint3DModel.Descriptor))
                {
                    _ = item.Unpack<Footprint3DModel>();
                    geometryCount++;
                }
                else if (item.Is(Kiapi.Board.Types.Field.Descriptor))
                {
                    Kiapi.Board.Types.Field field = item.Unpack<Kiapi.Board.Types.Field>();
                    geometryCount++;
                    nestedComplete &= field.Text is not null;
                }
                else if (item.Is(ReferencePoint.Descriptor))
                {
                    ReferencePoint point = item.Unpack<ReferencePoint>();
                    geometryCount++;
                    nestedComplete &= point.Id is null || CanonicalUuid(point.Id.Value);
                    if (point.Id is not null && !itemIds.Add(point.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(Zone.Descriptor))
                {
                    Zone zone = item.Unpack<Zone>();
                    geometryCount++;
                    nestedComplete &= zone.Id is null || CanonicalUuid(zone.Id.Value);
                    if (zone.Id is not null && !itemIds.Add(zone.Id.Value))
                        nestedComplete = false;
                }
                else if (item.Is(Table.Descriptor) || item.Is(Kiapi.Board.Types.Group.Descriptor)
                    || item.Is(Constraint.Descriptor))
                {
                    // These are typed nested properties.  Unpack them to prove that
                    // their payload is decodable, even though they have no pad count.
                    if (item.Is(Table.Descriptor))
                    {
                        _ = item.Unpack<Table>();
                        geometryCount++;
                    }
                    else if (item.Is(Kiapi.Board.Types.Group.Descriptor))
                    {
                        Kiapi.Board.Types.Group group = item.Unpack<Kiapi.Board.Types.Group>();
                        nestedComplete &= group.Id is null || CanonicalUuid(group.Id.Value);
                        if (group.Id is not null && !itemIds.Add(group.Id.Value))
                            nestedComplete = false;
                    }
                    else
                    {
                        Constraint constraint = item.Unpack<Constraint>();
                        nestedComplete &= constraint.Id is null || CanonicalUuid(constraint.Id.Value);
                        if (constraint.Id is not null && !itemIds.Add(constraint.Id.Value))
                            nestedComplete = false;
                    }
                }
                else
                {
                    nestedComplete = false;
                    limitations.Add("nested footprint item type is outside the typed coverage contract: "
                        + Any.GetTypeName(item.TypeUrl));
                }
            }
            catch (InvalidProtocolBufferException)
            {
                nestedComplete = false;
                limitations.Add("nested footprint item contains a malformed protobuf payload");
            }
        }

        bool complete = response.DefinitionComplete && canonicalHash && providerHash
            && nestedComplete && padsComplete && properties;
        return new(response.DefinitionComplete, complete, canonicalHash, providerHash,
            nestedComplete, padsComplete, properties, nestedComplete,
            definition.Items.Count, geometryCount, padCount, propertyCount,
            false, false, limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static void ValidateCompleteDefinition(
        NativeCapabilityFootprintReadDefinitionResponse response, string operation)
    {
        FootprintDefinitionCoverage coverage = InspectDefinitionCoverage(response);
        if (!coverage.Complete)
        {
            string limits = coverage.Limitations.Length == 0
                ? "the typed nested definition coverage is incomplete"
                : string.Join("; ", coverage.Limitations);
            throw new AutomationException("footprint_definition_incomplete",
                $"Cannot perform {operation} until the native footprint definition is complete: {limits}");
        }
    }

    internal static FootprintDefinitionCoverage ValidateDefinitionResponse(
        NativeCapabilityFootprintReadDefinitionResponse response,
        DocumentSpecifier expectedDocument, string expectedProcessEpoch,
        NativeFootprintDefinitionSource source, LibraryIdentifier? expectedCanonicalTarget)
    {
        if (response is null || response.Document is null || !response.Document.Equals(expectedDocument)
            || expectedDocument.Type != DocumentType.DoctypeFootprint
            || expectedDocument.LibId is null)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint definition response does not match the requested document.");
        if (string.IsNullOrWhiteSpace(expectedProcessEpoch)
            || !string.Equals(response.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("stale_process_epoch",
                "The native footprint definition response belongs to a different process epoch.");
        if ((int)source is not 1 and not 2)
            throw new AutomationException("invalid_footprint_definition_source",
                "The native footprint definition response has an unsupported source.");

        LibraryIdentifier? actualTarget = response.CanonicalTarget;
        if ((int)source == 2)
        {
            LibraryIdentifier expectedTarget = expectedCanonicalTarget ?? expectedDocument.LibId;
            if (actualTarget is null || !actualTarget.Equals(expectedTarget))
                throw new AutomationException("footprint_receipt_mismatch",
                    "The native editor definition response does not match the requested canonical target.");
        }
        else if (actualTarget is not null)
        {
            throw new AutomationException("footprint_receipt_mismatch",
                "A persisted footprint definition response must not carry an editor canonical target.");
        }

        FootprintDefinitionCoverage coverage = InspectDefinitionCoverage(response);
        if (response.DefinitionComplete && !coverage.Complete)
            throw new AutomationException("footprint_definition_incomplete",
                "The native footprint response claimed complete coverage but its typed nested content is malformed or incomplete: "
                + string.Join("; ", coverage.Limitations));
        return coverage;
    }

    /// <summary>
    /// Reports whether either native receipt source actually contains the
    /// requested operation. A successful read transport with Found=false is
    /// still an honest receipt miss and must not be advertised as available
    /// native evidence.
    /// </summary>
    internal static bool HasNativeReceipt(
        NativeCapabilityFootprintReadOperationResponse? editorReceipt,
        NativeCapabilityFootprintLibraryReadOperationResponse? libraryReceipt) =>
        editorReceipt?.Found == true || libraryReceipt?.Found == true;

    internal static void ValidateDeleteResponse(
        NativeCapabilityFootprintLibraryDeleteResponse response,
        string operationId, string libraryNickname, string footprintName)
    {
        ValidateOperationId(operationId);
        if (response is null
            || response.OperationId != operationId
            || response.LibraryNickname != libraryNickname
            || response.FootprintName != footprintName)
            throw new AutomationException("footprint_operation_mismatch",
                "The native footprint-library response does not match the requested operation or library entry.");
        if (!response.Completed)
            throw new AutomationException("footprint_native_rejected",
                $"The native footprint-library delete for {libraryNickname}:{footprintName} was not completed; inspect the native editor before retrying.");
    }

    internal static void ValidateEditorReceiptIdentity(
        NativeCapabilityFootprintReadOperationResponse receipt, string operationId)
    {
        ValidateOperationId(operationId);
        if (receipt is null || receipt.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint receipt does not match the requested operation ID.");
        if (!receipt.Found) return;
        if (!receipt.Completed || string.IsNullOrWhiteSpace(receipt.Operation))
            throw new AutomationException("footprint_native_rejected",
                "The native footprint receipt is present but does not report a completed operation.");
    }

    internal static void ValidateEditorReceipt(
        NativeCapabilityFootprintReadOperationResponse receipt, string operationId,
        string operation, DocumentSpecifier document)
    {
        ValidateEditorReceiptIdentity(receipt, operationId);
        if (!receipt.Found) return;
        if (!string.Equals(receipt.Operation, operation, StringComparison.Ordinal)
            || receipt.Document is null || !receipt.Document.Equals(document))
            throw new AutomationException("footprint_receipt_mismatch",
                $"The native footprint receipt does not match the requested {operation} document or operation.");
    }

    internal static void ValidateLibraryReceipt(
        NativeCapabilityFootprintLibraryReadOperationResponse receipt,
        string operationId, string libraryNickname, string footprintName)
    {
        ValidateLibraryReceiptIdentity(receipt, operationId);
        if (!receipt.Found) return;
        if (receipt.DeleteResponse is null)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt is present without its delete result.");
        ValidateDeleteResponse(receipt.DeleteResponse, operationId, libraryNickname, footprintName);
    }

    internal static void ValidateLibraryReceiptIdentity(
        NativeCapabilityFootprintLibraryReadOperationResponse receipt, string operationId)
    {
        ValidateOperationId(operationId);
        if (receipt is null || receipt.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt does not match the requested operation ID.");
        if (!receipt.Found) return;
        if (receipt.DeleteResponse is null || receipt.DeleteResponse.OperationId != operationId)
            throw new AutomationException("footprint_receipt_mismatch",
                "The native footprint-library receipt contains a delete result for another operation.");
        if (!receipt.DeleteResponse.Completed)
            throw new AutomationException("footprint_native_rejected",
                "The native footprint-library receipt is present but does not report a completed delete operation.");
    }

    internal static void ValidateDeletePersistence(
        NativeCapabilityFootprintLibraryReadNamesResponse readback,
        string operationId, string libraryNickname, string footprintName, ulong expectedModifyHash)
    {
        if (readback is null
            || readback.LibraryNickname != libraryNickname
            || readback.FootprintNames.Contains(footprintName, StringComparer.Ordinal)
            || readback.ModifyHash != expectedModifyHash)
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed footprint-library delete operation {operationId}, but the native library readback did not prove that {libraryNickname}:{footprintName} is absent at modify hash {expectedModifyHash}.");
    }

    internal static void ValidateDefinitionReadbackIdentity(
        NativeCapabilityFootprintReadDefinitionResponse before,
        NativeCapabilityFootprintReadDefinitionResponse after,
        DocumentSpecifier expectedBeforeDocument,
        DocumentSpecifier expectedAfterDocument,
        string expectedProcessEpoch,
        string operation,
        string operationId)
    {
        ValidateOperationId(operationId);
        if (before is null || after is null
            || before.Document is null || !before.Document.Equals(expectedBeforeDocument)
            || after.Document is null || !after.Document.Equals(expectedAfterDocument)
            || expectedBeforeDocument.Type != DocumentType.DoctypeFootprint
            || expectedAfterDocument.Type != DocumentType.DoctypeFootprint
            || expectedBeforeDocument.LibId is null || expectedAfterDocument.LibId is null
            || string.IsNullOrWhiteSpace(expectedProcessEpoch)
            || !string.Equals(before.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal)
            || !string.Equals(after.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but the definition readback identity did not match the source, destination or native process.");
    }

    private static FootprintDefinitionCoverage IncompleteCoverage(List<string> limitations) =>
        new(false, false, false, false, false, false, false, false,
            0, 0, 0, 0, false, false,
            limitations.Distinct(StringComparer.Ordinal).ToArray());

    private static bool ValidSha256(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length == 64 && value.All(IsHex);

    private static bool IsHex(char value) => value is >= '0' and <= '9'
        or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    internal static bool IsCanonicalUuid(string? value) => CanonicalUuid(value);

    private static bool CanonicalUuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out Guid parsed)
        && parsed != Guid.Empty
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static void ValidateOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID in canonical D format.");
    }
}
