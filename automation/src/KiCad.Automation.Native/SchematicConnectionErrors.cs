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
    // Only the freeze stubs raise this. It disappears when lane 2A delivers.
    public const string ConnectedAdditionUnavailable = "connected_addition_unavailable";

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
