/* Real native document snapshots, not complete-project DRC qualification. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/pcb_drc_document_snapshot.h>
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_item.h>
#include <footprint.h>
#include <netinfo.h>
#include <pad.h>
#include <pcb_group.h>
#include <pcb_marker.h>
#include <pcb_text.h>
#include <pcb_track.h>
#include <zone.h>
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

// Every object of the open board keeps its identity in the check's copy, including a teardrop,
// which KiCad writes without one. The editor's current variant and the zone connections of the
// last fill, which live only in memory, reach the copy too.
BOOST_AUTO_TEST_CASE( CopyKeepsEveryObjectIdentityTheCurrentVariantAndFillConnections )
{
    BOARD source;
    source.SetFileName( "identities.kicad_pcb" );
    source.SetCopperLayerCount( 2 );
    auto* net = new NETINFO_ITEM( &source, "SIGNAL", 1 );
    source.Add( net );
    auto* footprint = new FOOTPRINT( &source );
    footprint->SetPosition( { 5000000, 5000000 } );
    footprint->Reference().SetText( "R1" );
    source.Add( footprint );
    auto* pad = new PAD( footprint );
    pad->SetNumber( "1" );
    pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
    pad->SetAttribute( PAD_ATTRIB::PTH );
    pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::CIRCLE );
    pad->SetSize( PADSTACK::ALL_LAYERS, { 1500000, 1500000 } );
    pad->SetDrillSize( { 800000, 800000 } );
    pad->SetLayerSet( PAD::PTHMask() );
    pad->SetPosition( footprint->GetPosition() );
    pad->SetNet( net );
    footprint->Add( pad );
    auto* track = new PCB_TRACK( &source );
    track->SetStart( { 5000000, 5000000 } ); track->SetEnd( { 9000000, 5000000 } );
    track->SetWidth( 200000 ); track->SetLayer( F_Cu ); track->SetNet( net );
    source.Add( track );
    auto* via = new PCB_VIA( &source );
    via->SetPadstackMode( PADSTACK::MODE::NORMAL );
    via->SetViaType( VIATYPE::THROUGH );
    via->SetLayerPair( F_Cu, B_Cu );
    via->SetPosition( { 9000000, 5000000 } );
    via->SetWidth( PADSTACK::ALL_LAYERS, 600000 );
    via->SetDrill( 300000 );
    via->SetNet( net );
    source.Add( via );
    auto* group = new PCB_GROUP( &source );
    group->AddItem( track );
    source.Add( group );
    auto* text = new PCB_TEXT( &source );
    text->SetText( "${VARIANT}" ); text->SetLayer( F_SilkS );
    source.Add( text );
    auto* teardrop = new ZONE( &source );
    teardrop->SetTeardropAreaType( TEARDROP_TYPE::TD_VIAPAD );
    teardrop->SetLayer( F_Cu );
    teardrop->SetNetCode( net->GetNetCode() );
    for( const VECTOR2I& corner : { VECTOR2I( 8600000, 4900000 ), VECTOR2I( 8900000, 4700000 ),
                                    VECTOR2I( 8900000, 5300000 ), VECTOR2I( 8600000, 5100000 ) } )
        teardrop->AppendCorner( corner, -1 );
    source.Add( teardrop );
    source.AddVariant( "Assembly" );
    source.SetCurrentVariant( "Assembly" );
    pad->SetZoneLayerOverride( F_Cu, ZLO_FORCE_FLASHED );
    via->SetZoneLayerOverride( B_Cu, ZLO_FORCE_NO_ZONE_CONNECTION );

    auto snapshot = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( source );
    BOARD& copy = snapshot->GetBoard();
    BOOST_CHECK_MESSAGE( snapshot->IdentityGap().Empty(),
                         "missing " << snapshot->IdentityGap().missing << ", unexpected "
                                    << snapshot->IdentityGap().unexpected << ", shared "
                                    << snapshot->IdentityGap().shared );
    for( const BOARD_ITEM* item : { static_cast<const BOARD_ITEM*>( footprint ), static_cast<const BOARD_ITEM*>( pad ),
                                    static_cast<const BOARD_ITEM*>( &footprint->Reference() ),
                                    static_cast<const BOARD_ITEM*>( &footprint->Value() ),
                                    static_cast<const BOARD_ITEM*>( track ), static_cast<const BOARD_ITEM*>( via ),
                                    static_cast<const BOARD_ITEM*>( group ), static_cast<const BOARD_ITEM*>( text ),
                                    static_cast<const BOARD_ITEM*>( teardrop ), static_cast<const BOARD_ITEM*>( &source ) } )
    {
        BOOST_CHECK( snapshot->SourceIdentities().contains( item->m_Uuid ) );
        const BOARD_ITEM* copied = copy.ResolveItem( item->m_Uuid, true );
        BOOST_REQUIRE_MESSAGE( copied, "The copy lost " << item->GetClass() << " " << item->m_Uuid.AsStdString() );
        BOOST_CHECK( copied != item );
        BOOST_CHECK( copied->Type() == item->Type() );
    }
    BOOST_REQUIRE_EQUAL( copy.Zones().size(), 1 );
    BOOST_CHECK( copy.Zones().front()->IsTeardropArea() );
    BOOST_CHECK( copy.Zones().front()->m_Uuid == teardrop->m_Uuid );
    BOOST_CHECK( snapshot->SourceTeardrop( *copy.Zones().front() ) == teardrop->m_Uuid );
    BOOST_CHECK( copy.GetCurrentVariant() == "Assembly" );
    auto* copiedPad = static_cast<const PAD*>( copy.ResolveItem( pad->m_Uuid ) );
    auto* copiedVia = static_cast<const PCB_VIA*>( copy.ResolveItem( via->m_Uuid ) );
    BOOST_CHECK( copiedPad->GetZoneLayerOverride( F_Cu ) == ZLO_FORCE_FLASHED );
    BOOST_CHECK( copiedPad->GetZoneLayerOverride( B_Cu ) == ZLO_NONE );
    BOOST_CHECK( copiedVia->GetZoneLayerOverride( B_Cu ) == ZLO_FORCE_NO_ZONE_CONNECTION );
    BOOST_CHECK( copiedVia->GetZoneLayerOverride( F_Cu ) == ZLO_NONE );

    // An identity that two objects of the open board hold cannot name one object: the copy says so.
    auto* twin = new PCB_TRACK( &source );
    twin->SetStart( { 1000000, 1000000 } ); twin->SetEnd( { 2000000, 1000000 } );
    twin->SetWidth( 200000 ); twin->SetLayer( B_Cu );
    twin->SetUuidDirect( track->m_Uuid );
    source.Add( twin );
    auto ambiguous = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( source );
    BOOST_CHECK( !ambiguous->IdentityGap().Empty() );
    BOOST_CHECK_EQUAL( ambiguous->IdentityGap().shared, 1 );
}

BOOST_AUTO_TEST_SUITE_END()
