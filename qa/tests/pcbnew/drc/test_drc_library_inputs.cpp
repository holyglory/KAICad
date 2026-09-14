/* Frozen real library content for native checks. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_design_settings.h>
#include <cli_progress_reporter.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <drc/drc_library_inputs.h>
#include <drc/drc_run_scope.h>
#include <footprint.h>
#include <footprint_library_adapter.h>
#include <libraries/library_manager.h>
#include <libraries/library_table.h>
#include <pad.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcbnew_utils/board_test_utils.h>

namespace
{
struct LIBRARY_FIXTURE
{
    KI_TEST::TEMPORARY_DIRECTORY scratch{ "drc_library_" + KIID().AsStdString(), "" };
    wxString nickname = wxString::FromUTF8( "captured_" + KIID().AsStdString() );
    wxString path = wxString::FromUTF8( ( scratch.GetPath() / "local.pretty" ).string() );
    FOOTPRINT original{ nullptr };
    PCB_IO_KICAD_SEXPR io;

    LIBRARY_FIXTURE()
    {
        std::filesystem::create_directory( scratch.GetPath() / "local.pretty" );
        original.SetFPID( LIB_ID( nickname, "Part" ) );
        auto* pad = new PAD( &original );
        pad->SetNumber( "1" );
        pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
        pad->SetAttribute( PAD_ATTRIB::SMD );
        pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::RECTANGLE );
        pad->SetSize( PADSTACK::ALL_LAYERS, { 800000, 800000 } );
        pad->SetLayerSet( LSET( { F_Cu } ) );
        original.Add( pad );
        io.FootprintSave( path, &original );

        LIBRARY_TABLE table( wxFileName( wxString::FromUTF8( ( scratch.GetPath() / "fp-lib-table" ).string() ) ),
                             LIBRARY_TABLE_SCOPE::PROJECT, LIBRARY_TABLE_TYPE::FOOTPRINT );
        table.SetType( LIBRARY_TABLE_TYPE::FOOTPRINT ); table.SetOk();
        auto add = [&]( const wxString& name, const wxString& uri, bool disabled )
        {
            auto& row = table.InsertRow();
            row.SetNickname( name ); row.SetType( "KiCad" ); row.SetURI( uri ); row.SetDisabled( disabled );
        };
        add( nickname, path, false );
        add( nickname + "_disabled", path, true );
        add( nickname + "_unavailable", path + "_absent", false );
        BOOST_REQUIRE( table.Save().has_value() );
    }

    void Load( LIBRARY_MANAGER& manager )
    {
        manager.LoadProjectTables( wxString::FromUTF8( scratch.GetPath().string() ),
                                   { LIBRARY_TABLE_TYPE::FOOTPRINT } );
    }

    FOOTPRINT* Place( BOARD& board, const LIB_ID& id )
    {
        auto* footprint = static_cast<FOOTPRINT*>( original.Clone() );
        footprint->SetFPID( id ); footprint->SetParent( &board ); board.Add( footprint );
        return footprint;
    }
};

std::vector<int> Check( BOARD& board, std::shared_ptr<const DRC_LIBRARY_INPUTS> inputs )
{
    auto& settings = board.GetDesignSettings();
    settings.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &settings );
    auto& engine = *settings.m_DRCEngine;
    engine.InitEngine( wxFileName() );
    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
    settings.m_DRCSeverities[DRCE_LIB_FOOTPRINT_ISSUES] = SEVERITY::RPT_SEVERITY_ERROR;
    settings.m_DRCSeverities[DRCE_LIB_FOOTPRINT_MISMATCH] = SEVERITY::RPT_SEVERITY_ERROR;
    std::vector<int> findings;
    bool running = false;
    {
        DRC_RUN_SCOPE scope( engine, running );
        engine.SetLibraryInputs( std::move( inputs ) );
        engine.SetViolationHandler( [&]( const auto& item, const auto&, int, const auto& )
                                    { findings.push_back( item->GetErrorCode() ); } );
        BOOST_REQUIRE( engine.RunTests( EDA_UNITS::MM, true, false ) == DRC_RUN_RESULT::COMPLETED );
    }
    BOOST_CHECK( !running );
    BOOST_CHECK( engine.GetLibraryInputs() == nullptr );
    return findings;
}
}

BOOST_AUTO_TEST_SUITE( DrcLibraryInputs )

BOOST_FIXTURE_TEST_CASE( CapturedLibraryOutlivesAdapterAndDoesNotFollowLaterEdits, LIBRARY_FIXTURE )
{
    BOARD board;
    Place( board, original.GetFPID() );
    std::shared_ptr<const DRC_LIBRARY_INPUTS> before;
    {
        LIBRARY_MANAGER manager; Load( manager );
        FOOTPRINT_LIBRARY_ADAPTER adapter( manager );
        auto loaded = adapter.LoadOne( nickname );
        BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
        before = DRC_LIBRARY_INPUTS::Capture( board, adapter );
    }
    BOOST_REQUIRE( before ); BOOST_REQUIRE_EQUAL( before->Size(), 1 );
    BOOST_REQUIRE( before->Find( original.GetFPID() )->status == DRC_LIBRARY_INPUTS::STATUS::LOADED );
    BOOST_REQUIRE( before->Find( original.GetFPID() )->footprint->Pads().front()->GetSize( F_Cu )
                   == VECTOR2I( 800000, 800000 ) );
    BOOST_CHECK( Check( board, before ).empty() );

    const auto footprintPath = scratch.GetPath() / "local.pretty" / "Part.kicad_mod";
    const auto timestamp = std::filesystem::last_write_time( footprintPath );
    const auto bytes = std::filesystem::file_size( footprintPath );
    original.Pads().front()->SetSize( PADSTACK::ALL_LAYERS, { 1400000, 1400000 } );
    io.FootprintSave( path, &original );
    std::filesystem::last_write_time( footprintPath, timestamp );
    BOOST_REQUIRE_EQUAL( std::filesystem::file_size( footprintPath ), bytes );
    std::shared_ptr<const DRC_LIBRARY_INPUTS> after;
    {
        LIBRARY_MANAGER manager; Load( manager );
        FOOTPRINT_LIBRARY_ADAPTER adapter( manager );
        auto loaded = adapter.LoadOne( nickname );
        BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
        after = DRC_LIBRARY_INPUTS::Capture( board, adapter );
    }
    BOOST_REQUIRE( after );
    BOOST_REQUIRE( after->Find( original.GetFPID() )->footprint );
    BOOST_REQUIRE_MESSAGE( after->Find( original.GetFPID() )->footprint->Pads().front()->GetSize( F_Cu )
                           == VECTOR2I( 1400000, 1400000 ),
                           "A new capture must read the changed library pad size" );
    BOOST_CHECK( Check( board, before ).empty() );
    auto mismatches = Check( board, after );
    BOOST_REQUIRE_EQUAL( mismatches.size(), 1 );
    BOOST_CHECK_EQUAL( mismatches.front(), DRCE_LIB_FOOTPRINT_MISMATCH );
}

BOOST_FIXTURE_TEST_CASE( MissingDisabledAndUnavailableInputsAreExplicitAndCancellationIsAtomic, LIBRARY_FIXTURE )
{
    BOARD board;
    const LIB_ID missing( nickname + "_missing", "Part" );
    const LIB_ID disabled( nickname + "_disabled", "Part" );
    const LIB_ID unavailable( nickname + "_unavailable", "Part" );
    const LIB_ID absentPart( nickname, "AbsentPart" );
    for( const auto& id : { missing, disabled, unavailable, absentPart } ) Place( board, id );
    LIBRARY_MANAGER manager; Load( manager );
    FOOTPRINT_LIBRARY_ADAPTER adapter( manager );
    auto loaded = adapter.LoadOne( nickname );
    BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
    auto captured = DRC_LIBRARY_INPUTS::Capture( board, adapter );
    BOOST_REQUIRE( captured ); BOOST_REQUIRE_EQUAL( captured->Size(), 4 );
    BOOST_CHECK( captured->Find( missing )->status == DRC_LIBRARY_INPUTS::STATUS::MISSING_LIBRARY );
    BOOST_CHECK( captured->Find( disabled )->status == DRC_LIBRARY_INPUTS::STATUS::DISABLED_LIBRARY );
    BOOST_CHECK( captured->Find( unavailable )->status == DRC_LIBRARY_INPUTS::STATUS::UNAVAILABLE_LIBRARY );
    BOOST_CHECK( captured->Find( absentPart )->status == DRC_LIBRARY_INPUTS::STATUS::UNAVAILABLE_FOOTPRINT );
    auto findings = Check( board, captured );
    BOOST_REQUIRE_EQUAL( findings.size(), 4 );
    for( int code : findings ) BOOST_CHECK_EQUAL( code, DRCE_LIB_FOOTPRINT_ISSUES );

    class CANCEL_AFTER_FIRST_ENTRY : public CLI_PROGRESS_REPORTER
    {
    public:
        bool IsCancelled() const override { return ++checks > 1; }
        mutable int checks = 0;
    } cancelled;
    BOOST_CHECK( !DRC_LIBRARY_INPUTS::Capture( board, adapter, &cancelled ) );
}

BOOST_FIXTURE_TEST_CASE( AnIncompleteCapturedCatalogueCannotProduceACompletedCheck, LIBRARY_FIXTURE )
{
    LIBRARY_MANAGER manager; Load( manager );
    FOOTPRINT_LIBRARY_ADAPTER adapter( manager );
    BOARD empty;
    auto incomplete = DRC_LIBRARY_INPUTS::Capture( empty, adapter );
    BOOST_REQUIRE( incomplete ); BOOST_CHECK_EQUAL( incomplete->Size(), 0 );
    BOARD board;
    Place( board, original.GetFPID() );
    auto& settings = board.GetDesignSettings();
    settings.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &settings );
    auto& engine = *settings.m_DRCEngine;
    engine.InitEngine( wxFileName() );
    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
    settings.m_DRCSeverities[DRCE_LIB_FOOTPRINT_ISSUES] = SEVERITY::RPT_SEVERITY_ERROR;
    settings.m_DRCSeverities[DRCE_LIB_FOOTPRINT_MISMATCH] = SEVERITY::RPT_SEVERITY_ERROR;
    bool running = false;
    {
        DRC_RUN_SCOPE scope( engine, running );
        engine.SetLibraryInputs( incomplete );
        BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, true, false ) == DRC_RUN_RESULT::INCOMPLETE );
    }
    BOOST_CHECK( engine.GetLibraryInputs() == nullptr );
}

BOOST_AUTO_TEST_SUITE_END()
