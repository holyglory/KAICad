/* Real native document snapshots, not complete-project DRC qualification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/pcb_drc_document_snapshot.h>
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_item.h>
#include <pcb_marker.h>
#include <pcb_track.h>
#include <pcbnew_utils/board_test_utils.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <settings/settings_manager.h>
#include <json_common.h>
#include <fstream>

BOOST_AUTO_TEST_SUITE( DrcDocumentSnapshot )

BOOST_AUTO_TEST_CASE( StandaloneBoardKeepsIdentityGeometryAndUnsavedRules )
{
    BOARD source;
    source.SetFileName( "unsaved.kicad_pcb" );
    auto* track = new PCB_TRACK( &source );
    track->SetStart( { 1000000, 2000000 } );
    track->SetEnd( { 1000000, 5000000 } );
    track->SetWidth( 230000 );
    track->SetLayer( B_Cu );
    source.Add( track );
    auto& settings = source.GetDesignSettings();
    settings.m_MinClearance = 330000;
    settings.m_NetSettings->GetDefaultNetclass()->SetTrackWidth( 420000 );
    const auto before = settings.CaptureCurrentState();
    const auto beforeNets = settings.m_NetSettings->CaptureCurrentState();
    const int sequence = source.GetTimeStamp();
    auto snapshot = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( source );
    BOOST_CHECK_EQUAL( snapshot->SourceSequence(), sequence );
    auto& board = snapshot->GetBoard();
    BOOST_CHECK( board.m_Uuid == source.m_Uuid );
    BOOST_CHECK( board.GetProject() == nullptr );
    BOOST_REQUIRE_EQUAL( board.Tracks().size(), 1 );
    auto* copied = board.Tracks().front();
    BOOST_CHECK( copied != track );
    BOOST_CHECK( copied->m_Uuid == track->m_Uuid );
    BOOST_CHECK( copied->GetStart() == track->GetStart() );
    BOOST_CHECK( copied->GetEnd() == track->GetEnd() );
    BOOST_CHECK_EQUAL( copied->GetWidth(), track->GetWidth() );
    BOOST_CHECK( copied->GetLayer() == B_Cu );
    BOOST_CHECK( board.GetDesignSettings().CaptureCurrentState() == before );
    BOOST_CHECK( board.GetDesignSettings().m_NetSettings->CaptureCurrentState() == beforeNets );
    board.GetDesignSettings().m_MinClearance = 700000;
    board.GetDesignSettings().m_NetSettings->GetDefaultNetclass()->SetTrackWidth( 800000 );
    copied->SetPosition( { 9000000, 9000000 } );
    BOOST_CHECK( settings.CaptureCurrentState() == before );
    BOOST_CHECK( settings.m_NetSettings->CaptureCurrentState() == beforeNets );
    BOOST_CHECK( track->GetStart() == VECTOR2I( 1000000, 2000000 ) );
    BOOST_CHECK_EQUAL( source.GetTimeStamp(), sequence );
}

BOOST_AUTO_TEST_CASE( ProjectBoardKeepsEffectiveExclusionsWithoutBorrowingItsOwner )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_document_" + KIID().AsStdString(), "" );
    const auto path = scratch.GetPath() / "fixture.kicad_pro";
    { std::ofstream stream( path ); stream << R"({"meta":{"filename":"fixture.kicad_pro","version":3}})"; }
    const wxString filename = wxString::FromUTF8( path.string() );
    SETTINGS_MANAGER manager;
    BOOST_REQUIRE( manager.LoadProject( filename, false ) );
    PROJECT* project = manager.GetProject( filename );
    BOOST_REQUIRE( project );
    std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> snapshot;
    {
        BOARD source;
        source.SetFileName( wxString::FromUTF8( ( scratch.GetPath() / "fixture.kicad_pcb" ).string() ) );
        source.SetProject( project );
        project->GetTextVars()["SUPPLY"] = "1.8 V";
        auto& settings = source.GetDesignSettings();
        settings.m_MinClearance = 440000;
        settings.m_NetSettings->GetDefaultNetclass()->SetTrackWidth( 550000 );
        auto item = DRC_ITEM::Create( DRCE_INVALID_OUTLINE );
        item->SetItems( &source );
        auto* marker = new PCB_MARKER( item, { 0, 0 }, Edge_Cuts );
        source.Add( marker );
        marker->SetExcluded( true, "stored comment" );
        settings.m_DrcExclusions.insert( DRC_EXCLUSION::FromMarker( *marker ) );
        marker->SetExcluded( true, "unsaved comment" );
        const auto before = project->GetProjectFile().CaptureCurrentState();

        snapshot = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( source );
        auto& copied = snapshot->GetBoard();
        BOOST_CHECK( copied.m_Uuid == source.m_Uuid );
        BOOST_REQUIRE( copied.GetProject() );
        BOOST_CHECK( copied.GetProject() != project );
        BOOST_CHECK( copied.GetProject()->IsReadOnly() );
        BOOST_CHECK_EQUAL( copied.GetDesignSettings().m_MinClearance, 440000 );
        BOOST_CHECK( copied.GetDesignSettings().m_NetSettings == copied.GetProject()->GetProjectFile().NetSettings() );
        BOOST_REQUIRE_EQUAL( copied.GetDesignSettings().m_DrcExclusions.size(), 1 );
        BOOST_CHECK( copied.GetDesignSettings().m_DrcExclusions.begin()->GetComment() == "unsaved comment" );
        BOOST_CHECK( project->GetProjectFile().CaptureCurrentState() == before );
        copied.GetProject()->GetTextVars()["SUPPLY"] = "3.3 V";
        BOOST_CHECK( project->GetTextVars().at( "SUPPLY" ) == "1.8 V" );
    }
    BOOST_CHECK( manager.UnloadProject( project, false ) );
    auto& copied = snapshot->GetBoard();
    BOOST_CHECK_EQUAL( copied.GetDesignSettings().m_NetSettings->GetDefaultNetclass()->GetTrackWidth(), 550000 );
    BOOST_CHECK( copied.GetProject()->GetTextVars().at( "SUPPLY" ) == "3.3 V" );
}

BOOST_AUTO_TEST_SUITE_END()
