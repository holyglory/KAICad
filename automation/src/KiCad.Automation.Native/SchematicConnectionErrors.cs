namespace KiCad.Automation.Native;

/// <summary>CN-1 error codes (cn1-wiring-intent.md §13). Codes are API: add new
/// ones, never rename these. Every code fails before any native mutation unless
/// its section says otherwise.</summary>
public static class SchematicConnectionErrors
{
    // Planning.
    public const string XmlDisconnectionUnsupported = "xml_disconnection_unsupported";
    public const string ConnectedPinUnresolved = "connected_pin_unresolved";
    public const string ConnectedPinAmbiguous = "connected_pin_ambiguous";
    public const string ConnectedPinIdentityMissing = "connected_pin_identity_missing";
    public const string ConnectedPinDriverConflict = "connected_pin_driver_conflict";
    public const string ConnectedVariantSymbolUnsupported = "connected_variant_symbol_unsupported";
    public const string ConnectedBusRealizationUnsupported = "connected_bus_realization_unsupported";
    public const string ConnectedGlobalNameConflict = "connected_global_name_conflict";
    public const string ConnectedImplicitPowerConflict = "connected_implicit_power_conflict";
    public const string ConnectedPowerNameUnresolved = "connected_power_name_unresolved";
    public const string ConnectedNetNameConflict = "connected_net_name_conflict";
    public const string ConnectedLabelTextInvalid = "connected_label_text_invalid";
    public const string ConnectedLabelTextDivergent = "connected_label_text_divergent";
    public const string ConnectedRepeatedScreenDivergent = "connected_repeated_screen_divergent";
    public const string ConnectedHierarchyUnsupported = "connected_hierarchy_unsupported";
    public const string ConnectedLockedSheetSymbol = "connected_locked_sheet_symbol";
    public const string ConnectedScopeTooLarge = "connected_scope_too_large";
    public const string ConnectedInternalInconsistency = "connected_internal_inconsistency";
    // Pins that one placed symbol's own definition draws at the same point are always one
    // connection in KiCad, so XML that puts them on different nets can never be drawn. Raised
    // by the creation projection and the connection intent, before any native change
    // (decision kicad-stacked-pins-one-node-20260924).
    public const string StackedPinsOnDifferentNets = "stacked_pins_on_different_nets";

    // Realization.
    public const string NativeCapabilityMissing = "native_capability_missing";
    public const string RealizationGridUnavailable = "realization_grid_unavailable";
    public const string RealizationMeasurementUnsupported = "realization_measurement_unsupported";
    public const string RealizationMeasurementStale = "realization_measurement_stale";
    public const string RealizationMeasurementIncomplete = "realization_measurement_incomplete";
    public const string RealizationPinGeometryIncomplete = "realization_pin_geometry_incomplete";
    public const string RealizationVariantPinIdentityUnresolved = "realization_variant_pin_identity_unresolved";
    public const string RealizationPinGeometryMismatch = "realization_pin_geometry_mismatch";
    public const string RealizationImplicitPowerMismatch = "realization_implicit_power_mismatch";
    public const string RealizationLabelOrientationMismatch = "realization_label_orientation_mismatch";
    public const string RealizationNoConnectConflict = "realization_no_connect_conflict";
    public const string RealizationCreatedPinContact = "realization_created_pin_contact";
    public const string RealizationNoFreeStub = "realization_no_free_stub";
    public const string RealizationNoJoinAnchor = "realization_no_join_anchor";
    public const string RealizationNoFreeSheetPinSlot = "realization_no_free_sheet_pin_slot";
    public const string RealizationIdentityCollision = "realization_identity_collision";
    public const string RealizationBatchTooLarge = "realization_batch_too_large";
    public const string RealizationVariantPinMissing = "realization_variant_pin_missing";
    public const string RealizationVariantGeometryDivergent = "realization_variant_geometry_divergent";

    // Native receipt error_code strings. A postcondition failure proves no native
    // mutation; an unverified assertion requires inspection.
    public const string ConnectivityPostconditionFailed = "connectivity_postcondition_failed";
    public const string ConnectivityAssertionUnverified = "connectivity_assertion_unverified";

    // Execution.
    public const string RealizationConnectivityMismatch = "realization_connectivity_mismatch";
    public const string RealizationAssertionUnverified = "realization_assertion_unverified";
    public const string RealizationResolutionMismatch = "realization_resolution_mismatch";
    public const string InvalidRealizationRejection = "invalid_realization_rejection";
    public const string InvalidLayoutIntent = "invalid_layout_intent";

    // Non-blocking diagnostics.
    public const string ExistingNetNamedByRealization = "existing_net_named_by_realization";
    public const string RealizationPageReservationsUnspecified = "realization_page_reservations_unspecified";

    // Existing codes CN-1 reuses unchanged, so lane code spells them once.
    // Every SchematicNativeCreationProjection code (§13 "Reused unchanged"). Each equals
    // the literal that projection throws; CreatedBindingInvalid and
    // CreatedSymbolPlacementRequired below are also projection codes.
    public const string UnresolvedDesignBindings = "unresolved_design_bindings";
    public const string PartDefinitionChangeRequiresResolution = "part_definition_change_requires_resolution";
    public const string NewPartRequiresLibraryDefinition = "new_part_requires_library_definition";
    public const string SheetOwnershipChangeRequiresResolution = "sheet_ownership_change_requires_resolution";
    public const string ComponentRebindingRequiresResolution = "component_rebinding_requires_resolution";
    public const string DuplicateComponentDefinition = "duplicate_component_definition";
    public const string NoComponentCreation = "no_component_creation";
    public const string ComponentDefinitionRequired = "component_definition_required";
    public const string UnknownComponentSheet = "unknown_component_sheet";
    public const string CreatedComponentConnectivityRequiresResolution = "created_component_connectivity_requires_resolution";
    public const string SymbolRebindingRequiresResolution = "symbol_rebinding_requires_resolution";
    public const string SymbolOwnerRequiresResolution = "symbol_owner_requires_resolution";
    public const string ComponentUnitsIncomplete = "component_units_incomplete";
    public const string CreatedUnitSheetCoverageMismatch = "created_unit_sheet_coverage_mismatch";
    public const string SharedSymbolPlacementConflict = "shared_symbol_placement_conflict";
    public const string CreatedNativeIdentityCollision = "created_native_identity_collision";
    public const string MissingNativeSheet = "missing_native_sheet";
    public const string CreatedNativeIdentityMissing = "created_native_identity_missing";
    public const string MissingSymbolTemplate = "missing_symbol_template";
    public const string AmbiguousSymbolTemplate = "ambiguous_symbol_template";
    public const string IncompleteSymbolFields = "incomplete_symbol_fields";
    public const string IncompleteSymbolInstanceRecords = "incomplete_symbol_instance_records";
    public const string AmbiguousSymbolInstanceRecord = "ambiguous_symbol_instance_record";
    public const string IncompleteSymbolTemplate = "incomplete_symbol_template";
    public const string SymbolUnitMismatch = "symbol_unit_mismatch";
    public const string SymbolPinTemplateMismatch = "symbol_pin_template_mismatch";
    public const string UnsupportedSymbolChild = "unsupported_symbol_child";
    public const string CreatedSymbolCacheConflict = "created_symbol_cache_conflict";
    public const string CreationBindingsChanged = "creation_bindings_changed";
    public const string CreationRequiresStableNativeHierarchy = "creation_requires_stable_native_hierarchy";
    public const string CreationRequiresStableConnectivity = "creation_requires_stable_connectivity";
    public const string UnalignedElectricalBaseline = "unaligned_electrical_baseline";
    public const string CreatedBindingInvalid = "created_binding_invalid";
    public const string CreatedSymbolPlacementRequired = "created_symbol_placement_required";
    public const string InconsistentDesignSerialization = "inconsistent_design_serialization";
    public const string NativeCheckpointStale = "native_checkpoint_stale";
    public const string NativeFileConflict = "native_file_conflict";
    public const string NativeSyncNotCommitted = "native_sync_not_committed";
    public const string NativeChangedDuringSync = "native_changed_during_sync";
    public const string NativeSyncConnectivityMismatch = "native_sync_connectivity_mismatch";
    public const string PublicationTargetChanged = "publication_target_changed";
    public const string InstanceChanged = "instance_changed";
    public const string UnsupportedLayoutCreation = "unsupported_layout_creation";
}
