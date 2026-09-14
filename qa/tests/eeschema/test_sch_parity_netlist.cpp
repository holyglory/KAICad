/* Read-only native parity capture, not end-to-end job qualification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/sch_parity_netlist.h>
#include <connection_graph.h>
#include <lib_symbol.h>
#include <netlist_reader/netlist_reader.h>
#include <netlist_reader/netlist.h>
#include <project/project_file.h>
#include <json_common.h>
#include <richio.h>
#include <schematic.h>
#include <schematic_utils/schematic_file_util.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_symbol.h>
#include <settings/settings_manager.h>
#include <algorithm>

namespace
{
std::string state( SCHEMATIC& schematic )
{
    STRING_FORMATTER output;
    SCH_IO_KICAD_SEXPR writer;
    for( const SCH_SHEET_PATH& path : schematic.Hierarchy() )
        writer.FormatSchematicToFormatter( &output, path.Last(), &schematic, nullptr, false );
    return output.GetString() + schematic.Project().GetProjectFile().CaptureCurrentState().dump();
}

struct PARITY_FIXTURE
{
    SETTINGS_MANAGER settings;
    std::unique_ptr<SCHEMATIC> schematic;

    PARITY_FIXTURE()
    {
        KI_TEST::LoadSchematic( settings, "net_chains_four_nets", schematic );
        schematic->ConnectionGraph()->Recalculate( schematic->Hierarchy(), true );
    }

    SCH_SYMBOL* firstSymbol()
    {
        for( const auto& path : schematic->Hierarchy() )
            for( SCH_ITEM* item : path.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
            {
                auto* symbol = static_cast<SCH_SYMBOL*>( item );
                if( symbol->GetLibSymbolRef() && !symbol->GetLibSymbolRef()->IsPower() ) return symbol;
            }
        return nullptr;
    }

    void expect( SCH_PARITY_INPUT_STATUS expected )
    {
        STRING_FORMATTER output;
        BOOST_CHECK_EXCEPTION( FormatSchematicParityNetlist( *schematic, output ), SCH_PARITY_INPUT_ERROR,
                               [&]( const SCH_PARITY_INPUT_ERROR& error ) { return error.Status() == expected; } );
        BOOST_CHECK( output.GetString().empty() );
    }
};
}

BOOST_FIXTURE_TEST_SUITE( SchematicParityNetlist, PARITY_FIXTURE )

BOOST_AUTO_TEST_CASE( NativeNetlistContainsActualComponentsAndNetsWithoutChangingSource )
{
    const auto before = state( *schematic );
    const auto sheet = schematic->CurrentSheet();
    const auto revision = schematic->ChangeJournal().Sequence();
    STRING_FORMATTER output;
    auto warnings = FormatSchematicParityNetlist( *schematic, output );
    BOOST_CHECK( warnings.empty() );
    NETLIST netlist;
    KICAD_NETLIST_READER reader( new STRING_LINE_READER( output.GetString(), "captured netlist" ), &netlist );
    reader.LoadNetlist();
    BOOST_REQUIRE_GT( netlist.GetCount(), 0 );
    unsigned pins = 0;
    for( unsigned i = 0; i < netlist.GetCount(); ++i )
    {
        auto* component = netlist.GetComponent( i );
        BOOST_CHECK( !component->GetReference().empty() );
        BOOST_CHECK( !component->GetKIIDs().empty() );
        // Native netlists omit the root UUID from sheet paths. An empty path
        // means root, not lost identity; compare the exact native relative path
        // and symbol UUID instead of inferring identity from reference or position.
        bool identityMatched = false;
        for( const auto& path : schematic->Hierarchy() )
        {
            if( KIID_PATH( path.PathAsString() ) != component->GetPath() ) continue;
            for( SCH_ITEM* item : path.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
            {
                const auto& ids = component->GetKIIDs();
                if( std::find( ids.begin(), ids.end(), item->m_Uuid ) == ids.end() ) continue;
                identityMatched = true;
                BOOST_CHECK_EQUAL( component->GetReference(), static_cast<SCH_SYMBOL*>( item )->GetRef( &path, false ) );
            }
        }
        BOOST_CHECK( identityMatched );
        pins += component->GetNetCount();
    }
    BOOST_CHECK_GT( pins, 0 );
    BOOST_CHECK_EQUAL( state( *schematic ), before );
    BOOST_CHECK( schematic->CurrentSheet() == sheet );
    BOOST_CHECK_EQUAL( schematic->ChangeJournal().Sequence(), revision );
}

BOOST_AUTO_TEST_CASE( AnnotationAndTransientEditsAreRejectedWithoutAutomaticRepair )
{
    SCH_SYMBOL* symbol = firstSymbol();
    BOOST_REQUIRE( symbol );
    auto path = schematic->Hierarchy().front();
    symbol->SetRef( &path, "J?" );
    const auto before = state( *schematic );
    expect( SCH_PARITY_INPUT_STATUS::ANNOTATION_REQUIRED );
    BOOST_CHECK_EQUAL( symbol->GetRef( &path ), "J?" );
    BOOST_CHECK_EQUAL( state( *schematic ), before );
    symbol->SetFlags( IN_EDIT );
    expect( SCH_PARITY_INPUT_STATUS::EDIT_IN_PROGRESS );
    symbol->ClearFlags( IN_EDIT );
}

BOOST_AUTO_TEST_CASE( PendingConnectivityAndMissingDefinitionsDoNotProducePartialNetlists )
{
    SCH_SYMBOL* symbol = firstSymbol();
    BOOST_REQUIRE( symbol );
    symbol->SetConnectivityDirty();
    expect( SCH_PARITY_INPUT_STATUS::CONNECTIVITY_PENDING );
    BOOST_CHECK( symbol->IsConnectivityDirty() );
    schematic->ConnectionGraph()->Recalculate( schematic->Hierarchy(), true );
    STRING_FORMATTER recovered;
    BOOST_CHECK_NO_THROW( FormatSchematicParityNetlist( *schematic, recovered ) );
    schematic->RootScreen()->Append( new SCH_SYMBOL );
    expect( SCH_PARITY_INPUT_STATUS::MISSING_SYMBOL_DEFINITION );
}

BOOST_AUTO_TEST_CASE( OutputFailurePreservesTheSelectedSheetAndAllowsAnotherCapture )
{
    class FAILING_OUTPUT : public OUTPUTFORMATTER
    {
        void write( const char*, int ) override { throw std::runtime_error( "fixture output failure" ); }
    } failure;
    const auto before = state( *schematic );
    const auto selected = schematic->CurrentSheet();
    BOOST_CHECK_THROW( FormatSchematicParityNetlist( *schematic, failure ), std::runtime_error );
    BOOST_CHECK( schematic->CurrentSheet() == selected );
    BOOST_CHECK_EQUAL( state( *schematic ), before );
    STRING_FORMATTER output;
    BOOST_CHECK_NO_THROW( FormatSchematicParityNetlist( *schematic, output ) );
    BOOST_CHECK( !output.GetString().empty() );
}

BOOST_AUTO_TEST_CASE( MissingProjectIsAnExplicitInputError )
{
    SCHEMATIC noProject( nullptr );
    noProject.CreateDefaultScreens();
    STRING_FORMATTER output;
    BOOST_CHECK_EXCEPTION( FormatSchematicParityNetlist( noProject, output ), SCH_PARITY_INPUT_ERROR,
                           []( const SCH_PARITY_INPUT_ERROR& error )
                           { return error.Status() == SCH_PARITY_INPUT_STATUS::NOT_INITIALIZED; } );
    BOOST_CHECK( output.GetString().empty() );
}

BOOST_AUTO_TEST_CASE( AnExplicitlyEmptySchematicProducesAnEmptyNativeNetlist )
{
    SETTINGS_MANAGER emptySettings;
    // Empty filename initializes the default project but reports no file loaded.
    emptySettings.LoadProject( "" );
    SCHEMATIC empty( &emptySettings.Prj() );
    empty.CreateDefaultScreens();
    const auto before = state( empty );
    STRING_FORMATTER output;
    BOOST_CHECK( FormatSchematicParityNetlist( empty, output ).empty() );
    NETLIST netlist;
    KICAD_NETLIST_READER reader( new STRING_LINE_READER( output.GetString(), "empty netlist" ), &netlist );
    reader.LoadNetlist();
    BOOST_CHECK_EQUAL( netlist.GetCount(), 0 );
    BOOST_CHECK_EQUAL( state( empty ), before );
}

BOOST_AUTO_TEST_CASE( DuplicateSheetNamesRequireAnExplicitChoiceAndRetainTheWarning )
{
    SETTINGS_MANAGER emptySettings;
    emptySettings.LoadProject( "" );
    SCHEMATIC nested( &emptySettings.Prj() );
    nested.CreateDefaultScreens();
    for( const wxString& filename : { wxString( "one.kicad_sch" ), wxString( "two.kicad_sch" ) } )
    {
        auto* child = new SCH_SHEET( &nested.Root() );
        auto* screen = new SCH_SCREEN( &nested );
        screen->SetFileName( filename );
        child->SetScreen( screen );
        child->SetFileName( filename );
        child->SetName( "SameName" );
        nested.RootScreen()->Append( child );
    }
    nested.RefreshHierarchy();
    const auto sheets = nested.Hierarchy();
    BOOST_REQUIRE_EQUAL( sheets.size(), 3 );
    nested.SetCurrentSheet( sheets.back() );
    nested.ConnectionGraph()->Recalculate( sheets, true );
    const auto before = state( nested );
    STRING_FORMATTER rejected;
    BOOST_CHECK_EXCEPTION( FormatSchematicParityNetlist( nested, rejected ), SCH_PARITY_INPUT_ERROR,
                           []( const SCH_PARITY_INPUT_ERROR& error )
                           { return error.Status() == SCH_PARITY_INPUT_STATUS::DUPLICATE_SHEET_NAMES; } );
    BOOST_CHECK( rejected.GetString().empty() );
    BOOST_CHECK_EQUAL( state( nested ), before );
    STRING_FORMATTER accepted;
    auto warnings = FormatSchematicParityNetlist( nested, accepted, true );
    BOOST_REQUIRE_EQUAL( warnings.size(), 1 );
    BOOST_CHECK( !accepted.GetString().empty() );
    BOOST_CHECK( nested.CurrentSheet() == sheets.back() );
    BOOST_CHECK_EQUAL( state( nested ), before );
}

BOOST_AUTO_TEST_SUITE_END()
