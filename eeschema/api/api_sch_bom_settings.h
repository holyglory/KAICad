/* Typed project BOM view/export state. GPL-3.0-or-later. */
#ifndef API_SCH_BOM_SETTINGS_H
#define API_SCH_BOM_SETTINGS_H

#include <settings/bom_settings.h>
#include <schematic/schematic_types.pb.h>
#include <string>
#include <utility>

namespace SCH_BOM_SETTINGS
{
using MESSAGE = kiapi::schematic::types::SchematicBomSettings;
using VIEW = kiapi::schematic::types::SchematicBomView;
using FORMAT = kiapi::schematic::types::SchematicBomFormat;

inline VIEW CaptureView( const BOM_PRESET& aValue )
{
    VIEW value;
    value.set_name( aValue.name.ToStdString( wxConvUTF8 ) );
    for( const auto& field : aValue.fieldsOrdered )
    {
        auto* entry = value.add_fields();
        entry->set_name( field.name.ToStdString( wxConvUTF8 ) );
        entry->set_label( field.label.ToStdString( wxConvUTF8 ) );
        entry->set_show( field.show );
        entry->set_group_by( field.groupBy );
    }
    value.set_sort_field( aValue.sortField.ToStdString( wxConvUTF8 ) );
    value.set_sort_ascending( aValue.sortAsc );
    value.set_filter( aValue.filterString.ToStdString( wxConvUTF8 ) );
    switch( aValue.filterScope )
    {
    case BOM_FILTER_SCOPE::REFERENCE: value.set_filter_scope( kiapi::schematic::types::SBFS_REFERENCE ); break;
    case BOM_FILTER_SCOPE::VISIBLE: value.set_filter_scope( kiapi::schematic::types::SBFS_VISIBLE ); break;
    case BOM_FILTER_SCOPE::ALL: value.set_filter_scope( kiapi::schematic::types::SBFS_ALL ); break;
    }
    value.set_group_symbols( aValue.groupSymbols );
    value.set_exclude_dnp( aValue.excludeDNP );
    value.set_include_excluded_from_bom( aValue.includeExcludedFromBOM );
    return value;
}

inline FORMAT CaptureFormat( const BOM_FMT_PRESET& aValue )
{
    FORMAT value;
    value.set_name( aValue.name.ToStdString( wxConvUTF8 ) );
    value.set_field_delimiter( aValue.fieldDelimiter.ToStdString( wxConvUTF8 ) );
    value.set_string_delimiter( aValue.stringDelimiter.ToStdString( wxConvUTF8 ) );
    value.set_reference_delimiter( aValue.refDelimiter.ToStdString( wxConvUTF8 ) );
    value.set_reference_range_delimiter( aValue.refRangeDelimiter.ToStdString( wxConvUTF8 ) );
    value.set_keep_tabs( aValue.keepTabs );
    value.set_keep_line_breaks( aValue.keepLineBreaks );
    value.set_include_byte_order_mark( aValue.includeByteOrderMark );
    return value;
}

inline MESSAGE Capture( const FIELDS_TABLE_BOM_SETTINGS& aSettings )
{
    MESSAGE value;
    value.set_export_filename( aSettings.m_BomExportFileName.ToStdString( wxConvUTF8 ) );
    *value.mutable_current_view() = CaptureView( aSettings.m_BomSettings );
    for( const auto& view : aSettings.m_BomPresets )
        *value.add_saved_views() = CaptureView( view );
    *value.mutable_current_format() = CaptureFormat( aSettings.m_BomFmtSettings );
    for( const auto& format : aSettings.m_BomFmtPresets )
        *value.add_saved_formats() = CaptureFormat( format );
    return value;
}

inline BOM_PRESET RestoreView( const VIEW& aValue )
{
    BOM_PRESET value;
    value.name = wxString::FromUTF8( aValue.name() );
    for( const auto& field : aValue.fields() )
        value.fieldsOrdered.push_back( { wxString::FromUTF8( field.name() ),
                wxString::FromUTF8( field.label() ), field.show(), field.group_by() } );
    value.sortField = wxString::FromUTF8( aValue.sort_field() );
    value.sortAsc = aValue.sort_ascending();
    value.filterString = wxString::FromUTF8( aValue.filter() );
    switch( aValue.filter_scope() )
    {
    case kiapi::schematic::types::SBFS_REFERENCE: value.filterScope = BOM_FILTER_SCOPE::REFERENCE; break;
    case kiapi::schematic::types::SBFS_VISIBLE: value.filterScope = BOM_FILTER_SCOPE::VISIBLE; break;
    case kiapi::schematic::types::SBFS_ALL: value.filterScope = BOM_FILTER_SCOPE::ALL; break;
    default: break; // Validate rejects unknown values before live mutation.
    }
    value.groupSymbols = aValue.group_symbols();
    value.excludeDNP = aValue.exclude_dnp();
    value.includeExcludedFromBOM = aValue.include_excluded_from_bom();
    return value;
}

inline BOM_FMT_PRESET RestoreFormat( const FORMAT& aValue )
{
    BOM_FMT_PRESET value;
    value.name = wxString::FromUTF8( aValue.name() );
    value.fieldDelimiter = wxString::FromUTF8( aValue.field_delimiter() );
    value.stringDelimiter = wxString::FromUTF8( aValue.string_delimiter() );
    value.refDelimiter = wxString::FromUTF8( aValue.reference_delimiter() );
    value.refRangeDelimiter = wxString::FromUTF8( aValue.reference_range_delimiter() );
    value.keepTabs = aValue.keep_tabs();
    value.keepLineBreaks = aValue.keep_line_breaks();
    value.includeByteOrderMark = aValue.include_byte_order_mark();
    return value;
}

inline FIELDS_TABLE_BOM_SETTINGS Prepare( const MESSAGE& aValue )
{
    FIELDS_TABLE_BOM_SETTINGS value;
    value.m_BomExportFileName = wxString::FromUTF8( aValue.export_filename() );
    value.m_BomSettings = RestoreView( aValue.current_view() );
    for( const auto& view : aValue.saved_views() )
        value.m_BomPresets.push_back( RestoreView( view ) );
    value.m_BomFmtSettings = RestoreFormat( aValue.current_format() );
    for( const auto& format : aValue.saved_formats() )
        value.m_BomFmtPresets.push_back( RestoreFormat( format ) );
    return value;
}

inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    { aFailure = "BOM settings contain unsupported fields"; return false; }
    if( !aValue.has_current_view() || !aValue.has_current_format() )
    { aFailure = "BOM settings require explicit current view and format"; return false; }
    auto validScope = []( const VIEW& view )
    { return view.filter_scope() >= kiapi::schematic::types::SBFS_REFERENCE
             && view.filter_scope() <= kiapi::schematic::types::SBFS_ALL; };
    if( !validScope( aValue.current_view() ) )
    { aFailure = "BOM view has an unsupported filter scope"; return false; }
    for( const auto& view : aValue.saved_views() )
        if( !validScope( view ) )
        { aFailure = "Saved BOM view has an unsupported filter scope"; return false; }
    // Native JSON supports empty, repeated and Unicode names. Do not invent
    // uniqueness restrictions or infer built-in names. Check exact conversion.
    if( Capture( Prepare( aValue ) ).SerializeAsString() != aValue.SerializeAsString() )
    { aFailure = "BOM text cannot be reconstructed losslessly"; return false; }
    return true;
}

inline void Swap( FIELDS_TABLE_BOM_SETTINGS& aSettings, FIELDS_TABLE_BOM_SETTINGS& aPrepared )
{
    std::swap( aSettings.m_BomExportFileName, aPrepared.m_BomExportFileName );
    std::swap( aSettings.m_BomSettings, aPrepared.m_BomSettings );
    std::swap( aSettings.m_BomPresets, aPrepared.m_BomPresets );
    std::swap( aSettings.m_BomFmtSettings, aPrepared.m_BomFmtSettings );
    std::swap( aSettings.m_BomFmtPresets, aPrepared.m_BomFmtPresets );
}

inline void Restore( FIELDS_TABLE_BOM_SETTINGS& aSettings, const MESSAGE& aValue )
{
    auto prepared = Prepare( aValue );
    Swap( aSettings, prepared );
}

inline FIELDS_TABLE_BOM_SETTINGS PrepareUiEdit( const FIELDS_TABLE_BOM_SETTINGS& aLive,
        const FIELDS_TABLE_BOM_SETTINGS& aBeforeUi, const FIELDS_TABLE_BOM_SETTINGS& aAfterUi,
        bool aSaveFilename )
{
    // The editor normalizes its initial presentation. Only a subsequent UI
    // change owns replacement of a persisted group; untouched live values and
    // their transient flags must survive Cancel and unrelated table actions.
    FIELDS_TABLE_BOM_SETTINGS desired = aLive;
    if( nlohmann::json( aBeforeUi.m_BomSettings ) != nlohmann::json( aAfterUi.m_BomSettings ) )
        desired.m_BomSettings = aAfterUi.m_BomSettings;
    if( nlohmann::json( aBeforeUi.m_BomPresets ) != nlohmann::json( aAfterUi.m_BomPresets ) )
        desired.m_BomPresets = aAfterUi.m_BomPresets;
    if( nlohmann::json( aBeforeUi.m_BomFmtSettings ) != nlohmann::json( aAfterUi.m_BomFmtSettings ) )
        desired.m_BomFmtSettings = aAfterUi.m_BomFmtSettings;
    if( nlohmann::json( aBeforeUi.m_BomFmtPresets ) != nlohmann::json( aAfterUi.m_BomFmtPresets ) )
        desired.m_BomFmtPresets = aAfterUi.m_BomFmtPresets;
    if( aSaveFilename && aBeforeUi.m_BomExportFileName != aAfterUi.m_BomExportFileName )
        desired.m_BomExportFileName = aAfterUi.m_BomExportFileName;
    return desired;
}
}
#endif
