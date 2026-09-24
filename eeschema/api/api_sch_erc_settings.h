/* Typed live ERC settings capture. GPL-3.0-or-later. */
#ifndef API_SCH_ERC_SETTINGS_H
#define API_SCH_ERC_SETTINGS_H

#include <api/api_enums.h>
#include <erc/erc_settings.h>
#include <sch_marker.h>
#include <sch_screen.h>
#include <schematic.h>
#include <schematic/schematic_types.pb.h>
#include <map>
#include <set>
#include <stdexcept>
#include <array>
#include <memory>
#include <limits>
#include <optional>
#include <vector>

namespace SCH_ERC_SETTINGS
{
using MESSAGE = kiapi::schematic::types::SchematicErcSettings;

// This is a read-only projection of the same policy and live exclusions that
// native saving persists. Do not call RecordERCExclusions here: its saved cache
// can lag a user's unsaved exclusion/comment edit.
inline MESSAGE Capture( SCHEMATIC& aSchematic )
{
    MESSAGE result;
    const ERC_SETTINGS& settings = aSchematic.ErcSettings();
    for( const RC_ITEM& item : ERC_ITEM::GetItemsWithSeverities() )
    {
        const auto entry = settings.m_ERCSeverities.find( item.GetErrorCode() );
        if( item.GetSettingsKey().empty() || entry == settings.m_ERCSeverities.end() )
            continue;
        auto* rule = result.add_rule_severities();
        rule->set_rule_type( ToProtoEnum<ERCE_T, kiapi::schematic::ErcErrorType>(
                static_cast<ERCE_T>( item.GetErrorCode() ) ) );
        // Match SeverityToString's exact persisted domain.
        rule->set_severity( entry->second == RPT_SEVERITY_WARNING ? kiapi::common::types::RS_WARNING
                : entry->second == RPT_SEVERITY_IGNORE ? kiapi::common::types::RS_IGNORE
                                                      : kiapi::common::types::RS_ERROR );
    }

    for( int first = 0; first < ELECTRICAL_PINTYPES_TOTAL; ++first )
    {
        for( int second = 0; second < ELECTRICAL_PINTYPES_TOTAL; ++second )
        {
            auto* cell = result.add_pin_map();
            cell->set_first( ToProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>(
                    static_cast<ELECTRICAL_PINTYPE>( first ) ) );
            cell->set_second( ToProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>(
                    static_cast<ELECTRICAL_PINTYPE>( second ) ) );
            switch( settings.GetPinMapValue( first, second ) )
            {
            case PIN_ERROR::OK: cell->set_conflict( kiapi::schematic::types::SEPC_NONE ); break;
            case PIN_ERROR::WARNING: cell->set_conflict( kiapi::schematic::types::SEPC_WARNING ); break;
            case PIN_ERROR::PP_ERROR: cell->set_conflict( kiapi::schematic::types::SEPC_ERROR ); break;
            case PIN_ERROR::UNCONNECTED: cell->set_conflict( kiapi::schematic::types::SEPC_UNCONNECTED ); break;
            default: throw std::runtime_error( "ERC pin policy contains an unsupported native value" );
            }
        }
    }

    std::set<SCH_SCREEN*> seen;
    std::map<std::string, kiapi::schematic::ErcExclusion> exclusions;
    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
    {
        SCH_SCREEN* screen = path.LastScreen();
        if( !screen || !seen.insert( screen ).second )
            continue;
        for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
        {
            const auto* marker = static_cast<SCH_MARKER*>( item );
            if( marker->IsExcluded() )
            {
                const ERC_EXCLUSION exclusion = ERC_EXCLUSION::FromMarker( *marker );
                exclusions.emplace( exclusion.GetSortKey(), exclusion.ToProto() );
            }
        }
    }
    for( const auto& [key, exclusion] : exclusions )
        result.add_exclusions()->CopyFrom( exclusion );
    return result;
}

/// The loaded screen that owns a marker rebuilt from its exact references: the main item's
/// sheet, then the sheet-specific path, then the screen holding the first item, else the root.
inline SCH_SCREEN* OwnerScreen( const kiapi::schematic::ErcMarker& aMarker, const SCH_SHEET_LIST& aHierarchy,
                                SCHEMATIC& aSchematic )
{
    auto screenOf = [&]( const kiapi::common::types::SheetPath& aPath ) -> SCH_SCREEN*
    {
        KIID_PATH ids;
        for( const auto& id : aPath.path() )
            ids.push_back( KIID( id.value() ) );
        std::optional<SCH_SHEET_PATH> path = aHierarchy.GetSheetPathByKIIDPath( ids, true );
        return path ? path->LastScreen() : nullptr;
    };

    SCH_SCREEN* screen = nullptr;
    if( aMarker.has_main_item_sheet_path() )
        screen = screenOf( aMarker.main_item_sheet_path() );
    else if( aMarker.has_sheet_specific_path() )
        screen = screenOf( aMarker.sheet_specific_path() );
    else if( aMarker.items_size() )
    {
        SCH_SHEET_PATH owner;
        aHierarchy.ResolveItem( KIID( aMarker.items( 0 ).value() ), &owner );
        screen = owner.LastScreen();
    }
    return screen ? screen : aSchematic.RootScreen();
}

struct EXCLUSION
{
    std::string key;
    std::string comment;
    std::unique_ptr<SCH_MARKER> marker;
    SCH_SCREEN* screen = nullptr;
};

struct PREPARED
{
    MESSAGE canonical;
    std::map<int, SEVERITY> severities;
    std::array<PIN_ERROR, ELECTRICAL_PINTYPES_TOTAL * ELECTRICAL_PINTYPES_TOTAL> pinMap;
    std::vector<EXCLUSION> exclusions;
};

// Decode into owned, detached objects first. Nothing in the live schematic or
// settings store is changed by a rejected candidate.
inline bool Prepare( const MESSAGE& aValue, SCHEMATIC& aSchematic, PREPARED& aPrepared,
                     std::string& aFailure )
{
    auto reject = [&]( const char* message ) { aFailure = message; return false; };
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
        return reject( "ERC settings contain unsupported fields" );

    PREPARED candidate;
    candidate.canonical = Capture( aSchematic );
    candidate.canonical.clear_exclusions();
    std::map<int, int> ruleCodes;
    for( const RC_ITEM& item : ERC_ITEM::GetItemsWithSeverities() )
        if( !item.GetSettingsKey().empty() && aSchematic.ErcSettings().m_ERCSeverities.count( item.GetErrorCode() ) )
            ruleCodes.emplace( ToProtoEnum<ERCE_T, kiapi::schematic::ErcErrorType>(
                    static_cast<ERCE_T>( item.GetErrorCode() ) ), item.GetErrorCode() );
    for( const auto& rule : aValue.rule_severities() )
    {
        const auto code = ruleCodes.find( rule.rule_type() );
        if( code == ruleCodes.end() || candidate.severities.count( code->second ) )
            return reject( "ERC rule identities must be known and unique" );
        SEVERITY severity;
        switch( rule.severity() )
        {
        case kiapi::common::types::RS_WARNING: severity = RPT_SEVERITY_WARNING; break;
        case kiapi::common::types::RS_ERROR: severity = RPT_SEVERITY_ERROR; break;
        case kiapi::common::types::RS_IGNORE: severity = RPT_SEVERITY_IGNORE; break;
        default: return reject( "ERC rules require warning, error or ignore severity" );
        }
        candidate.severities.emplace( code->second, severity );
    }
    if( candidate.severities.size() != ruleCodes.size() )
        return reject( "ERC replacement must include every persisted native rule" );
    for( auto& rule : *candidate.canonical.mutable_rule_severities() )
    {
        const auto severity = candidate.severities.at( ruleCodes.at( rule.rule_type() ) );
        rule.set_severity( severity == RPT_SEVERITY_WARNING ? kiapi::common::types::RS_WARNING
                : severity == RPT_SEVERITY_IGNORE ? kiapi::common::types::RS_IGNORE : kiapi::common::types::RS_ERROR );
    }

    std::map<int, int> pinTypes;
    for( int type = 0; type < ELECTRICAL_PINTYPES_TOTAL; ++type )
        pinTypes.emplace( ToProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>(
                static_cast<ELECTRICAL_PINTYPE>( type ) ), type );
    std::set<int> cells;
    for( const auto& cell : aValue.pin_map() )
    {
        const auto first = pinTypes.find( cell.first() ), second = pinTypes.find( cell.second() );
        if( first == pinTypes.end() || second == pinTypes.end() || cell.conflict() < 1 || cell.conflict() > 4 )
            return reject( "ERC pin rules contain an unknown pin type or interaction" );
        const int index = first->second * ELECTRICAL_PINTYPES_TOTAL + second->second;
        if( !cells.insert( index ).second )
            return reject( "ERC pin pairs must be unique" );
        candidate.pinMap[index] = static_cast<PIN_ERROR>( static_cast<int>( cell.conflict() ) - 1 );
        candidate.canonical.mutable_pin_map( index )->set_conflict( cell.conflict() );
    }
    if( cells.size() != candidate.pinMap.size() )
        return reject( "ERC replacement must include the complete pin matrix" );

    const SCH_SHEET_LIST hierarchy = aSchematic.Hierarchy();
    auto identifier = []( const std::string& value )
    {
        return value.size() == 36 && KIID::SniffTest( wxString::FromUTF8( value ) )
               && KIID( value ) != niluuid && KIID( value ).AsStdString() == value;
    };
    auto path = [&]( const kiapi::common::types::SheetPath& value ) -> std::optional<SCH_SHEET_PATH>
    {
        if( value.path_size() == 0 ) return std::nullopt;
        KIID_PATH ids;
        for( const auto& id : value.path() )
        {
            if( !identifier( id.value() ) ) return std::nullopt;
            ids.push_back( KIID( id.value() ) );
        }
        return hierarchy.GetSheetPathByKIIDPath( ids, true );
    };
    std::map<std::string, EXCLUSION> exclusions;
    for( const auto& exclusion : aValue.exclusions() )
    {
        if( !exclusion.has_marker() || exclusion.comment().find( '\0' ) != std::string::npos )
            return reject( "ERC exclusions require marker data and NUL-free comments" );
        const auto& packed = exclusion.marker();
        if( !kiapi::schematic::ErcErrorType_IsValid( packed.error_type() ) || packed.error_type() == 0
                || !packed.has_position() || packed.items_size() > 2 )
            return reject( "ERC marker kind, coordinates or item count are invalid" );
        for( int64_t coordinate : { packed.position().x_nm(), packed.position().y_nm() } )
            if( coordinate % 100 != 0 || coordinate / 100 < std::numeric_limits<int>::min()
                    || coordinate / 100 > std::numeric_limits<int>::max() )
                return reject( "ERC marker coordinates are not exactly representable" );
        for( const auto& id : packed.items() )
            if( !identifier( id.value() ) || !hierarchy.ResolveItem( KIID( id.value() ) ) )
                return reject( "ERC exclusion item identity does not resolve in this schematic" );
        if( ( packed.has_sheet_specific_path() && !path( packed.sheet_specific_path() ) )
                || ( packed.has_main_item_sheet_path() && !path( packed.main_item_sheet_path() ) )
                || ( packed.has_aux_item_sheet_path() && !path( packed.aux_item_sheet_path() ) )
                || ( packed.has_child() && packed.child().text_value().find( '\0' ) != std::string::npos ) )
            return reject( "ERC exclusion sheet or child reference is invalid" );

        std::unique_ptr<SCH_MARKER> marker( SCH_MARKER::FromProto( packed, hierarchy ) );
        if( !marker || ERC_EXCLUSION::FromMarker( *marker ).ToProto().marker().SerializeAsString()
                            != packed.SerializeAsString() )
            return reject( "ERC exclusion cannot be reconstructed without changing its references" );
        SCH_SCREEN* screen = OwnerScreen( packed, hierarchy, aSchematic );
        if( !screen ) return reject( "ERC exclusion has no loaded owner screen" );
        const std::string key = packed.SerializeAsString();
        if( !exclusions.emplace( key, EXCLUSION{ key, exclusion.comment(), std::move( marker ), screen } ).second )
            return reject( "ERC marker exclusions must be unique" );
    }
    for( auto& [key, exclusion] : exclusions )
    {
        auto* packed = candidate.canonical.add_exclusions();
        packed->mutable_marker()->ParseFromString( key );
        packed->set_comment( exclusion.comment );
        candidate.exclusions.push_back( std::move( exclusion ) );
    }
    aPrepared = std::move( candidate );
    return true;
}

inline void RestorePolicy( ERC_SETTINGS& aSettings, const MESSAGE& aValue )
{
    for( const auto& rule : aValue.rule_severities() )
        aSettings.SetSeverity( FromProtoEnum<ERCE_T, kiapi::schematic::ErcErrorType>( rule.rule_type() ),
                rule.severity() == kiapi::common::types::RS_WARNING ? RPT_SEVERITY_WARNING
                : rule.severity() == kiapi::common::types::RS_IGNORE ? RPT_SEVERITY_IGNORE : RPT_SEVERITY_ERROR );
    for( const auto& cell : aValue.pin_map() )
        aSettings.SetPinMapValue(
                FromProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>( cell.first() ),
                FromProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>( cell.second() ),
                static_cast<PIN_ERROR>( static_cast<int>( cell.conflict() ) - 1 ) );
}
}

#endif
