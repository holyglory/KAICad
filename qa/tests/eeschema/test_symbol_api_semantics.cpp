/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <boost/test/unit_test.hpp>
#include <google/protobuf/any.pb.h>
#include <lib_symbol.h>
#include <sch_symbol.h>
#include <sch_symbol_cache_state.h>
#include <sch_screen.h>
#include <sch_shape.h>
#include <embedded_files.h>
#include <mmh3_hash.h>
#include <api/api_utils.h>
#include <api/api_sch_symbol_definition.h>
#include <api/api_sch_utils.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr_lib_cache.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr_parser.h>
#include <schematic.h>
#include <sch_sheet.h>
#include <richio.h>
#include <schematic/schematic_types.pb.h>
#include <wx/filename.h>
#include <set>
#include <vector>
#include <limits>
#include <utility>
#include <schematic_utils/schematic_file_util.h>
#include <settings/settings_manager.h>
#include <locale_io.h>
#include <reporter.h>
#include <connection_graph.h>
#include <sstream>

namespace
{
using namespace kiapi::schematic::types;

LIB_SYMBOL MakeLibrary( SchematicSymbolType type, int mask )
{
    LIB_SYMBOL library( wxS( "PowerTraits" ) );
    library.SetLibId( LIB_ID( wxS( "Automation" ), wxS( "PowerTraits" ) ) );
    if( type == SST_GLOBAL_POWER ) library.SetGlobalPower();
    else if( type == SST_LOCAL_POWER ) library.SetLocalPower();
    else library.SetNormal();
    library.SetExcludedFromSim( ( mask & 1 ) != 0 );
    library.SetExcludedFromBOM( ( mask & 2 ) != 0 );
    library.SetExcludedFromBoard( ( mask & 4 ) != 0 );
    library.SetExcludedFromPosFiles( ( mask & 8 ) != 0 );
    return library;
}

void CheckTraits( const LIB_SYMBOL& library, SchematicSymbolType type, int mask )
{
    BOOST_CHECK_EQUAL( library.IsGlobalPower(), type == SST_GLOBAL_POWER );
    BOOST_CHECK_EQUAL( library.IsLocalPower(), type == SST_LOCAL_POWER );
    BOOST_CHECK_EQUAL( library.IsNormal(), type == SST_NORMAL );
    BOOST_CHECK_EQUAL( library.GetExcludedFromSim(), ( mask & 1 ) != 0 );
    BOOST_CHECK_EQUAL( library.GetExcludedFromBOM(), ( mask & 2 ) != 0 );
    BOOST_CHECK_EQUAL( library.GetExcludedFromBoard(), ( mask & 4 ) != 0 );
    BOOST_CHECK_EQUAL( library.GetExcludedFromPosFiles(), ( mask & 8 ) != 0 );
}

google::protobuf::Any Pack( SCH_SYMBOL& symbol )
{
    google::protobuf::Any result;
    symbol.Serialize( result );
    return result;
}

struct TEMP_LIBRARY
{
    wxString path = wxFileName::CreateTempFileName( wxS( "kicad_symbol_traits_" ) );
    TEMP_LIBRARY() { wxRemoveFile( path ); }
    ~TEMP_LIBRARY() { wxRemoveFile( path ); }
};
}

BOOST_AUTO_TEST_SUITE( SymbolApiSemantics )

BOOST_AUTO_TEST_CASE( FieldCustomPropertiesSurviveDecodeAndLibraryReload )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    library.GetValueField().SetCustomProperty( wxS( "automation.source" ), wxS( "datasheet:page-2" ) );
    auto* user = new SCH_FIELD( &library, FIELD_T::USER, wxS( "Manufacturer" ) );
    user->SetText( wxS( "Fixture" ) );
    user->SetCustomProperty( wxS( "automation.guidance" ), wxS( "Keep this instruction" ) );
    library.AddField( user );
    SchematicCachedSymbol packed;
    BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:PowerTraits" ), library ) );
    auto restored = UnpackCachedSymbol( packed );
    BOOST_REQUIRE( restored );
    BOOST_CHECK( restored->GetValueField().GetCustomProperties() == library.GetValueField().GetCustomProperties() );
    BOOST_REQUIRE( restored->GetField( wxS( "Manufacturer" ) ) );
    BOOST_CHECK( restored->GetField( wxS( "Manufacturer" ) )->GetCustomProperties() == user->GetCustomProperties() );
    SchematicCachedSymbol again;
    BOOST_REQUIRE( PackCachedSymbol( again, wxS( "Automation:PowerTraits" ), *restored ) );
    BOOST_CHECK_EQUAL( packed.SerializeAsString(), again.SerializeAsString() );

    TEMP_LIBRARY file;
    {
        SCH_IO_KICAD_SEXPR writer;
        writer.CreateLibrary( file.path );
        writer.SaveSymbol( file.path, new LIB_SYMBOL( *restored ) );
        writer.SaveLibrary( file.path );
    }
    SCH_IO_KICAD_SEXPR reader;
    auto* loaded = reader.LoadSymbol( file.path, wxS( "PowerTraits" ) );
    BOOST_REQUIRE( loaded );
    BOOST_CHECK( loaded->GetValueField().GetCustomProperties() == library.GetValueField().GetCustomProperties() );
    BOOST_REQUIRE( loaded->GetField( wxS( "Manufacturer" ) ) );
    BOOST_CHECK( loaded->GetField( wxS( "Manufacturer" ) )->GetCustomProperties() == user->GetCustomProperties() );

    SCH_FIELD assigned( &library, FIELD_T::USER, wxS( "Copied" ) );
    assigned.SetCustomProperty( wxS( "stale" ), wxS( "must disappear" ) );
    assigned = library.GetValueField();
    BOOST_CHECK( assigned.GetCustomProperties() == library.GetValueField().GetCustomProperties() );
    assigned.operator=( assigned );
    BOOST_CHECK( assigned.GetCustomProperties() == library.GetValueField().GetCustomProperties() );

    // A complete replacement must clear metadata that is no longer present.
    SchematicField empty;
    restored->GetValueField().Serialize( empty, schIUScale );
    empty.clear_custom_properties();
    BOOST_REQUIRE( restored->GetValueField().Deserialize( empty, schIUScale ) );
    BOOST_CHECK( !restored->GetValueField().HasCustomProperties() );
    assigned = restored->GetValueField();
    BOOST_CHECK( !assigned.HasCustomProperties() );
}

BOOST_AUTO_TEST_CASE( ImplicitAndNamedBodyStylesRetainNativeCacheIdentity )
{
    for( int mode = 0; mode < 3; ++mode )
    {
        LIB_SYMBOL library( "BodyStyles" );
        library.SetLibId( LIB_ID( "Automation", "BodyStyles" ) );
        if( mode == 1 ) library.SetHasDeMorganBodyStyles( true );
        if( mode == 2 ) library.SetBodyStyleNames( { "Compact", "Detailed" } );
        SchematicSymbol definition;
        PackSymbolDefinition( definition, library );
        auto restored = UnpackSymbolDefinition( definition );
        BOOST_REQUIRE( restored );
        BOOST_CHECK( restored->GetBodyStyleNames() == library.GetBodyStyleNames() );
        BOOST_CHECK_EQUAL( restored->HasDeMorganBodyStyles(), library.HasDeMorganBodyStyles() );
        BOOST_CHECK_EQUAL( restored->GetBodyStyleCount(), library.GetBodyStyleCount() );
        BOOST_CHECK( library == *restored );
        SchematicSymbol repacked; PackSymbolDefinition( repacked, *restored );
        BOOST_CHECK_EQUAL( definition.SerializeAsString(), repacked.SerializeAsString() );
    }
}

BOOST_AUTO_TEST_CASE( LoadedSymbolDefinitionRemainsEqualThroughPlacementOnlyUpdate )
{
    LOCALE_IO locale;
    SETTINGS_MANAGER settings;
    std::unique_ptr<SCHEMATIC> schematic;
    KI_TEST::LoadSchematic( settings, "net_chains_four_nets", schematic );
    for( int pass = 0; pass < 2; ++pass )
    {
        if( pass != 0 )
        {
            const KIID rootInstance = schematic->GetTopLevelSheet( 0 )->m_Uuid;
            STRING_FORMATTER formatter;
            SCH_IO_KICAD_SEXPR writer;
            writer.FormatSchematicToFormatter( &formatter, schematic->GetTopLevelSheet( 0 ), schematic.get() );
            std::istringstream input( formatter.GetString() );
            auto reopened = KI_TEST::ReadSchematicFromStream( input, &schematic->Project() );
            BOOST_REQUIRE( reopened );
            // The stream fixture has no project manifest to supply its root
            // instance identity. Reuse the explicitly saved instance, not the
            // separately persisted screen UUID or a generated replacement.
            const_cast<KIID&>( reopened->GetTopLevelSheet( 0 )->m_Uuid ) = rootInstance;
            reopened->RefreshHierarchy();
            schematic = std::move( reopened );
        }
        for( const SCH_SHEET_PATH& path : schematic->Hierarchy() )
        {
          std::vector<SCH_SYMBOL*> symbols;
          for( SCH_ITEM* item : path.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
              symbols.push_back( static_cast<SCH_SYMBOL*>( item ) );
          for( SCH_SYMBOL* symbol : symbols )
          {
            google::protobuf::Any packed;
            SchematicSymbolInstance message;
            BOOST_REQUIRE( PackSymbol( &message, symbol, path ) );
            const LIB_ID originalDefinitionId = symbol->GetLibSymbolRef()->GetLibId();
            const wxString originalCacheKey = symbol->GetSchSymbolLibraryName();
            auto describeCache = [&]( const char* phase )
            {
                auto cached = path.LastScreen()->GetLibSymbols().find( originalCacheKey );
                if( cached == path.LastScreen()->GetLibSymbols().end() ) return;
                WX_STRING_REPORTER report;
                if( cached->second->Compare( *symbol->GetLibSymbolRef(), ~SCH_ITEM::COMPARE_FLAGS::UUID, &report ) )
                    BOOST_TEST_MESSAGE( std::string( phase ) + " cache/placement mismatch "
                        + symbol->GetRef( &path ).ToStdString() + ": " + report.GetMessages().ToStdString() );
            };
            describeCache( "Before update" );
            message.set_passthrough( SPM_BLOCK );
            packed.PackFrom( message );
            SCH_SYMBOL decoded;
            BOOST_REQUIRE( decoded.Deserialize( packed ) );
            BOOST_CHECK( symbol->GetLibSymbolRef()->GetBodyStyleNames() == decoded.GetLibSymbolRef()->GetBodyStyleNames() );
            WX_STRING_REPORTER differences;
            symbol->GetLibSymbolRef()->Compare( *decoded.GetLibSymbolRef(), ~SCH_ITEM::COMPARE_FLAGS::UUID, &differences );
            const int comparison = symbol->GetLibSymbolRef()->Compare( *decoded.GetLibSymbolRef(), ~SCH_ITEM::COMPARE_FLAGS::UUID );
            BOOST_CHECK_MESSAGE( comparison == 0, symbol->GetRef( &path ).ToStdString()
                    + ": " + differences.GetMessages().ToStdString() );
            // Follow the real update path through the screen cache, not only
            // the decoder. Old graph pin addresses must not outlive their owner.
            auto* graph = schematic->ConnectionGraph();
            graph->RemoveItem( symbol );
            for( SCH_PIN* pin : symbol->GetPins() ) graph->RemoveItem( pin );
            symbol->SwapItemData( &decoded );
            ApplySymbolInstance( symbol, message, path, schematic.get() );
            describeCache( "After swap" );
            path.LastScreen()->Update( symbol );
            BOOST_CHECK_MESSAGE( symbol->GetLibSymbolRef()->GetLibId() == originalDefinitionId,
                                 "Placement-only update changed the embedded definition ID" );
            BOOST_CHECK_MESSAGE( symbol->GetSchSymbolLibraryName() == originalCacheKey,
                                 "Placement-only update allocated an unrelated cache alias" );
          }
        }
        schematic->ConnectionGraph()->Recalculate( schematic->Hierarchy(), true );
    }
}

BOOST_AUTO_TEST_CASE( PowerCategoryAndLibraryDefaultsSurviveNativeApiRoundTrip )
{
    for( auto type : { SST_NORMAL, SST_GLOBAL_POWER, SST_LOCAL_POWER } )
    {
        for( int mask = 0; mask < 16; ++mask )
        {
            auto library = MakeLibrary( type, mask );
            SCH_SYMBOL source;
            source.SetLibId( library.GetLibId() );
            source.SetLibSymbol( new LIB_SYMBOL( library ) );
            auto packed = Pack( source );
            SchematicSymbolInstance message;
            BOOST_REQUIRE( packed.UnpackTo( &message ) );
            BOOST_CHECK_EQUAL( message.definition().type(), type );
            BOOST_REQUIRE( message.definition().has_attributes() );
            SCH_SYMBOL restored;
            BOOST_REQUIRE( restored.Deserialize( packed ) );
            CheckTraits( *restored.GetLibSymbolRef(), type, mask );
        }
    }
}

BOOST_AUTO_TEST_CASE( ReconstructedPowerCategoryAndDefaultsRemainSaveable )
{
    for( auto type : { SST_NORMAL, SST_GLOBAL_POWER, SST_LOCAL_POWER } )
    {
        auto library = MakeLibrary( type, 15 );
        SCH_SYMBOL source;
        source.SetLibId( library.GetLibId() );
        source.SetLibSymbol( new LIB_SYMBOL( library ) );
        SCH_SYMBOL restored;
        BOOST_REQUIRE( restored.Deserialize( Pack( source ) ) );
        TEMP_LIBRARY file;
        {
            SCH_IO_KICAD_SEXPR writer;
            writer.CreateLibrary( file.path );
            writer.SaveSymbol( file.path, new LIB_SYMBOL( *restored.GetLibSymbolRef() ) );
            writer.SaveLibrary( file.path );
        }
        SCH_IO_KICAD_SEXPR reader;
        LIB_SYMBOL* loaded = reader.LoadSymbol( file.path, wxS( "PowerTraits" ) );
        BOOST_REQUIRE( loaded );
        CheckTraits( *loaded, type, 15 );
    }
}

BOOST_AUTO_TEST_CASE( UnknownPowerTypeAndInvalidEnumsAreRejectedBeforeMutation )
{
    auto library = MakeLibrary( SST_GLOBAL_POWER, 15 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    for( int type : { 0, 999 } )
    {
        SchematicSymbolInstance invalid;
        BOOST_REQUIRE( before.UnpackTo( &invalid ) );
        invalid.mutable_id()->set_value( KIID().AsStdString() );
        invalid.mutable_definition()->set_type( static_cast<SchematicSymbolType>( type ) );
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }
}

BOOST_AUTO_TEST_CASE( LegacyUntypedNormalDefinitionsRemainAccepted )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL source;
    source.SetLibId( library.GetLibId() );
    source.SetLibSymbol( new LIB_SYMBOL( library ) );
    SchematicSymbolInstance legacy;
    BOOST_REQUIRE( Pack( source ).UnpackTo( &legacy ) );
    legacy.mutable_definition()->set_type( SST_UNKNOWN );
    google::protobuf::Any input;
    input.PackFrom( legacy );
    SCH_SYMBOL restored;
    BOOST_REQUIRE( restored.Deserialize( input ) );
    CheckTraits( *restored.GetLibSymbolRef(), SST_NORMAL, 0 );
}

BOOST_AUTO_TEST_CASE( InstanceOnlyDnpCannotBeSilentlyStoredAsALibraryDefault )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SchematicSymbolInstance invalid;
    BOOST_REQUIRE( before.UnpackTo( &invalid ) );
    invalid.mutable_definition()->mutable_attributes()->set_do_not_populate( true );
    invalid.mutable_position()->set_x_nm( 987654 );
    google::protobuf::Any input;
    input.PackFrom( invalid );
    BOOST_CHECK( !symbol.Deserialize( input ) );
    BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
}

BOOST_AUTO_TEST_CASE( UnsupportedOrMalformedChildrenRejectWithoutPartialMutation )
{
    auto library = MakeLibrary( SST_GLOBAL_POWER, 15 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    std::vector<google::protobuf::Any> rejected;
    google::protobuf::Any unknown;
    unknown.set_type_url( "type.googleapis.com/future.SymbolGraphic" );
    rejected.push_back( unknown );
    google::protobuf::Any malformed;
    malformed.set_type_url( "type.googleapis.com/kiapi.schematic.types.SchematicText" );
    malformed.set_value( std::string( 1, '\xff' ) );
    rejected.push_back( malformed );
    google::protobuf::Any sheet;
    sheet.PackFrom( SheetSymbol() );
    rejected.push_back( sheet );
    SCH_SHAPE segment( SHAPE_T::SEGMENT );
    google::protobuf::Any unsaveable;
    segment.Serialize( unsaveable );
    rejected.push_back( unsaveable );

    for( const auto& child : rejected )
    {
        SchematicSymbolInstance invalid;
        BOOST_REQUIRE( before.UnpackTo( &invalid ) );
        invalid.mutable_id()->set_value( KIID().AsStdString() );
        invalid.mutable_position()->set_x_nm( 1234567 );
        // A decoded valid sibling must not survive a later rejection either.
        SCH_SHAPE circle( SHAPE_T::CIRCLE );
        circle.Serialize( *invalid.mutable_definition()->add_items()->mutable_item() );
        *invalid.mutable_definition()->add_items()->mutable_item() = child;
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }
}

BOOST_AUTO_TEST_CASE( OwnedEmbeddedAssetsRoundTripAndRejectCorruption )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    auto file = std::make_shared<EMBEDDED_FILES::EMBEDDED_FILE>();
    file->name = wxS( "part-notes.txt" );
    file->type = EMBEDDED_FILES::EMBEDDED_FILE::FILE_TYPE::DATASHEET;
    std::string contents = "Library-owned part notes, not a document attachment.";
    file->decompressedData.assign( contents.begin(), contents.end() );
    MMH3_HASH hash( EMBEDDED_FILES::Seed() );
    hash.add( file->decompressedData );
    file->data_hash = hash.digest().ToString();
    BOOST_REQUIRE( EMBEDDED_FILES::CompressAndEncode( *file ) == EMBEDDED_FILES::RETURN_CODE::OK );
    library.GetEmbeddedFiles()->AddFile( file );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SchematicSymbolInstance message;
    BOOST_REQUIRE( before.UnpackTo( &message ) );
    BOOST_REQUIRE_EQUAL( message.definition().embedded_files().files_size(), 1 );
    SCH_SYMBOL restored;
    BOOST_REQUIRE( restored.Deserialize( before ) );
    BOOST_CHECK_EQUAL( Pack( restored ).SerializeAsString(), before.SerializeAsString() );
    auto loadedAsset = restored.GetLibSymbolRef()->GetEmbeddedFiles()->EmbeddedFileMap().at( file->name );
    BOOST_CHECK_EQUAL_COLLECTIONS( loadedAsset->decompressedData.begin(), loadedAsset->decompressedData.end(),
                                  contents.begin(), contents.end() );

    TEMP_LIBRARY saved;
    {
        SCH_IO_KICAD_SEXPR writer;
        writer.CreateLibrary( saved.path );
        writer.SaveSymbol( saved.path, new LIB_SYMBOL( *restored.GetLibSymbolRef() ) );
        writer.SaveLibrary( saved.path );
    }
    SCH_IO_KICAD_SEXPR reader;
    auto* loaded = reader.LoadSymbol( saved.path, wxS( "PowerTraits" ) );
    BOOST_REQUIRE( loaded );
    kiapi::common::types::EmbeddedFiles savedAssets;
    kiapi::common::PackEmbeddedFiles( savedAssets, *loaded->GetEmbeddedFiles() );
    BOOST_CHECK_EQUAL( savedAssets.SerializeAsString(), message.definition().embedded_files().SerializeAsString() );

    for( int corruption = 0; corruption < 6; ++corruption )
    {
        auto invalid = message;
        invalid.mutable_position()->set_x_nm( 8765432 );
        auto* assets = invalid.mutable_definition()->mutable_embedded_files();
        switch( corruption )
        {
        case 0: invalid.mutable_definition()->clear_embedded_files(); break;
        case 1: assets->mutable_files( 0 )->set_data_hash( "bad checksum" ); break;
        case 2: assets->mutable_files( 0 )->set_data( "invalid compressed payload" ); break;
        case 3: assets->mutable_files( 0 )->set_name( "../part.txt" ); break;
        case 4: assets->mutable_files( 0 )->set_type( kiapi::common::types::EFT_UNKNOWN ); break;
        case 5: *assets->add_files() = message.definition().embedded_files().files( 0 ); break;
        }
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }
    message.mutable_definition()->mutable_embedded_files()->clear_files();
    google::protobuf::Any clear;
    clear.PackFrom( message );
    BOOST_REQUIRE( symbol.Deserialize( clear ) );
    BOOST_CHECK( symbol.GetLibSymbolRef()->GetEmbeddedFiles()->EmbeddedFileMap().empty() );

    EMBEDDED_FILES destination;
    destination.SetAreFontsEmbedded( true );
    int notifications = 0;
    destination.SetFileAddedCallback( [&]( EMBEDDED_FILES::EMBEDDED_FILE* ) { ++notifications; } );
    BOOST_REQUIRE( !kiapi::common::UnpackEmbeddedFiles( savedAssets, destination ) );
    BOOST_CHECK( destination.GetAreFontsEmbedded() );
    BOOST_CHECK_EQUAL( notifications, 0 );
    auto additional = std::make_shared<EMBEDDED_FILES::EMBEDDED_FILE>( *file );
    additional->name = wxS( "another-note.txt" );
    destination.AddFile( additional );
    BOOST_CHECK_EQUAL( notifications, 1 );
}

BOOST_AUTO_TEST_CASE( ExplicitChildIdentitiesCannotBeRepairedOrDuplicated )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SCH_SHAPE circle( SHAPE_T::CIRCLE );
    google::protobuf::Any shapeAny;
    circle.Serialize( shapeAny );
    SchematicGraphicShape shape;
    BOOST_REQUIRE( shapeAny.UnpackTo( &shape ) );
    for( std::string id : { std::string( "not-a-uuid" ), niluuid.AsStdString() } )
    {
        SchematicSymbolInstance invalid;
        BOOST_REQUIRE( before.UnpackTo( &invalid ) );
        invalid.mutable_position()->set_x_nm( 7654321 );
        auto corrupt = shape;
        corrupt.mutable_id()->set_value( id );
        invalid.mutable_definition()->add_items()->mutable_item()->PackFrom( corrupt );
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }
    SchematicSymbolInstance duplicate;
    BOOST_REQUIRE( before.UnpackTo( &duplicate ) );
    for( int i = 0; i < 2; ++i )
        *duplicate.mutable_definition()->add_items()->mutable_item() = shapeAny;
    google::protobuf::Any input;
    input.PackFrom( duplicate );
    BOOST_CHECK( !symbol.Deserialize( input ) );
    BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );

    // Preserve legacy missing-ID loading. Identical new graphics receive
    // distinct native identities, which subsequent snapshots must retain.
    for( auto& child : *duplicate.mutable_definition()->mutable_items() )
    {
        auto legacy = shape;
        legacy.clear_id();
        child.mutable_item()->PackFrom( legacy );
    }
    input.PackFrom( duplicate );
    SCH_SYMBOL restored;
    BOOST_REQUIRE( restored.Deserialize( input ) );
    SchematicSymbolInstance observed;
    BOOST_REQUIRE( Pack( restored ).UnpackTo( &observed ) );
    std::set<std::string> ids;
    for( const auto& child : observed.definition().items() )
    {
        SchematicGraphicShape graphic;
        BOOST_REQUIRE( child.item().UnpackTo( &graphic ) );
        BOOST_CHECK( KIID( graphic.id().value() ) != niluuid );
        BOOST_CHECK( ids.insert( graphic.id().value() ).second );
    }
    BOOST_CHECK_EQUAL( ids.size(), 2 );
}

BOOST_AUTO_TEST_CASE( ChildUnitsAndBodyStylesMustMatchTheDefinition )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SchematicSymbolInstance original;
    BOOST_REQUIRE( before.UnpackTo( &original ) );
    SCH_SHAPE circle( SHAPE_T::CIRCLE );
    circle.Serialize( *original.mutable_definition()->add_items()->mutable_item() );

    for( auto [unit, style] : std::vector<std::pair<int, int>>{ {-1, 0}, {2, 0}, {0, -1}, {0, 2} } )
    {
        auto invalid = original;
        invalid.mutable_position()->set_x_nm( 7654321 );
        auto* child = invalid.mutable_definition()->mutable_items( 0 );
        child->mutable_unit()->set_unit( unit );
        child->mutable_body_style()->set_style( style );
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }
    auto overflowing = original;
    overflowing.mutable_definition()->set_unit_count( std::numeric_limits<uint32_t>::max() );
    google::protobuf::Any input;
    input.PackFrom( overflowing );
    BOOST_CHECK( !symbol.Deserialize( input ) );
    BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );

    for( auto [unit, style] : std::vector<std::pair<int, int>>{ {0, 0}, {0, 1}, {1, 0}, {1, 1}, {2, 2} } )
    {
        auto valid = original;
        valid.mutable_definition()->set_unit_count( 2 );
        valid.mutable_definition()->add_body_style()->set_name( "Alternate" );
        auto* child = valid.mutable_definition()->mutable_items( 0 );
        child->mutable_unit()->set_unit( unit );
        child->mutable_body_style()->set_style( style );
        input.PackFrom( valid );
        SCH_SYMBOL restored;
        BOOST_REQUIRE( restored.Deserialize( input ) );
        SchematicSymbolInstance observed;
        BOOST_REQUIRE( Pack( restored ).UnpackTo( &observed ) );
        BOOST_REQUIRE_EQUAL( observed.definition().items_size(), 1 );
        BOOST_CHECK_EQUAL( observed.definition().items( 0 ).unit().unit(), unit );
        BOOST_CHECK_EQUAL( observed.definition().items( 0 ).body_style().style(), style );
    }
}

BOOST_AUTO_TEST_CASE( UnitDisplayNamesRejectLossyDeclarationsBeforeMutation )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SchematicSymbolInstance original;
    BOOST_REQUIRE( before.UnpackTo( &original ) );

    for( int unit : { -1, 2 } )
    {
        auto invalid = original;
        invalid.mutable_position()->set_x_nm( 4567890 );
        auto* name = invalid.mutable_definition()->add_unit_display_names();
        name->set_unit( unit ); name->set_name( "Invalid unit" );
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }

    for( const char* secondName : { "First", "Conflicting" } )
    {
        auto invalid = original;
        for( const char* text : { "First", secondName } )
        {
            auto* name = invalid.mutable_definition()->add_unit_display_names();
            name->set_unit( 1 ); name->set_name( text );
        }
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }

    auto valid = original;
    valid.mutable_definition()->set_unit_count( 2 );
    for( int unit : { 0, 1, 2 } )
    {
        auto* name = valid.mutable_definition()->add_unit_display_names();
        name->set_unit( unit ); name->set_name( "Unit " + std::to_string( unit ) );
        SCH_SHAPE circle( SHAPE_T::CIRCLE );
        auto* graphic = valid.mutable_definition()->add_items();
        graphic->mutable_unit()->set_unit( unit );
        graphic->mutable_body_style()->set_style( 1 );
        circle.Serialize( *graphic->mutable_item() );
    }
    google::protobuf::Any input;
    input.PackFrom( valid );
    BOOST_REQUIRE( symbol.Deserialize( input ) );
    SchematicSymbolInstance observed;
    BOOST_REQUIRE( Pack( symbol ).UnpackTo( &observed ) );
    BOOST_REQUIRE_EQUAL( observed.definition().unit_display_names_size(), 3 );
    for( int unit : { 0, 1, 2 } )
    {
        BOOST_CHECK_EQUAL( observed.definition().unit_display_names( unit ).unit(), unit );
        BOOST_CHECK_EQUAL( observed.definition().unit_display_names( unit ).name(), "Unit " + std::to_string( unit ) );
    }
    TEMP_LIBRARY saved;
    {
        SCH_IO_KICAD_SEXPR writer;
        writer.CreateLibrary( saved.path );
        writer.SaveSymbol( saved.path, new LIB_SYMBOL( *symbol.GetLibSymbolRef() ) );
        writer.SaveLibrary( saved.path );
    }
    SCH_IO_KICAD_SEXPR reader;
    auto* loaded = reader.LoadSymbol( saved.path, wxS( "PowerTraits" ) );
    BOOST_REQUIRE( loaded );
    BOOST_REQUIRE_EQUAL( loaded->GetUnitDisplayNames().size(), 3 );
    for( int unit : { 0, 1, 2 } )
        BOOST_CHECK_EQUAL( loaded->GetUnitDisplayNames().at( unit ).ToStdString(), "Unit " + std::to_string( unit ) );
}

BOOST_AUTO_TEST_CASE( PinMapsRejectOverwrittenIdentitiesWithoutRejectingSharedPads )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SCH_SYMBOL symbol;
    symbol.SetLibId( library.GetLibId() );
    symbol.SetLibSymbol( new LIB_SYMBOL( library ) );
    auto before = Pack( symbol );
    SchematicSymbolInstance original;
    BOOST_REQUIRE( before.UnpackTo( &original ) );

    for( bool duplicateMap : { false, true } )
    {
        auto invalid = original;
        invalid.mutable_position()->set_x_nm( 2345678 );
        auto* maps = invalid.mutable_definition()->mutable_pin_maps();
        auto* map = maps->add_pin_maps();
        map->set_name( "Package" );
        auto* entry = map->add_entries();
        entry->set_pin_number( "1" ); entry->set_pad_number( "A1" );
        if( duplicateMap )
        {
            auto* replacement = maps->add_pin_maps();
            replacement->set_name( "Package" );
        }
        else
        {
            auto* replacement = map->add_entries();
            replacement->set_pin_number( "1" ); replacement->set_pad_number( "B1" );
        }
        google::protobuf::Any input;
        input.PackFrom( invalid );
        BOOST_CHECK( !symbol.Deserialize( input ) );
        BOOST_CHECK_EQUAL( Pack( symbol ).SerializeAsString(), before.SerializeAsString() );
    }

    auto valid = original;
    for( const char* mapName : { "Package A", "Package B" } )
    {
        auto* map = valid.mutable_definition()->mutable_pin_maps()->add_pin_maps();
        map->set_name( mapName );
        for( auto [pin, pad] : std::vector<std::pair<const char*, const char*>>{
                { "1", "[4,9]" }, { "2", "[4,9]" }, { "3", "" } } )
        {
            auto* entry = map->add_entries();
            entry->set_pin_number( pin ); entry->set_pad_number( pad );
        }
    }
    google::protobuf::Any input;
    input.PackFrom( valid );
    BOOST_REQUIRE( symbol.Deserialize( input ) );
    const auto& maps = symbol.GetLibSymbolRef()->GetPinMaps();
    BOOST_REQUIRE_EQUAL( maps.GetAll().size(), 2 );
    for( const PIN_MAP& map : maps.GetAll() )
    {
        BOOST_REQUIRE_EQUAL( map.GetEntries().size(), 3 );
        BOOST_CHECK_EQUAL( map.GetPadNumber( wxS( "1" ) ).ToStdString(), "[4,9]" );
        BOOST_CHECK_EQUAL( map.GetPadNumber( wxS( "2" ) ).ToStdString(), "[4,9]" );
        BOOST_CHECK( map.HasEntry( wxS( "3" ) ) );
        BOOST_CHECK( map.GetPadNumber( wxS( "3" ) ).empty() );
    }
    TEMP_LIBRARY saved;
    {
        SCH_IO_KICAD_SEXPR writer;
        writer.CreateLibrary( saved.path );
        writer.SaveSymbol( saved.path, new LIB_SYMBOL( *symbol.GetLibSymbolRef() ) );
        writer.SaveLibrary( saved.path );
    }
    SCH_IO_KICAD_SEXPR reader;
    auto* loaded = reader.LoadSymbol( saved.path, wxS( "PowerTraits" ) );
    BOOST_REQUIRE( loaded );
    BOOST_REQUIRE_EQUAL( loaded->GetPinMaps().GetAll().size(), 2 );
    for( const PIN_MAP& map : loaded->GetPinMaps().GetAll() )
    {
        BOOST_REQUIRE_EQUAL( map.GetEntries().size(), 3 );
        BOOST_CHECK_EQUAL( map.GetPadNumber( wxS( "1" ) ).ToStdString(), "[4,9]" );
        BOOST_CHECK_EQUAL( map.GetPadNumber( wxS( "2" ) ).ToStdString(), "[4,9]" );
        BOOST_CHECK( map.HasEntry( wxS( "3" ) ) );
    }
}

BOOST_AUTO_TEST_CASE( DefinitionDecodingDoesNotRequireAPlacedSymbol )
{
    SchematicSymbol definition;
    definition.mutable_id()->set_library_nickname( "Automation" );
    definition.mutable_id()->set_entry_name( "Unplaced" );
    definition.set_type( SST_LOCAL_POWER );
    definition.set_unit_count( 1 );
    definition.mutable_attributes()->set_exclude_from_board( true );
    definition.mutable_embedded_files();
    LIB_SYMBOL parent( wxS( "Unplaced" ) );
    SCH_PIN pin( &parent );
    google::protobuf::Any packedPin;
    pin.Serialize( packedPin );
    SchematicPin pinData;
    BOOST_REQUIRE( packedPin.UnpackTo( &pinData ) );
    pinData.set_active_alternate( "Alternate" );
    auto* alternate = pinData.add_alternates();
    alternate->set_name( "Alternate" );
    alternate->set_shape( pinData.shape() );
    alternate->set_electrical_type( pinData.electrical_type() );
    definition.add_items()->mutable_item()->PackFrom( pinData );
    std::unordered_map<KIID, wxString> alternates;
    auto library = UnpackSymbolDefinition( definition, &alternates );
    BOOST_REQUIRE( library );
    BOOST_CHECK( library->IsLocalPower() );
    BOOST_CHECK( library->GetExcludedFromBoard() );
    BOOST_CHECK_EQUAL( library->GetLibId().GetLibItemName(), "Unplaced" );
    BOOST_REQUIRE_EQUAL( alternates.size(), 1 );
    BOOST_CHECK_EQUAL( alternates.at( pin.m_Uuid ).ToStdString(), "Alternate" );

    const auto before = alternates;
    auto invalid = definition;
    invalid.mutable_items( 0 )->mutable_item()->set_type_url( "type.googleapis.com/unsupported" );
    BOOST_CHECK( !UnpackSymbolDefinition( invalid, &alternates ) );
    BOOST_CHECK( alternates == before );
    auto validWithoutOutput = UnpackSymbolDefinition( definition );
    BOOST_REQUIRE( validWithoutOutput );
    BOOST_CHECK( validWithoutOutput->IsLocalPower() );
}

BOOST_AUTO_TEST_CASE( DefinitionPackingPreservesLibraryPinsWithoutPlacementIdentities )
{
    auto library = MakeLibrary( SST_LOCAL_POWER, 15 );
    auto* pin = new SCH_PIN( &library );
    pin->SetNumber( wxS( "1" ) );
    pin->SetName( wxS( "Supply" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    pin->SetPosition( VECTOR2I( schIUScale.MilsToIU( 100 ), schIUScale.MilsToIU( 200 ) ) );
    library.AddDrawItem( pin );
    const KIID pinId = pin->m_Uuid;

    SchematicSymbol packed;
    PackSymbolDefinition( packed, library );
    BOOST_REQUIRE_EQUAL( packed.items_size(), 1 );
    BOOST_CHECK( packed.pins_use_local_coordinates() );
    SchematicPin packedPin;
    BOOST_REQUIRE( packed.items( 0 ).item().UnpackTo( &packedPin ) );
    BOOST_CHECK_EQUAL( packedPin.id().value(), pinId.AsStdString() );
    BOOST_CHECK( packedPin.active_alternate().empty() );
    auto restored = UnpackSymbolDefinition( packed );
    BOOST_REQUIRE( restored );
    CheckTraits( *restored, SST_LOCAL_POWER, 15 );
    SchematicSymbol repacked;
    PackSymbolDefinition( repacked, *restored );
    BOOST_CHECK_EQUAL( repacked.SerializeAsString(), packed.SerializeAsString() );

    // Reusing an output must replace its contents, never append duplicate pins.
    PackSymbolDefinition( packed, library );
    BOOST_CHECK_EQUAL( packed.items_size(), 1 );
    PackSymbolDefinition( packed, library, false );
    BOOST_CHECK_EQUAL( packed.items_size(), 0 );
    BOOST_CHECK( !packed.pins_use_local_coordinates() );
    BOOST_CHECK( pin->m_Uuid == pinId );
}

BOOST_AUTO_TEST_CASE( PlacedPinRetainsItsIndependentDefinitionIdentity )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    auto* pin = new SCH_PIN( &library );
    pin->SetNumber( wxS( "1" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    library.AddDrawItem( pin );
    const KIID ownedId = pin->m_Uuid;
    const KIID placedId;
    SCH_SYMBOL source;
    source.SetLibId( library.GetLibId() );
    source.SetLibSymbol( new LIB_SYMBOL( library ) );
    SchematicSymbolInstance message;
    BOOST_REQUIRE( Pack( source ).UnpackTo( &message ) );
    PackSymbolDefinition( *message.mutable_definition(), library );
    auto* child = message.mutable_definition()->mutable_items( 0 );
    SchematicPin placed;
    BOOST_REQUIRE( child->item().UnpackTo( &placed ) );
    placed.mutable_library_pin_id()->set_value( ownedId.AsStdString() );
    placed.mutable_id()->set_value( placedId.AsStdString() );
    child->mutable_item()->PackFrom( placed );
    google::protobuf::Any input;
    input.PackFrom( message );
    SCH_SYMBOL restored;
    BOOST_REQUIRE( restored.Deserialize( input ) );
    BOOST_REQUIRE_EQUAL( restored.GetAllLibPins().size(), 1 );
    BOOST_REQUIRE_EQUAL( restored.GetPins().size(), 1 );
    BOOST_CHECK( restored.GetAllLibPins().front()->m_Uuid == ownedId );
    BOOST_CHECK( restored.GetPins().front()->m_Uuid == placedId );
    google::protobuf::Any serializedPin;
    restored.GetPins().front()->Serialize( serializedPin );
    SchematicPin observed;
    BOOST_REQUIRE( serializedPin.UnpackTo( &observed ) );
    BOOST_CHECK_EQUAL( observed.id().value(), placedId.AsStdString() );
    BOOST_CHECK_EQUAL( observed.library_pin_id().value(), ownedId.AsStdString() );
    SchematicSymbol owned;
    PackSymbolDefinition( owned, *restored.GetLibSymbolRef() );
    SchematicPin ownedPin;
    BOOST_REQUIRE( owned.items( 0 ).item().UnpackTo( &ownedPin ) );
    BOOST_CHECK( !ownedPin.has_library_pin_id() );
    BOOST_CHECK_EQUAL( ownedPin.id().value(), ownedId.AsStdString() );
    BOOST_CHECK( !UnpackSymbolDefinition( message.definition() ) );
    auto invalid = message;
    placed.mutable_library_pin_id()->set_value( "not-a-uuid" );
    invalid.mutable_definition()->mutable_items( 0 )->mutable_item()->PackFrom( placed );
    input.PackFrom( invalid );
    BOOST_CHECK( !restored.Deserialize( input ) );
    BOOST_CHECK( restored.GetAllLibPins().front()->m_Uuid == ownedId );
    BOOST_CHECK( restored.GetPins().front()->m_Uuid == placedId );
}

BOOST_AUTO_TEST_CASE( NewlyActivatedPinDoesNotReuseItsDefinitionIdentity )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    auto* pin = new SCH_PIN( &library );
    pin->SetNumber( wxS( "1" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    library.AddDrawItem( pin );
    const KIID ownedId = pin->m_Uuid;
    SCH_SYMBOL source;
    source.SetLibId( library.GetLibId() );
    source.SetLibSymbol( new LIB_SYMBOL( library ) );
    SchematicSymbolInstance message;
    BOOST_REQUIRE( Pack( source ).UnpackTo( &message ) );
    PackSymbolDefinition( *message.mutable_definition(), library );
    message.set_separate_pin_identities( true );
    google::protobuf::Any input;
    input.PackFrom( message );
    SCH_SYMBOL first, second;
    BOOST_REQUIRE( first.Deserialize( input ) );
    BOOST_REQUIRE( second.Deserialize( input ) );
    BOOST_REQUIRE_EQUAL( first.GetPins().size(), 1 );
    BOOST_REQUIRE_EQUAL( second.GetPins().size(), 1 );
    BOOST_CHECK( first.GetPins().front()->m_Uuid != ownedId );
    BOOST_CHECK( second.GetPins().front()->m_Uuid != ownedId );
    BOOST_CHECK( first.GetPins().front()->m_Uuid != second.GetPins().front()->m_Uuid );
    BOOST_CHECK( first.GetAllLibPins().front()->m_Uuid == ownedId );
    BOOST_CHECK( second.GetAllLibPins().front()->m_Uuid == ownedId );
}

BOOST_AUTO_TEST_CASE( CachedDefinitionsPreserveDisplaySettingsAndRemainNativelySaveable )
{
    auto library = MakeLibrary( SST_GLOBAL_POWER, 15 );
    library.SetShowPinNames( false );
    library.SetShowPinNumbers( true );
    // The native file format permits negative pin-name offsets too.
    library.SetPinNameOffset( -schIUScale.MilsToIU( 50 ) );
    auto* pin = new SCH_PIN( &library );
    pin->SetNumber( wxS( "1" ) );
    pin->SetName( wxS( "Supply" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    library.AddDrawItem( pin );
    SchematicCachedSymbol packed;
    BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:PowerTraits_7" ), library ) );
    BOOST_CHECK_EQUAL( packed.cache_key(), "Automation:PowerTraits_7" );
    auto restored = UnpackCachedSymbol( packed );
    BOOST_REQUIRE( restored );
    BOOST_CHECK( !restored->GetShowPinNames() );
    BOOST_CHECK( restored->GetShowPinNumbers() );
    BOOST_CHECK_EQUAL( restored->GetPinNameOffset(), library.GetPinNameOffset() );
    CheckTraits( *restored, SST_GLOBAL_POWER, 15 );
    SchematicCachedSymbol repacked;
    BOOST_REQUIRE( PackCachedSymbol( repacked, wxS( "Automation:PowerTraits_7" ), *restored ) );
    BOOST_CHECK_EQUAL( packed.SerializeAsString(), repacked.SerializeAsString() );

    TEMP_LIBRARY file;
    {
        SCH_IO_KICAD_SEXPR writer;
        writer.CreateLibrary( file.path );
        writer.SaveSymbol( file.path, new LIB_SYMBOL( *restored ) );
        writer.SaveLibrary( file.path );
    }
    SCH_IO_KICAD_SEXPR reader;
    auto* loaded = reader.LoadSymbol( file.path, wxS( "PowerTraits" ) );
    BOOST_REQUIRE( loaded );
    CheckTraits( *loaded, SST_GLOBAL_POWER, 15 );
    BOOST_CHECK( !loaded->GetShowPinNames() );
    BOOST_CHECK( loaded->GetShowPinNumbers() );
    BOOST_CHECK_EQUAL( loaded->GetPinNameOffset(), library.GetPinNameOffset() );
}

BOOST_AUTO_TEST_CASE( CacheValidationRejectsExternalInheritanceAndInstanceOnlyState )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SchematicCachedSymbol packed;
    BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:PowerTraits" ), library ) );
    const auto original = packed.SerializeAsString();
    LIB_SYMBOL derived( wxS( "Derived" ), &library );
    BOOST_CHECK( !PackCachedSymbol( packed, wxS( "Derived" ), derived ) );
    BOOST_CHECK_EQUAL( original, packed.SerializeAsString() );
    BOOST_CHECK( !PackCachedSymbol( packed, wxEmptyString, library ) );
    BOOST_CHECK_EQUAL( original, packed.SerializeAsString() );
    // Root definitions can retain a diagnostic name after flattening; the
    // native writer does not persist it as inheritance.
    library.SetParentName( wxS( "FormerParent" ) );
    BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:PowerTraits" ), library ) );
    BOOST_CHECK_EQUAL( original, packed.SerializeAsString() );

    for( int64_t offset : { int64_t( -1 ), int64_t( 1 ), std::numeric_limits<int64_t>::max() } )
    {
        auto invalid = packed;
        invalid.mutable_pin_name_offset()->set_value_nm( offset );
        BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    }
    auto invalid = packed;
    invalid.clear_pin_name_offset();
    BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    invalid = packed;
    invalid.GetReflection()->MutableUnknownFields( &invalid )->AddVarint( 900, 1 );
    BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    invalid = packed;
    auto* definition = invalid.mutable_definition();
    definition->GetReflection()->MutableUnknownFields( definition )->AddVarint( 900, 1 );
    BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    invalid = packed;
    invalid.mutable_definition()->set_pins_use_local_coordinates( false );
    BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    invalid = packed;
    SCH_PIN pin( &library );
    google::protobuf::Any pinAny;
    pin.Serialize( pinAny );
    SchematicPin pinData;
    BOOST_REQUIRE( pinAny.UnpackTo( &pinData ) );
    pinData.set_active_alternate( "PlacementOnly" );
    invalid.mutable_definition()->add_items()->mutable_item()->PackFrom( pinData );
    BOOST_CHECK( !UnpackCachedSymbol( invalid ) );
    BOOST_REQUIRE( UnpackCachedSymbol( packed ) );
}

BOOST_AUTO_TEST_CASE( CacheOwnershipExchangeRetainsExactDefinitionsAndRollsBack )
{
    struct OWNED_NATIVE_CACHE
    {
        std::map<wxString, LIB_SYMBOL*> symbols;
        ~OWNED_NATIVE_CACHE() { for( const auto& [key, value] : symbols ) delete value; }
    } native;
    auto original = std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_GLOBAL_POWER, 15 ) );
    LIB_SYMBOL* originalPointer = original.get();
    native.symbols.emplace( wxS( "Library:Unplaced" ), original.release() );
    SCH_SYMBOL_CACHE_STATE captured( native.symbols );
    BOOST_REQUIRE_EQUAL( captured.Symbols().size(), 1 );
    BOOST_CHECK( captured.Symbols().at( wxS( "Library:Unplaced" ) ) != originalPointer );
    originalPointer->SetShowPinNames( false );
    BOOST_CHECK( captured.Symbols().at( wxS( "Library:Unplaced" ) )->GetShowPinNames() );

    SCH_SYMBOL_CACHE_STATE replacement;
    BOOST_REQUIRE( replacement.Insert( wxS( "Library:Replacement" ),
            std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_NORMAL, 0 ) ) ) );
    BOOST_CHECK( !replacement.Insert( wxS( "Library:Replacement" ),
            std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_LOCAL_POWER, 0 ) ) ) );
    replacement.Swap( native.symbols );
    BOOST_REQUIRE_EQUAL( native.symbols.size(), 1 );
    BOOST_CHECK( native.symbols.at( wxS( "Library:Replacement" ) )->IsNormal() );
    BOOST_CHECK( replacement.Symbols().at( wxS( "Library:Unplaced" ) ) == originalPointer );
    replacement.Swap( native.symbols );
    BOOST_CHECK( native.symbols.at( wxS( "Library:Unplaced" ) ) == originalPointer );
    BOOST_CHECK( !originalPointer->GetShowPinNames() );
    captured.Swap( native.symbols );
    BOOST_CHECK( native.symbols.at( wxS( "Library:Unplaced" ) )->GetShowPinNames() );
    BOOST_CHECK( captured.Symbols().at( wxS( "Library:Unplaced" ) ) == originalPointer );

    // Validation failure must occur before either ownership graph changes.
    native.symbols.emplace( wxS( "Invalid" ), nullptr );
    auto* before = native.symbols.at( wxS( "Library:Unplaced" ) );
    BOOST_CHECK_THROW( replacement.Swap( native.symbols ), std::invalid_argument );
    BOOST_CHECK( native.symbols.at( wxS( "Library:Unplaced" ) ) == before );
    BOOST_CHECK_EQUAL( replacement.Symbols().size(), 1 );
    native.symbols.erase( wxS( "Invalid" ) );
    replacement.Swap( native.symbols );
    BOOST_CHECK( native.symbols.contains( wxS( "Library:Replacement" ) ) );
}

BOOST_AUTO_TEST_CASE( WholeCacheDecodingIsAtomicAndRetainsUnplacedDefinitions )
{
    SCH_SYMBOL_CACHE_STATE original;
    BOOST_REQUIRE( original.Insert( wxS( "Library:A" ),
            std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_LOCAL_POWER, 15 ) ) ) );
    BOOST_REQUIRE( original.Insert( wxS( "Library:B" ),
            std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_NORMAL, 0 ) ) ) );
    google::protobuf::RepeatedPtrField<SchematicCachedSymbol> packed;
    BOOST_REQUIRE( PackCachedSymbols( packed, original.Symbols() ) );
    BOOST_REQUIRE_EQUAL( packed.size(), 2 );
    SCH_SYMBOL_CACHE_STATE restored;
    BOOST_REQUIRE( UnpackCachedSymbols( packed, restored ) );
    BOOST_CHECK( restored.Symbols().at( wxS( "Library:A" ) )->IsLocalPower() );
    BOOST_CHECK( restored.Symbols().at( wxS( "Library:B" ) )->IsNormal() );
    auto* retained = restored.Symbols().at( wxS( "Library:A" ) );
    auto duplicate = packed;
    duplicate.Add()->CopyFrom( packed.Get( 0 ) );
    BOOST_CHECK( !UnpackCachedSymbols( duplicate, restored ) );
    BOOST_CHECK( restored.Symbols().at( wxS( "Library:A" ) ) == retained );
    auto corrupt = packed;
    corrupt.Mutable( 1 )->clear_definition();
    BOOST_CHECK( !UnpackCachedSymbols( corrupt, restored ) );
    BOOST_CHECK( restored.Symbols().at( wxS( "Library:A" ) ) == retained );
    std::map<wxString, LIB_SYMBOL*> invalid{ { wxS( "Invalid" ), nullptr } };
    BOOST_CHECK( !PackCachedSymbols( packed, invalid ) );
    BOOST_CHECK_EQUAL( packed.size(), 2 );
    google::protobuf::RepeatedPtrField<SchematicCachedSymbol> empty;
    BOOST_REQUIRE( UnpackCachedSymbols( empty, restored ) );
    BOOST_CHECK( restored.Symbols().empty() );
    BOOST_REQUIRE( UnpackCachedSymbols( packed, restored ) );
    BOOST_CHECK_EQUAL( restored.Symbols().size(), 2 );
}

BOOST_AUTO_TEST_CASE( UnplacedNativeCacheCanBeDeletedAndRebuiltFromTypedRecords )
{
    SCH_SCREEN screen;
    screen.AddLibSymbol( wxS( "Library:Unplaced" ),
            std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_GLOBAL_POWER, 15 ) ) );
    auto library = std::make_unique<LIB_SYMBOL>( MakeLibrary( SST_NORMAL, 0 ) );
    auto* pin = new SCH_PIN( library.get() );
    pin->SetNumber( wxS( "1" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    library->AddDrawItem( pin );
    screen.AddLibSymbol( wxS( "Library:Other" ), std::move( library ) );
    google::protobuf::RepeatedPtrField<SchematicCachedSymbol> original;
    BOOST_REQUIRE( PackCachedSymbols( original, screen.GetLibSymbols() ) );
    {
        SCH_SYMBOL_CACHE_STATE removed;
        screen.SwapLibSymbolCache( removed );
        BOOST_CHECK( screen.GetLibSymbols().empty() );
    } // The original definitions are actually destroyed here.
    SCH_SYMBOL_CACHE_STATE restored;
    BOOST_REQUIRE( UnpackCachedSymbols( original, restored ) );
    screen.SwapLibSymbolCache( restored );
    google::protobuf::RepeatedPtrField<SchematicCachedSymbol> rebuilt;
    BOOST_REQUIRE( PackCachedSymbols( rebuilt, screen.GetLibSymbols() ) );
    BOOST_REQUIRE_EQUAL( original.size(), rebuilt.size() );
    for( int index = 0; index < original.size(); ++index )
        BOOST_CHECK_EQUAL( original.Get( index ).SerializeAsString(), rebuilt.Get( index ).SerializeAsString() );
}

BOOST_AUTO_TEST_CASE( CacheSaveLoadKeepsLocalKeyAndDefinitionIdentityIndependent )
{
    auto library = MakeLibrary( SST_GLOBAL_POWER, 15 );
    const wxString cacheKey = wxS( "Local_9" );
    auto* pin = new SCH_PIN( &library );
    pin->SetNumber( wxS( "1" ) );
    pin->SetUnit( 1 );
    pin->SetBodyStyle( BODY_STYLE::BASE );
    library.AddDrawItem( pin );
    SchematicCachedSymbol before;
    BOOST_REQUIRE( PackCachedSymbol( before, cacheKey, library ) );
    auto invalidDefinition = before;
    invalidDefinition.mutable_definition()->mutable_id()->clear_entry_name();
    BOOST_CHECK( !UnpackCachedSymbol( invalidDefinition ) );
    auto escapedKey = before;
    escapedKey.set_cache_key( "Library:Name{slash}Suffix" );
    BOOST_CHECK( !UnpackCachedSymbol( escapedKey ) );
    STRING_FORMATTER output;
    SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol( &library, output, cacheKey, true, true );
    const std::string body = output.GetString();
    BOOST_CHECK( body.find( "(lib_id \"Automation:PowerTraits\")" ) != std::string::npos );

    SCHEMATIC schematic( nullptr );
    auto* sheet = schematic.GetTopLevelSheet( 0 );
    BOOST_REQUIRE( sheet && sheet->GetScreen() );
    STRING_LINE_READER reader( "(kicad_sch (version 20260907) (generator cache_test) (lib_symbols "
                              + body + "))", "cache-roundtrip" );
    SCH_IO_KICAD_SEXPR_PARSER parser( &reader );
    BOOST_REQUIRE_NO_THROW( parser.ParseSchematic( sheet ) );
    const auto& cache = sheet->GetScreen()->GetLibSymbols();
    BOOST_REQUIRE_EQUAL( cache.size(), 1 );
    BOOST_REQUIRE( cache.contains( cacheKey ) );
    SchematicCachedSymbol after;
    BOOST_REQUIRE( PackCachedSymbol( after, cacheKey, *cache.at( cacheKey ) ) );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );

    // Cache-only state must not quietly expand the external library grammar.
    STRING_LINE_READER externalReader( body, "not-an-external-library-symbol" );
    SCH_IO_KICAD_SEXPR_PARSER externalParser( &externalReader );
    LIB_SYMBOL_MAP externalMap;
    BOOST_CHECK_THROW( externalParser.ParseSymbol( externalMap ), IO_ERROR );
    for( const auto& invalid : {
            "(kicad_sch (version 20260907) (generator cache_test) (lib_symbols "
            "(symbol \"Alias\" (lib_id \"Library:One\") (lib_id \"Library:Two\"))))",
            "(kicad_sch (version 20250114) (generator cache_test) (lib_symbols "
            "(symbol \"Alias\" (lib_id \"Library:One\"))))" } )
    {
        SCHEMATIC rejected( nullptr );
        STRING_LINE_READER invalidReader( invalid, "invalid-cache-identity" );
        SCH_IO_KICAD_SEXPR_PARSER invalidParser( &invalidReader );
        BOOST_CHECK_THROW( invalidParser.ParseSchematic( rejected.GetTopLevelSheet( 0 ) ), IO_ERROR );
    }
}

BOOST_AUTO_TEST_CASE( DeMorganBodyStylesRemainDistinctFromIdenticallyNamedCustomStyles )
{
    for( bool demorgan : { false, true } )
    {
        auto library = MakeLibrary( SST_NORMAL, 0 );
        library.SetBodyStyleNames( { wxS( "Standard" ), wxS( "Alternate" ) } );
        library.SetHasDeMorganBodyStyles( demorgan );
        SchematicCachedSymbol packed;
        BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:BodyStyles" ), library ) );
        auto restored = UnpackCachedSymbol( packed );
        BOOST_REQUIRE( restored );
        BOOST_CHECK_EQUAL( restored->HasDeMorganBodyStyles(), demorgan );
        BOOST_CHECK_EQUAL( restored->GetBodyStyleCount(), 2 );
        if( demorgan )
        {
            auto wrongName = packed;
            wrongName.mutable_definition()->mutable_body_style( 1 )->set_name( "Custom" );
            BOOST_CHECK( !UnpackCachedSymbol( wrongName ) );
            auto missingStyle = packed;
            missingStyle.mutable_definition()->mutable_body_style()->RemoveLast();
            BOOST_CHECK( !UnpackCachedSymbol( missingStyle ) );
        }
    }
}

BOOST_AUTO_TEST_CASE( ExternalInheritanceCannotSilentlyEnterASelfContainedSchematicCache )
{
    auto library = MakeLibrary( SST_NORMAL, 0 );
    SchematicCachedSymbol original;
    BOOST_REQUIRE( PackCachedSymbol( original, wxS( "Automation:Derived" ), library ) );
    library.SetParentName( wxS( "MissingParent" ) );
    SchematicCachedSymbol packed;
    BOOST_REQUIRE( PackCachedSymbol( packed, wxS( "Automation:Derived" ), library ) );
    BOOST_CHECK( packed.SerializeAsString() == original.SerializeAsString() );

    const std::string symbol = "(symbol \"Derived\" (extends \"Parent\"))";
    SCHEMATIC schematic( nullptr );
    STRING_LINE_READER reader( "(kicad_sch (version 20260907) (generator cache_test) (lib_symbols "
                              + symbol + "))", "unsupported-cache-inheritance" );
    SCH_IO_KICAD_SEXPR_PARSER parser( &reader );
    BOOST_CHECK_THROW( parser.ParseSchematic( schematic.GetTopLevelSheet( 0 ) ), IO_ERROR );
    BOOST_CHECK( schematic.GetTopLevelSheet( 0 )->GetScreen()->GetLibSymbols().empty() );

    STRING_LINE_READER externalReader( symbol, "valid-external-inheritance" );
    SCH_IO_KICAD_SEXPR_PARSER externalParser( &externalReader );
    LIB_SYMBOL_MAP externalMap;
    std::unique_ptr<LIB_SYMBOL> external( externalParser.ParseSymbol( externalMap ) );
    BOOST_REQUIRE( external );
    BOOST_CHECK( external->GetParentName() == wxS( "Parent" ) );
}

BOOST_AUTO_TEST_CASE( CacheCodecPreservesCanonicalNativeDefinitionOutput )
{
    const wxString cacheKey = wxS( "LocalAlias_3" );
    auto save = [&]( const LIB_SYMBOL& input )
    {
        // The native writer may normalize embedded fonts; never run it on the
        // caller's live library while checking fidelity.
        LIB_SYMBOL copy( input );
        STRING_FORMATTER formatter;
        SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol( &copy, formatter, cacheKey, true, true );
        return formatter.GetString();
    };
    for( auto type : { SST_NORMAL, SST_GLOBAL_POWER, SST_LOCAL_POWER } )
    {
        for( bool demorgan : { false, true } )
        {
            auto library = MakeLibrary( type, 15 );
            library.SetBodyStyleNames( { wxS( "Standard" ), wxS( "Alternate" ) } );
            library.SetHasDeMorganBodyStyles( demorgan );
            library.LockUnits( true );
            library.SetUnitCount( 2, false );
            library.GetUnitDisplayNames()[1] = "Control";
            library.GetUnitDisplayNames()[2] = "Power";
            library.SetKeyWords( wxS( "measurement mixed-signal" ) );
            library.SetFPFilters( { wxS( "Package_*" ), wxS( "Custom footprint*" ) } );
            library.SetShowPinNames( false );
            library.SetShowPinNumbers( false );
            library.SetPinNameOffset( -schIUScale.MilsToIU( 25 ) );
            library.SetDuplicatePinNumbersAreJumpers( true );
            library.JumperPinGroups().push_back( { wxS( "1" ), wxS( "2" ) } );
            for( int unit = 1; unit <= 2; ++unit )
            {
                auto* pin = new SCH_PIN( &library );
                pin->SetNumber( wxString::Format( "%d", unit ) );
                pin->SetName( wxString::Format( "Signal%d", unit ) );
                pin->SetUnit( unit );
                pin->SetBodyStyle( unit );
                library.AddDrawItem( pin );
            }
            const auto before = save( library );
            SchematicCachedSymbol packed;
            BOOST_REQUIRE( PackCachedSymbol( packed, cacheKey, library ) );
            auto reconstructed = UnpackCachedSymbol( packed );
            BOOST_REQUIRE( reconstructed );
            BOOST_CHECK_MESSAGE( save( *reconstructed ) == before,
                                 "Typed cache reconstruction changed native persisted definition output" );
            BOOST_CHECK( save( library ) == before );

            SCHEMATIC loaded( nullptr );
            STRING_LINE_READER input( "(kicad_sch (version 20260907) (generator cache_test) (lib_symbols "
                                     + before + "))", "native-cache-fidelity" );
            SCH_IO_KICAD_SEXPR_PARSER parser( &input );
            BOOST_REQUIRE_NO_THROW( parser.ParseSchematic( loaded.GetTopLevelSheet( 0 ) ) );
            const auto& definitions = loaded.GetTopLevelSheet( 0 )->GetScreen()->GetLibSymbols();
            BOOST_REQUIRE_EQUAL( definitions.size(), 1 );
            BOOST_REQUIRE( definitions.contains( cacheKey ) );
            BOOST_CHECK_EQUAL( definitions.at( cacheKey )->HasDeMorganBodyStyles(), demorgan );
            const auto nativeLoaded = save( *definitions.at( cacheKey ) );
            SchematicCachedSymbol loadedMessage;
            BOOST_REQUIRE( PackCachedSymbol( loadedMessage, cacheKey, *definitions.at( cacheKey ) ) );
            auto rebuilt = UnpackCachedSymbol( loadedMessage );
            BOOST_REQUIRE( rebuilt );
            BOOST_CHECK_MESSAGE( save( *rebuilt ) == nativeLoaded,
                                 "Reconstruction changed the native loader's persisted definition" );
        }
    }
}

BOOST_AUTO_TEST_SUITE_END()
