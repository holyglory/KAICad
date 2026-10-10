/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, you may find one here:
 * http://www.gnu.org/licenses/old-licenses/gpl-2.0.html
 * or you may search the http://www.gnu.org website for the version 2 license,
 * or you may write to the Free Software Foundation, Inc.,
 * 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA
 */

#include <qa_utils/wx_utils/unit_test_utils.h>

#include <pcbnew_utils/board_test_utils.h>
#include <board.h>
#include <board_commit.h>
#include <board_design_settings.h>
#include <footprint.h>
#include <netinfo.h>
#include <pad.h>
#include <pcb_track.h>
#include <zone.h>
#include <settings/settings_manager.h>
#include <geometry/shape_poly_set.h>
#include <connectivity/connectivity_data.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <teardrop/teardrop.h>
#include <tool/tool_manager.h>


/**
 * The U2 corner of demos/royalblue54L_feather.  The QFN's 3.6 mm exposed pad is held by the GND
 * pour through thermal spokes so it wants no teardrop, but its centre falls in the thermal gap
 * punched for the via underneath it, where the fill reads as absent.
 */
BOOST_AUTO_TEST_CASE( NoTeardropOnZoneConnectedThermalPad )
{
    SETTINGS_MANAGER       settingsManager;
    std::unique_ptr<BOARD> board;

    KI_TEST::LoadBoard( settingsManager, "teardrop_qfn_thermal_pad_zone", board );
    KI_TEST::FillZones( board.get() );
    board->BuildConnectivity();

    TOOL_MANAGER toolMgr;
    toolMgr.SetEnvironment( board.get(), nullptr, nullptr, nullptr, nullptr );

    KI_TEST::DUMMY_TOOL* dummyTool = new KI_TEST::DUMMY_TOOL();
    toolMgr.RegisterTool( dummyTool );

    BOARD_COMMIT     commit( dummyTool );
    TEARDROP_MANAGER teardropMgr( board.get(), &toolMgr );

    teardropMgr.UpdateTeardrops( commit, nullptr, nullptr, true );

    if( !commit.Empty() )
        commit.Push( wxT( "Add teardrops" ), SKIP_UNDO | SKIP_SET_DIRTY );

    PAD* exposedPad = nullptr;

    for( FOOTPRINT* footprint : board->Footprints() )
    {
        for( PAD* pad : footprint->Pads() )
        {
            // The footprint splits the exposed pad into eleven sub-pads all numbered 33.
            if( footprint->GetReference() == wxT( "U2" ) && pad->GetNumber() == wxT( "33" )
                && pad->GetSize( F_Cu ).x > pcbIUScale.mmToIU( 3.0 ) )
            {
                exposedPad = pad;
            }
        }
    }

    BOOST_REQUIRE( exposedPad );

    SHAPE_POLY_SET padPoly;
    int            teardropsOnPad = 0;

    exposedPad->TransformShapeToPolygon( padPoly, F_Cu, 0,
                                         board->GetDesignSettings().m_MaxError, ERROR_INSIDE );

    for( ZONE* zone : board->Zones() )
    {
        if( !zone->IsTeardropArea() || zone->GetFirstLayer() != F_Cu )
            continue;

        SHAPE_POLY_SET overlap = *zone->Outline();
        overlap.BooleanIntersection( padPoly );

        if( !overlap.IsEmpty() )
            teardropsOnPad++;
    }

    BOOST_CHECK_EQUAL( teardropsOnPad, 0 );
}


BOOST_AUTO_TEST_CASE( SmoothConnectionBlendsWideTrackIntoThinPad )
{
    auto board = std::make_unique<BOARD>();
    board->Add( new NETINFO_ITEM( board.get(), wxT( "N1" ), 1 ) );

    FOOTPRINT* footprint = new FOOTPRINT( board.get() );
    board->Add( footprint );

    PAD* pad = new PAD( footprint );
    pad->SetPadstackMode( PADSTACK::MODE::NORMAL );
    pad->SetNumber( wxT( "1" ) );
    pad->SetAttribute( PAD_ATTRIB::SMD );
    pad->SetShape( PADSTACK::ALL_LAYERS, PAD_SHAPE::RECTANGLE );
    pad->SetSize( PADSTACK::ALL_LAYERS,
                  VECTOR2I( pcbIUScale.mmToIU( 2.0 ), pcbIUScale.mmToIU( 0.3 ) ) );
    pad->SetLayerSet( LSET::AllCuMask() );
    pad->SetPosition( VECTOR2I( pcbIUScale.mmToIU( 5.0 ), 0 ) );
    pad->SetNetCode( 1 );
    footprint->Add( pad );

    PCB_TRACK* track = new PCB_TRACK( board.get() );
    const VECTOR2I originalEnd( pcbIUScale.mmToIU( 5.0 ), 0 );
    track->SetStart( VECTOR2I( 0, 0 ) );
    track->SetEnd( originalEnd );
    track->SetWidth( pcbIUScale.mmToIU( 1.0 ) );
    track->SetLayer( F_Cu );
    track->SetNetCode( 1 );
    board->Add( track );
    track->SetNet( board->FindNet( 1 ) );

    PCB_VIA* via = new PCB_VIA( board.get() );
    via->SetPosition( VECTOR2I( 0, 0 ) );
    via->SetWidth( PADSTACK::ALL_LAYERS, pcbIUScale.mmToIU( 0.8 ) );
    via->SetDrill( pcbIUScale.mmToIU( 0.4 ) );
    via->SetNetCode( 1 );
    board->Add( via );

    BOARD_DESIGN_SETTINGS& settings = board->GetDesignSettings();
    board->SetLegacyTeardrops( false );
    settings.SetSmoothConnections( true );
    settings.SetSmoothConnectionRadius( pcbIUScale.mmToIU( 0.2 ) );
    settings.SetSmoothConnectionRadiusUsesPercent( false );
    board->BuildConnectivity();
    BOOST_REQUIRE_EQUAL( board->GetConnectivity()->GetConnectedPads( track ).size(), 1 );

    TOOL_MANAGER toolMgr;
    toolMgr.SetEnvironment( board.get(), nullptr, nullptr, nullptr, nullptr );
    KI_TEST::DUMMY_TOOL* dummyTool = new KI_TEST::DUMMY_TOOL();
    toolMgr.RegisterTool( dummyTool );

    BOARD_COMMIT commit( dummyTool );
    TEARDROP_MANAGER teardropMgr( board.get(), &toolMgr );
    teardropMgr.UpdateTeardrops( commit, nullptr, nullptr, true );

    int smoothZones = 0;


    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() && zone->GetTeardropAreaType() == TEARDROP_TYPE::TD_VIAPAD
            && zone->GetFirstLayer() == F_Cu )
        {
            ++smoothZones;
        }
    }

    BOOST_REQUIRE_EQUAL( smoothZones, 2 );
    BOOST_CHECK_LT( track->GetEnd().x, originalEnd.x );
    BOOST_CHECK_LE( track->GetEnd().x,
                    pad->GetPosition().x - pad->GetSize( F_Cu ).x / 2 - track->GetWidth() / 2 );

    SHAPE_POLY_SET padPoly;
    pad->TransformShapeToPolygon( padPoly, F_Cu, 0, settings.m_MaxError, ERROR_INSIDE );
    bool overlapsPad = false;

    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() && zone->GetFirstLayer() == F_Cu )
        {
            SHAPE_POLY_SET overlap = *zone->Outline();
            overlap.BooleanIntersection( padPoly );
            overlapsPad |= !overlap.IsEmpty();
        }
    }

    BOOST_CHECK( overlapsPad );

    // The unchanged capsule primitive must be hidden by the generated copper.  Merely moving
    // its centre outside the pad does not prevent the curved cap protruding through the spline.
    const double radius = track->GetWidth() / 2.0;
    bool capContained = true;

    for( int degree = -90; degree <= 90; ++degree )
    {
        const double angle = degree * M_PI / 180.0;
        const VECTOR2I point = track->GetEnd()
                               + VECTOR2I( KiROUND( radius * std::cos( angle ) ),
                                           KiROUND( radius * std::sin( angle ) ) );
        bool covered = padPoly.Contains( point, -1, settings.m_MaxError );

        for( ZONE* zone : board->Zones() )
        {
            if( zone->IsTeardropArea() && zone->GetFirstLayer() == F_Cu )
                covered |= zone->Outline()->Contains( point, -1, settings.m_MaxError );
        }

        capContained &= covered;
    }

    BOOST_CHECK_MESSAGE( capContained, "The track cap protrudes outside the smooth copper envelope" );

    // Run the real DRC engine against the generated transition.  Keep this fixture focused on
    // copper clearance so unrelated board-level checks cannot mask a transition violation.
    std::vector<DRC_ITEM> clearanceViolations;
    auto drcEngine = std::make_shared<DRC_ENGINE>( board.get(), &settings );
    drcEngine->InitEngine( wxFileName() );
    settings.m_DRCEngine = drcEngine;

    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;

    settings.m_DRCSeverities[DRCE_CLEARANCE] = SEVERITY::RPT_SEVERITY_ERROR;
    drcEngine->SetViolationHandler(
            [&]( const std::shared_ptr<DRC_ITEM>& aItem, const VECTOR2I&, int,
                 const std::function<void( PCB_MARKER* )>& )
            {
                if( aItem->GetErrorCode() == DRCE_CLEARANCE )
                    clearanceViolations.push_back( *aItem );
            } );
    drcEngine->RunTests( EDA_UNITS::MM, true, false );
    BOOST_CHECK( clearanceViolations.empty() );

    settings.SetSmoothConnections( false );
    BOARD_COMMIT restoreCommit( dummyTool );
    teardropMgr.UpdateTeardrops( restoreCommit, nullptr, nullptr, true );
    BOOST_CHECK_EQUAL( track->GetEnd(), originalEnd );

    int remainingSmoothZones = 0;

    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() && zone->GetFirstLayer() == F_Cu )
            ++remainingSmoothZones;
    }

    BOOST_CHECK_EQUAL( remainingSmoothZones, 0 );

    // A track can force smooth behavior even when the board default is off.
    track->SetConnectionMode( TRACK_CONNECTION_MODE::SMOOTH );
    BOARD_COMMIT forcedSmoothCommit( dummyTool );
    teardropMgr.UpdateTeardrops( forcedSmoothCommit, nullptr, nullptr, true );
    int forcedSmoothZones = 0;

    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() )
            ++forcedSmoothZones;
    }

    BOOST_CHECK_GE( forcedSmoothZones, 1 );

    // An explicit ordinary override wins over a board-wide smooth default.
    track->SetConnectionMode( TRACK_CONNECTION_MODE::ORDINARY );
    settings.SetSmoothConnections( true );
    BOARD_COMMIT ordinaryCommit( dummyTool );
    teardropMgr.UpdateTeardrops( ordinaryCommit, nullptr, nullptr, true );
    int ordinaryZones = 0;

    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() )
            ++ordinaryZones;
    }

    BOOST_CHECK_EQUAL( ordinaryZones, 0 );
}


BOOST_AUTO_TEST_CASE( SmoothConnectionMergesUnequalTracks )
{
    auto board = std::make_unique<BOARD>();
    board->Add( new NETINFO_ITEM( board.get(), wxT( "N1" ), 1 ) );

    PCB_TRACK* wide = new PCB_TRACK( board.get() );
    wide->SetStart( VECTOR2I( 0, 0 ) );
    wide->SetEnd( VECTOR2I( pcbIUScale.mmToIU( 5.0 ), 0 ) );
    wide->SetWidth( pcbIUScale.mmToIU( 1.0 ) );
    wide->SetLayer( F_Cu );
    wide->SetNetCode( 1 );
    wide->SetNet( board->FindNet( 1 ) );
    board->Add( wide );

    PCB_TRACK* thin = new PCB_TRACK( board.get() );
    thin->SetStart( VECTOR2I( pcbIUScale.mmToIU( 5.0 ), 0 ) );
    thin->SetEnd( VECTOR2I( pcbIUScale.mmToIU( 10.0 ), 0 ) );
    thin->SetWidth( pcbIUScale.mmToIU( 0.3 ) );
    thin->SetLayer( F_Cu );
    thin->SetNetCode( 1 );
    thin->SetNet( board->FindNet( 1 ) );
    board->Add( thin );

    BOARD_DESIGN_SETTINGS& settings = board->GetDesignSettings();
    board->SetLegacyTeardrops( false );
    settings.SetSmoothConnections( true );
    settings.SetSmoothConnectionRadius( pcbIUScale.mmToIU( 0.2 ) );
    settings.SetSmoothConnectionRadiusUsesPercent( false );
    board->BuildConnectivity();

    TOOL_MANAGER toolMgr;
    toolMgr.SetEnvironment( board.get(), nullptr, nullptr, nullptr, nullptr );
    KI_TEST::DUMMY_TOOL* dummyTool = new KI_TEST::DUMMY_TOOL();
    toolMgr.RegisterTool( dummyTool );
    BOARD_COMMIT commit( dummyTool );
    TEARDROP_MANAGER teardropMgr( board.get(), &toolMgr );
    teardropMgr.UpdateTeardrops( commit, nullptr, nullptr, true );

    int trackEndZones = 0;

    for( ZONE* zone : board->Zones() )
    {
        if( zone->IsTeardropArea() && zone->GetTeardropAreaType() == TEARDROP_TYPE::TD_TRACKEND )
            ++trackEndZones;
    }

    BOOST_CHECK_GE( trackEndZones, 1 );
    BOOST_CHECK( wide->GetEnd().x < pcbIUScale.mmToIU( 5.0 )
                 || thin->GetStart().x > pcbIUScale.mmToIU( 5.0 ) );
}
