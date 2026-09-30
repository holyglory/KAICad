/* Native parsing and real parity with owned inputs. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/pcb_drc_schematic_input.h>
#include <api/native_state_digest.h>
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <footprint.h>
#include <netlist_reader/pcb_netlist.h>

namespace
{
using namespace kiapi::automation::v1;
using kiapi::common::types::DocumentSpecifier;
using kiapi::common::types::DOCTYPE_PCB;
using kiapi::common::types::DOCTYPE_SCHEMATIC;
struct PARITY_INPUT_FIXTURE
{
    std::string epoch = KIID().AsStdString();
    DocumentSpecifier board;
    SchematicParityNetlistSnapshot snapshot;
    const std::string symbol = "e74fd410-f341-421a-b332-a39f523c96f6";

    PARITY_INPUT_FIXTURE()
    {
        board.set_type( DOCTYPE_PCB ); board.set_board_filename( "fixture.kicad_pcb" );
        board.mutable_project()->set_path( "/fixture/project" );
        board.mutable_project()->set_name( "fixture" );
        snapshot.set_schema_version( 1 );
        auto* state = snapshot.mutable_source_state();
        state->mutable_document()->mutable_project()->CopyFrom( board.project() );
        state->mutable_document()->set_type( DOCTYPE_SCHEMATIC );
        state->mutable_document()->mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
        state->mutable_revision()->set_epoch( KIID().AsStdString() );
        state->mutable_revision()->set_sequence( 17 );
        state->set_native_identity( KIID().AsStdString() );
        state->set_process_epoch( epoch );
        state->set_state_sha256( std::string( 64, 'a' ) );
        state->set_scope( DLS_SCHEMATIC_HIERARCHY );
        state->set_project_settings_included( true );
        payload( "(export (version E) (design) (components "
                 "(comp (ref R1) (value 10k) (footprint Device:R) "
                 "(sheetpath (names /) (tstamps /)) (tstamps " + symbol + "))) "
                 "(libparts) (libraries) (nets (net (code 1) (name SUPPLY) (node (ref R1) (pin 1)))))" );
    }
    void payload( const std::string& text )
    {
        snapshot.set_native_netlist_sexpr( text );
        NATIVE_STATE_DIGEST digest; digest.Append( text );
        snapshot.set_netlist_sha256( digest.Hex() );
    }
    auto capture()
    { return PCB_DRC_SCHEMATIC_INPUT::Capture( snapshot, snapshot.source_state(), board, epoch ); }
};
}

BOOST_FIXTURE_TEST_SUITE( DrcSchematicInput, PARITY_INPUT_FIXTURE )

BOOST_AUTO_TEST_CASE( OwnsNativeComponentsPinsAndExactRelativeIdentities )
{
    auto input = capture();
    BOOST_REQUIRE( input );
    const auto source = snapshot.source_state();
    snapshot.Clear();
    BOOST_REQUIRE_EQUAL( input->Netlist().GetCount(), 1 );
    auto* component = input->Netlist().GetComponent( 0 );
    BOOST_CHECK_EQUAL( component->GetReference(), "R1" );
    BOOST_CHECK_EQUAL( component->GetValue(), "10k" );
    BOOST_CHECK( component->GetPath().empty() ); // Explicit source document anchors root.
    BOOST_REQUIRE_EQUAL( component->GetKIIDs().size(), 1 );
    BOOST_CHECK_EQUAL( component->GetKIIDs().front().AsStdString(), symbol );
    BOOST_REQUIRE_EQUAL( component->GetNetCount(), 1 );
    BOOST_CHECK_EQUAL( component->GetNet( 0 ).GetPinName(), "1" );
    BOOST_CHECK_EQUAL( component->GetNet( 0 ).GetNetName(), "SUPPLY" );
    BOOST_CHECK( input->Matches( source ) );
    auto changed = source;
    changed.set_state_sha256( std::string( 64, 'b' ) );
    BOOST_CHECK( !input->Matches( changed ) ); // Same revision but different writer state.
}

BOOST_AUTO_TEST_CASE( RejectsWrongOwnerStateDigestAndUnsupportedEnvelope )
{
    const auto original = snapshot;
    auto other = board; other.mutable_project()->set_name( "other" );
    BOOST_CHECK_THROW( PCB_DRC_SCHEMATIC_INPUT::Capture( snapshot, snapshot.source_state(), other, epoch ), std::invalid_argument );
    BOOST_CHECK_THROW( PCB_DRC_SCHEMATIC_INPUT::Capture( snapshot, snapshot.source_state(), board, "other" ), std::invalid_argument );
    auto stale = snapshot.source_state(); stale.mutable_revision()->set_sequence( 18 );
    BOOST_CHECK_THROW( PCB_DRC_SCHEMATIC_INPUT::Capture( snapshot, stale, board, epoch ), std::invalid_argument );
    snapshot.set_netlist_sha256( std::string( 64, '0' ) );
    BOOST_CHECK_THROW( capture(), std::invalid_argument );
    snapshot = original; snapshot.set_schema_version( 2 );
    BOOST_CHECK_THROW( capture(), std::invalid_argument );
    snapshot = original; snapshot.mutable_source_state()->mutable_document()->clear_sheet_path();
    BOOST_CHECK_THROW( capture(), std::invalid_argument );
    snapshot = original;
    for( const std::string bad : {
            "(export (version E))",
            "(export (version F) (design) (components) (libparts) (libraries) (nets))",
            "(export (version E) (design) (components) (libparts) (libraries) (nets)",
            "(export (version E) (design) (components) (libparts) (libraries) (nets)) trailing",
            "(export (version E) (design) (components) (libparts) (libraries) (nets) (nets))" } )
    {
        payload( bad );
        BOOST_CHECK_THROW( capture(), std::exception );
    }
    snapshot = original;
    BOOST_CHECK_NO_THROW( capture() );
}

BOOST_AUTO_TEST_CASE( DrivesRealNativeParityAndRecoversAfterMatchingFootprintIsAdded )
{
    auto input = capture();
    BOARD native;
    auto& settings = native.GetDesignSettings();
    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
    settings.m_DRCSeverities[DRCE_MISSING_FOOTPRINT] = SEVERITY::RPT_SEVERITY_ERROR;
    settings.m_DRCEngine = std::make_shared<DRC_ENGINE>( &native, &settings );
    auto& engine = *settings.m_DRCEngine;
    engine.InitEngine( wxFileName() );
    engine.SetSchematicNetlist( &input->Netlist() );
    std::vector<int> findings;
    engine.SetViolationHandler( [&]( const auto& item, const auto&, int, const auto& )
                               { findings.push_back( item->GetErrorCode() ); } );
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, true, true ) == DRC_RUN_RESULT::COMPLETED );
    BOOST_REQUIRE_EQUAL( findings.size(), 1 );
    BOOST_CHECK_EQUAL( findings.front(), DRCE_MISSING_FOOTPRINT );
    auto* footprint = new FOOTPRINT( &native ); footprint->SetReference( "R1" ); native.Add( footprint );
    findings.clear();
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, true, true ) == DRC_RUN_RESULT::COMPLETED );
    BOOST_CHECK( findings.empty() );
    engine.SetSchematicNetlist( nullptr );
}

BOOST_AUTO_TEST_CASE( ExplicitEmptyNetlistIsNotMissingInput )
{
    payload( "(export (version E) (design) (components) (libparts) (libraries) (nets))" );
    auto input = capture();
    BOOST_CHECK_EQUAL( input->Netlist().GetCount(), 0 );
}

BOOST_AUTO_TEST_SUITE_END()
