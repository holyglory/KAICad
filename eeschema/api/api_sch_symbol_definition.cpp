/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <api/api_sch_symbol_definition.h>
#include <api/api_sch_field_text_modes.h>
#include <api/api_sch_utils.h>
#include <api/api_utils.h>
#include <api/api_enums.h>
#include <lib_symbol.h>
#include <sch_symbol_cache_state.h>
#include <sch_shape.h>
#include <algorithm>
#include <limits>
#include <optional>
#include <set>

void PackSymbolDefinition( kiapi::schematic::types::SchematicSymbol& aOutput,
                           const LIB_SYMBOL& aLibrary, bool aIncludePins )
{
    using namespace kiapi::common;
    using namespace kiapi::schematic::types;
    aOutput.Clear();
    auto* def = &aOutput;
    PackLibId( def->mutable_id(), aLibrary.GetLibId() );
    def->set_pins_use_local_coordinates( aIncludePins );
    def->set_type( aLibrary.IsGlobalPower() ? SST_GLOBAL_POWER
                    : aLibrary.IsLocalPower() ? SST_LOCAL_POWER : SST_NORMAL );
    auto* defaults = def->mutable_attributes();
    defaults->set_exclude_from_simulation( aLibrary.GetExcludedFromSim() );
    defaults->set_exclude_from_bill_of_materials( aLibrary.GetExcludedFromBOM() );
    defaults->set_exclude_from_board( aLibrary.GetExcludedFromBoard() );
    defaults->set_exclude_from_position_files( aLibrary.GetExcludedFromPosFiles() );

    aLibrary.GetField( FIELD_T::REFERENCE )->Serialize( *def->mutable_reference_field(), schIUScale );
    aLibrary.GetField( FIELD_T::VALUE )->Serialize( *def->mutable_value_field(), schIUScale );
    aLibrary.GetField( FIELD_T::FOOTPRINT )->Serialize( *def->mutable_footprint_field(), schIUScale );
    aLibrary.GetField( FIELD_T::DATASHEET )->Serialize( *def->mutable_datasheet_field(), schIUScale );
    aLibrary.GetField( FIELD_T::DESCRIPTION )->Serialize( *def->mutable_description_field(), schIUScale );

    for( const SCH_ITEM& drawItem : aLibrary.GetDrawItems() )
    {
        if( drawItem.Type() == SCH_FIELD_T && static_cast<const SCH_FIELD&>( drawItem ).IsMandatory() )
            continue;

        // Placed pins are packed separately with their instance identities.
        if( !aIncludePins && drawItem.Type() == SCH_PIN_T )
            continue;

        SchematicSymbolChild* item = def->add_items();
        item->mutable_unit()->set_unit( drawItem.GetUnit() );
        item->mutable_body_style()->set_style( drawItem.GetBodyStyle() );
        item->set_is_private( drawItem.IsPrivate() );
        drawItem.Serialize( *item->mutable_item() );
    }

    def->set_unit_count( aLibrary.GetUnitCount() );
    def->set_demorgan_body_styles( aLibrary.HasDeMorganBodyStyles() );

    for( int bodyStyle = BODY_STYLE::BASE; bodyStyle <= aLibrary.GetBodyStyleCount(); ++bodyStyle )
    {
        def->add_body_style()->set_name(
                aLibrary.GetBodyStyleDescription( bodyStyle, false ).ToUTF8() );
    }

    def->set_keywords( aLibrary.GetKeyWords().ToUTF8() );

    for( const wxString& filter : aLibrary.GetFPFilters() )
        def->add_footprint_filters( filter.ToUTF8() );

    JumperSettings* jumpers = def->mutable_jumpers();
    jumpers->set_duplicate_names_are_jumpered( aLibrary.GetDuplicatePinNumbersAreJumpers() );

    for( const std::set<wxString>& group : aLibrary.JumperPinGroups() )
    {
        JumperGroup* jumperGroup = jumpers->add_groups();

        for( const wxString& pinNumber : group )
            jumperGroup->add_pin_numbers( pinNumber.ToUTF8() );
    }

    def->set_units_locked( aLibrary.UnitsLocked() );
    def->set_embedded_fonts( aLibrary.GetAreFontsEmbedded() );
    PackEmbeddedFiles( *def->mutable_embedded_files(), *aLibrary.GetEmbeddedFiles() );

    for( const auto& [unit, displayName] : aLibrary.GetUnitDisplayNames() )
    {
        SchematicUnitDisplayName* protoName = def->add_unit_display_names();
        protoName->set_unit( unit );
        protoName->set_name( displayName.ToUTF8() );
    }
    auto* maps = def->mutable_pin_maps();
    for( const PIN_MAP& map : aLibrary.GetPinMaps().GetAll() )
    {
        auto* packed = maps->add_pin_maps();
        packed->set_name( map.GetName().ToUTF8() );
        for( const PIN_MAP_ENTRY& entry : map.GetEntries() )
        {
            auto* item = packed->add_entries();
            item->set_pin_number( entry.m_PinNumber.ToUTF8() );
            item->set_pad_number( entry.m_PadNumber.ToUTF8() );
        }
    }
    for( const ASSOCIATED_FOOTPRINT& association : aLibrary.GetAssociatedFootprints() )
    {
        auto* packed = maps->add_associated_footprints();
        PackLibId( packed->mutable_footprint(), association.m_FootprintLibId );
        packed->set_map_name( association.m_MapName.ToUTF8() );
    }
}


bool PackCachedSymbols(
        google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol>& aOutput,
        const std::map<wxString, LIB_SYMBOL*>& aCache )
{
    google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol> candidate;
    for( const auto& [key, symbol] : aCache )
    {
        if( !symbol || !PackCachedSymbol( *candidate.Add(), key, *symbol ) )
            return false;
    }
    aOutput.Swap( &candidate );
    return true;
}


bool UnpackCachedSymbols(
        const google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol>& aInput,
        SCH_SYMBOL_CACHE_STATE& aOutput )
{
    SCH_SYMBOL_CACHE_STATE candidate;
    for( const auto& packed : aInput )
    {
        auto definition = UnpackCachedSymbol( packed );
        if( !definition || !candidate.Insert( wxString::FromUTF8( packed.cache_key() ),
                                              std::move( definition ) ) )
            return false;
    }
    aOutput = std::move( candidate );
    return true;
}


bool PackCachedSymbol( kiapi::schematic::types::SchematicCachedSymbol& aOutput,
                       const wxString& aCacheKey, const LIB_SYMBOL& aLibrary )
{
    if( aCacheKey.IsEmpty() || aCacheKey.find( wxChar( 0 ) ) != wxString::npos
            || !aLibrary.IsRoot() )
        return false;

    kiapi::schematic::types::SchematicCachedSymbol candidate;
    candidate.set_cache_key( aCacheKey.ToUTF8() );
    PackSymbolDefinition( *candidate.mutable_definition(), aLibrary );
    candidate.set_show_pin_names( aLibrary.GetShowPinNames() );
    candidate.set_show_pin_numbers( aLibrary.GetShowPinNumbers() );
    kiapi::common::PackDistance( *candidate.mutable_pin_name_offset(),
                                aLibrary.GetPinNameOffset(), schIUScale );
    aOutput.Swap( &candidate );
    return true;
}


std::unique_ptr<LIB_SYMBOL> UnpackCachedSymbol(
        const kiapi::schematic::types::SchematicCachedSymbol& aInput )
{
    auto known = aInput;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aInput.ByteSizeLong() )
        return nullptr;
    if( aInput.cache_key().empty() || aInput.cache_key().find( '\0' ) != std::string::npos
            || !aInput.has_definition()
            || !aInput.definition().pins_use_local_coordinates()
            || !aInput.has_pin_name_offset() )
        return nullptr;

    // Both identities must survive the native parser unchanged. In particular,
    // reject empty definitions and legacy slash escapes before a live cache
    // replacement can create a schematic that cannot be reopened faithfully.
    LIB_ID cacheId;
    const wxString cacheKey = wxString::FromUTF8( aInput.cache_key() );
    if( cacheId.Parse( cacheKey ) >= 0 || cacheId.GetLibItemName().empty()
            || cacheId.Format().wx_str() != cacheKey || cacheKey.Contains( wxS( "{slash}" ) ) )
        return nullptr;
    const LIB_ID definitionId = kiapi::common::UnpackLibId( aInput.definition().id() );
    LIB_ID parsedDefinitionId;
    if( definitionId.GetLibItemName().empty()
            || parsedDefinitionId.Parse( definitionId.Format() ) >= 0
            || parsedDefinitionId.GetLibNickname() != definitionId.GetLibNickname()
            || parsedDefinitionId.GetLibItemName() != definitionId.GetLibItemName() )
        return nullptr;

    const int64_t offset = aInput.pin_name_offset().value_nm();
    const int64_t quantum = schIUScale.IUToNm( 1 );
    if( offset < schIUScale.IUToNm( std::numeric_limits<int>::min() )
            || offset > schIUScale.IUToNm( std::numeric_limits<int>::max() )
            || offset % quantum != 0 )
        return nullptr;

    // A cache pin has no per-placement active-alternate state.
    std::unordered_map<KIID, wxString> alternates;
    auto candidate = UnpackSymbolDefinition( aInput.definition(), &alternates );
    if( !candidate || !alternates.empty() )
        return nullptr;

    candidate->SetShowPinNames( aInput.show_pin_names() );
    candidate->SetShowPinNumbers( aInput.show_pin_numbers() );
    candidate->SetPinNameOffset( static_cast<int>( offset / quantum ) );
    return candidate;
}


std::unique_ptr<LIB_SYMBOL> UnpackSymbolDefinition(
        const kiapi::schematic::types::SchematicSymbol& def,
        std::unordered_map<KIID, wxString>* aActiveAlternates )
{
    using namespace kiapi::common;
    using namespace kiapi::common::types;
    using namespace kiapi::schematic::types;
    if( !SchematicSymbolType_IsValid( def.type() ) || def.attributes().do_not_populate()
            || def.unit_count() > static_cast<uint32_t>( std::numeric_limits<int>::max() )
            || !SchematicFieldTextModesArePersistable( def ) )
        return nullptr;
    if( def.demorgan_body_styles()
            && ( def.body_style_size() != 2 || def.body_style( 0 ).name() != "Standard"
                 || def.body_style( 1 ).name() != "Alternate" ) )
        return nullptr;

    LIB_ID libId = UnpackLibId( def.id() );
    auto libSymbol = std::make_unique<LIB_SYMBOL>( libId.GetLibItemName() );
    libSymbol->SetLibId( libId );

    if( def.has_embedded_files()
            && UnpackEmbeddedFiles( def.embedded_files(), *libSymbol->GetEmbeddedFiles() ) )
        return nullptr;

    switch( def.type() )
    {
    case SST_GLOBAL_POWER: libSymbol->SetGlobalPower(); break;
    case SST_LOCAL_POWER:  libSymbol->SetLocalPower(); break;
    default:              libSymbol->SetNormal(); break;
    }

    if( def.has_attributes() )
    {
        libSymbol->SetExcludedFromSim( def.attributes().exclude_from_simulation() );
        libSymbol->SetExcludedFromBOM( def.attributes().exclude_from_bill_of_materials() );
        libSymbol->SetExcludedFromBoard( def.attributes().exclude_from_board() );
        libSymbol->SetExcludedFromPosFiles( def.attributes().exclude_from_position_files() );
    }


    libSymbol->GetField( FIELD_T::REFERENCE )->Deserialize( def.reference_field(), schIUScale );
    libSymbol->GetField( FIELD_T::VALUE )->Deserialize( def.value_field(), schIUScale );
    libSymbol->GetField( FIELD_T::FOOTPRINT )->Deserialize( def.footprint_field(), schIUScale );
    libSymbol->GetField( FIELD_T::DATASHEET )->Deserialize( def.datasheet_field(), schIUScale );
    libSymbol->GetField( FIELD_T::DESCRIPTION )->Deserialize( def.description_field(), schIUScale );

    std::unordered_map<::KIID, wxString> pinAltMap;
    std::set<std::string> childIdentities;

    for( const SchematicSymbolChild& child : def.items() )
    {
        // Zero identifies common graphics. Omitted legacy declarations use
        // the native single-unit, single-body-style default.
        if( child.unit().unit() < 0 || child.body_style().style() < 0
                || static_cast<uint32_t>( child.unit().unit() ) > std::max( 1u, def.unit_count() )
                || child.body_style().style() > std::max( 1, def.body_style_size() ) )
            return nullptr;

        std::optional<KICAD_T> type = TypeNameFromAny( child.item() );

        // Match the native library writer, not every schematic item accepted
        // by the general factory. Dropping an unknown child loses source data.
        if( !type || ( *type != SCH_SHAPE_T && *type != SCH_PIN_T
                       && *type != SCH_TEXT_T && *type != SCH_TEXTBOX_T
                       && *type != SCH_FIELD_T ) )
            return nullptr;

        // Legacy definitions may omit graphic UUIDs, but a supplied identity
        // must never be repaired into a random one by KIID's legacy fallback.
        // Fields have native name-based identity rather than a persisted UUID.
        auto readId = [&]( auto packed ) -> std::optional<std::string>
        {
            if( !child.item().UnpackTo( &packed ) )
                return std::nullopt;
            return packed.id().value();
        };
        std::optional<std::string> childId;
        switch( *type )
        {
        case SCH_SHAPE_T:   childId = readId( SchematicGraphicShape() ); break;
        case SCH_PIN_T:     childId = readId( SchematicPin() ); break;
        case SCH_TEXT_T:    childId = readId( SchematicText() ); break;
        case SCH_TEXTBOX_T: childId = readId( SchematicTextBox() ); break;
        case SCH_FIELD_T:   childId = std::string(); break;
        default: return nullptr;
        }
        if( !childId )
            return nullptr;
        if( *type == SCH_PIN_T )
        {
            SchematicPin pin;
            if( !child.item().UnpackTo( &pin ) || pin.has_library_pin_id() )
                return nullptr; // A definition decoder accepts owned pins only.
        }
        if( !childId->empty() )
        {
            if( !::KIID::SniffTest( wxString::FromUTF8( *childId ) ) )
                return nullptr;
            ::KIID identity( *childId );
            if( identity == niluuid || identity.AsStdString() != *childId
                    || !childIdentities.insert( *childId ).second )
                return nullptr;
        }

        std::unique_ptr<EDA_ITEM> item = CreateItemForType( *type, libSymbol.get() );

        if( !item || !item->Deserialize( child.item() ) )
            return nullptr;

        if( *type == SCH_SHAPE_T )
        {
            switch( static_cast<SCH_SHAPE*>( item.get() )->GetShape() )
            {
            case SHAPE_T::ARC:
            case SHAPE_T::CIRCLE:
            case SHAPE_T::RECTANGLE:
            case SHAPE_T::BEZIER:
            case SHAPE_T::POLY:
            case SHAPE_T::ELLIPSE:
            case SHAPE_T::ELLIPSE_ARC:
                break;
            default:
                return nullptr;
            }
        }

        SCH_ITEM* schItem = static_cast<SCH_ITEM*>( item.release() );

        if( schItem->Type() == SCH_PIN_T )
        {
            SchematicPin pinProto;

            if( child.item().UnpackTo( &pinProto ) )
            {
                if( pinProto.has_active_alternate() )
                    pinAltMap[schItem->m_Uuid] = wxString::FromUTF8( pinProto.active_alternate() );
            }
        }

        if( child.has_unit() )
            schItem->SetUnit( child.unit().unit() );

        if( child.has_body_style() )
            schItem->SetBodyStyle( child.body_style().style() );

        schItem->SetLayer( LAYER_DEVICE );
        schItem->SetPrivate( child.is_private() );
        libSymbol->AddDrawItem( schItem );
    }

    if( def.unit_count() > 0 )
        libSymbol->SetUnitCount( def.unit_count(), false );

    if( def.body_style_size() > 0 )
    {
        std::vector<wxString> bodyStyleNames;

        for( const SchematicBodyStyle& bodyStyle : def.body_style() )
            bodyStyleNames.emplace_back( wxString::FromUTF8( bodyStyle.name() ) );

        // A single native body style has no persisted name. Its display
        // description is "?"; storing that placeholder as an explicit name
        // makes an otherwise unchanged definition differ from its cache.
        if( def.demorgan_body_styles()
            || ( bodyStyleNames.size() == 1 && bodyStyleNames.front() == wxS( "?" ) ) )
            libSymbol->SetBodyStyleNames( {} );
        else
            libSymbol->SetBodyStyleNames( bodyStyleNames );
        libSymbol->SetBodyStyleCount( static_cast<int>( bodyStyleNames.size() ), false, false );
    }
    libSymbol->SetHasDeMorganBodyStyles( def.demorgan_body_styles() );

    if( !def.keywords().empty() )
        libSymbol->SetKeyWords( wxString::FromUTF8( def.keywords() ) );

    if( def.footprint_filters_size() > 0 )
    {
        wxArrayString filters;

        for( const std::string& filter : def.footprint_filters() )
            filters.Add( wxString::FromUTF8( filter ) );

        libSymbol->SetFPFilters( filters );
    }

    libSymbol->SetDuplicatePinNumbersAreJumpers( def.jumpers().duplicate_names_are_jumpered() );

    for( const JumperGroup& group : def.jumpers().groups() )
    {
        std::set<wxString> pinNumbers;

        for( const std::string& pinNumber : group.pin_numbers() )
            pinNumbers.insert( wxString::FromUTF8( pinNumber ) );

        if( !pinNumbers.empty() )
            libSymbol->JumperPinGroups().push_back( std::move( pinNumbers ) );
    }

    libSymbol->LockUnits( def.units_locked() );
    libSymbol->SetAreFontsEmbedded( def.embedded_fonts() );

    for( const SchematicUnitDisplayName& displayName : def.unit_display_names() )
    {
        // Unit zero is the native common-graphics unit. Never truncate an
        // out-of-range declaration or silently overwrite a duplicate name.
        if( displayName.unit() < 0
                || static_cast<uint32_t>( displayName.unit() ) > std::max( 1u, def.unit_count() )
                || !libSymbol->GetUnitDisplayNames().emplace(
                        displayName.unit(), wxString::FromUTF8( displayName.name() ) ).second )
            return nullptr;
    }

    if( def.has_pin_maps() )
    {
        PIN_MAP_SET pinMapSet;
        std::set<std::string> mapNames;

        for( const PinMap& map : def.pin_maps().pin_maps() )
        {
            if( !mapNames.insert( map.name() ).second )
                return nullptr;

            PIN_MAP pinMap( wxString::FromUTF8( map.name() ) );
            std::set<std::string> mappedPins;

            for( const PinMapEntry& entry : map.entries() )
            {
                if( !mappedPins.insert( entry.pin_number() ).second )
                    return nullptr;

                pinMap.SetEntry( wxString::FromUTF8( entry.pin_number() ),
                                 wxString::FromUTF8( entry.pad_number() ) );
            }

            pinMapSet.AddOrReplace( std::move( pinMap ) );
        }

        std::vector<ASSOCIATED_FOOTPRINT> associatedFootprints;

        for( const AssociatedFootprint& footprint : def.pin_maps().associated_footprints() )
        {
            ASSOCIATED_FOOTPRINT assoc;
            assoc.m_FootprintLibId = UnpackLibId( footprint.footprint() );
            assoc.m_MapName        = wxString::FromUTF8( footprint.map_name() );
            associatedFootprints.push_back( std::move( assoc ) );
        }

        libSymbol->SetPinMaps( pinMapSet );
        libSymbol->SetAssociatedFootprints( std::move( associatedFootprints ) );
    }

    if( aActiveAlternates )
        *aActiveAlternates = std::move( pinAltMap );
    return libSymbol;
}
