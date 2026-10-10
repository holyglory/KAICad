/*
 * KiRouter - a push-and-(sometimes-)shove PCB router
 *
 * Copyright (C) 2014  CERN
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * Author: Maciej Suminski <maciej.suminski@cern.ch>
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

/**
 * Push and Shove router settings dialog.
 */

#include "dialog_pns_settings.h"
#include <router/pns_routing_settings.h>

#include <board_design_settings.h>
#include <board.h>
#include <pcb_edit_frame.h>
#include <wx/stattext.h>

DIALOG_PNS_SETTINGS::DIALOG_PNS_SETTINGS( wxWindow* aParent, PNS::ROUTING_SETTINGS& aSettings,
                                          BOARD_DESIGN_SETTINGS* aBoardSettings ) :
        DIALOG_PNS_SETTINGS_BASE( aParent ),
        m_settings( aSettings ),
        m_boardSettings( aBoardSettings ),
        m_smoothConnections( nullptr ),
        m_smoothRadiusPercentMode( nullptr ),
        m_smoothRadiusMm( nullptr ),
        m_smoothRadiusPercent( nullptr ),
        m_flexibleRouting( nullptr ),
        m_roundCorners( nullptr ),
        m_cornerRadiusPercentMode( nullptr ),
        m_cornerRadiusMm( nullptr ),
        m_cornerRadiusPercent( nullptr )
{
    if( !m_boardSettings )
    {
        if( PCB_EDIT_FRAME* frame = dynamic_cast<PCB_EDIT_FRAME*>( aParent ) )
        m_boardSettings = &frame->GetBoard()->GetDesignSettings();
    }

    wxStaticBoxSizer* smoothSizer =
            new wxStaticBoxSizer( new wxStaticBox( this, wxID_ANY, _( "Board-wide connection and path behavior" ) ),
                                  wxVERTICAL );

    m_smoothConnections = new wxCheckBox( smoothSizer->GetStaticBox(), wxID_ANY,
                                          _( "Enable smooth connections by default" ) );
    m_smoothConnections->SetToolTip( _( "Blend routed tracks into pads, vias, and unequal-width tracks while respecting DRC." ) );
    smoothSizer->Add( m_smoothConnections, 0, wxALL, 5 );

    wxBoxSizer* radiusSizer = new wxBoxSizer( wxHORIZONTAL );
    radiusSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "Connection radius:" ) ),
                      0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 5 );
    m_smoothRadiusMm = new wxSpinCtrlDouble( smoothSizer->GetStaticBox(), wxID_ANY );
    m_smoothRadiusMm->SetRange( 0.001, 25.0 );
    m_smoothRadiusMm->SetIncrement( 0.01 );
    m_smoothRadiusMm->SetDigits( 3 );
    radiusSizer->Add( m_smoothRadiusMm, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 4 );
    radiusSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "mm" ) ),
                      0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 12 );
    m_smoothRadiusPercentMode = new wxCheckBox( smoothSizer->GetStaticBox(), wxID_ANY,
                                                _( "Use track-width percentage" ) );
    radiusSizer->Add( m_smoothRadiusPercentMode, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 5 );
    m_smoothRadiusPercent = new wxSpinCtrlDouble( smoothSizer->GetStaticBox(), wxID_ANY );
    m_smoothRadiusPercent->SetRange( 0.0, 1000.0 );
    m_smoothRadiusPercent->SetIncrement( 5.0 );
    m_smoothRadiusPercent->SetDigits( 1 );
    radiusSizer->Add( m_smoothRadiusPercent, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 4 );
    radiusSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "% of track width" ) ),
                      0, wxALIGN_CENTER_VERTICAL );
    smoothSizer->Add( radiusSizer, 0, wxLEFT | wxRIGHT | wxBOTTOM, 5 );

    wxBoxSizer* cornerSizer = new wxBoxSizer( wxHORIZONTAL );
    cornerSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "Rounded route corner radius:" ) ),
                      0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 5 );
    m_cornerRadiusMm = new wxSpinCtrlDouble( smoothSizer->GetStaticBox(), wxID_ANY );
    m_cornerRadiusMm->SetRange( 0.001, 25.0 );
    m_cornerRadiusMm->SetIncrement( 0.01 );
    m_cornerRadiusMm->SetDigits( 3 );
    cornerSizer->Add( m_cornerRadiusMm, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 4 );
    cornerSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "mm" ) ),
                      0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 12 );
    m_cornerRadiusPercentMode = new wxCheckBox( smoothSizer->GetStaticBox(), wxID_ANY,
                                                _( "Use track-width percentage" ) );
    cornerSizer->Add( m_cornerRadiusPercentMode, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 5 );
    m_cornerRadiusPercent = new wxSpinCtrlDouble( smoothSizer->GetStaticBox(), wxID_ANY );
    m_cornerRadiusPercent->SetRange( 0.0, 1000.0 );
    m_cornerRadiusPercent->SetIncrement( 5.0 );
    m_cornerRadiusPercent->SetDigits( 1 );
    cornerSizer->Add( m_cornerRadiusPercent, 0, wxALIGN_CENTER_VERTICAL | wxRIGHT, 4 );
    cornerSizer->Add( new wxStaticText( smoothSizer->GetStaticBox(), wxID_ANY, _( "% of track width" ) ),
                      0, wxALIGN_CENTER_VERTICAL );
    smoothSizer->Add( cornerSizer, 0, wxLEFT | wxRIGHT | wxBOTTOM, 5 );

    m_roundCorners = new wxCheckBox( smoothSizer->GetStaticBox(), wxID_ANY,
                                     _( "Round route corners" ) );
    m_roundCorners->SetToolTip( _( "Use tangent arcs at route corners instead of sharp miters." ) );
    smoothSizer->Add( m_roundCorners, 0, wxLEFT | wxRIGHT | wxBOTTOM, 5 );

    m_flexibleRouting = new wxCheckBox( smoothSizer->GetStaticBox(), wxID_ANY,
                                        _( "Enable flexible DRC-safe paths" ) );
    m_flexibleRouting->SetToolTip( _( "Allow arbitrary angles and rounded path candidates in Walk Around and Shove routing." ) );
    smoothSizer->Add( m_flexibleRouting, 0, wxALL, 5 );

    if( m_boardSettings )
    {
        m_smoothConnections->SetValue( m_boardSettings->SmoothConnections() );
        m_smoothRadiusMm->SetValue( pcbIUScale.IUTomm( m_boardSettings->SmoothConnectionRadius() ) );
        m_smoothRadiusPercent->SetValue( m_boardSettings->SmoothConnectionRadiusPercent() );
        m_smoothRadiusPercentMode->SetValue( m_boardSettings->SmoothConnectionRadiusUsesPercent() );
    }

    m_cornerRadiusMm->SetValue( pcbIUScale.IUTomm( m_settings.CornerRadius() ) );
    m_cornerRadiusPercent->SetValue( m_settings.CornerRadiusPercent() );
    m_cornerRadiusPercentMode->SetValue( m_settings.CornerRadiusUsesPercent() );
    m_roundCorners->SetValue( m_settings.GetCornerMode() == DIRECTION_45::CORNER_MODE::ROUNDED_45
                              || m_settings.GetCornerMode() == DIRECTION_45::CORNER_MODE::ROUNDED_90 );

    // Both units stay visible and editable.  The checkbox records which unit is used for
    // routing, while editing either field updates the other field from the active track width.
    // This keeps the two representations useful for comparison instead of hiding one of them.
    auto currentWidthMm = [this]
    {
        if( m_boardSettings )
        {
            const double width = pcbIUScale.IUTomm( m_boardSettings->GetCurrentTrackWidth() );

            if( width > 0.0 )
                return width;
        }

        return 0.2;
    };

    if( m_boardSettings && m_smoothRadiusPercentMode->GetValue() )
        m_smoothRadiusMm->SetValue( currentWidthMm() * m_smoothRadiusPercent->GetValue() / 100.0 );

    m_smoothRadiusMm->Bind( wxEVT_SPINCTRLDOUBLE, [this, currentWidthMm]( wxCommandEvent& )
    {
        if( !m_boardSettings )
            return;

        const double radius = m_smoothRadiusMm->GetValue();
        m_smoothRadiusPercent->SetValue( radius > 0.0 ? 100.0 * radius / currentWidthMm() : 0.0 );
        m_smoothRadiusPercentMode->SetValue( false );
    } );

    m_smoothRadiusPercent->Bind( wxEVT_SPINCTRLDOUBLE, [this, currentWidthMm]( wxCommandEvent& )
    {
        if( !m_boardSettings )
            return;

        const double width = pcbIUScale.IUTomm( m_boardSettings->GetCurrentTrackWidth() ) > 0.0
                                      ? pcbIUScale.IUTomm( m_boardSettings->GetCurrentTrackWidth() )
                                      : 0.2;
        m_smoothRadiusMm->SetValue( width * m_smoothRadiusPercent->GetValue() / 100.0 );
        m_smoothRadiusPercentMode->SetValue( true );
    } );

    if( m_cornerRadiusPercentMode->GetValue() )
        m_cornerRadiusMm->SetValue( currentWidthMm() * m_cornerRadiusPercent->GetValue() / 100.0 );

    m_cornerRadiusMm->Bind( wxEVT_SPINCTRLDOUBLE, [this, currentWidthMm]( wxCommandEvent& )
    {
        const double width = currentWidthMm();
        m_cornerRadiusPercent->SetValue( width > 0.0 ? 100.0 * m_cornerRadiusMm->GetValue() / width : 0.0 );
        m_cornerRadiusPercentMode->SetValue( false );
    } );

    m_cornerRadiusPercent->Bind( wxEVT_SPINCTRLDOUBLE, [this, currentWidthMm]( wxCommandEvent& )
    {
        const double width = currentWidthMm();
        m_cornerRadiusMm->SetValue( width * m_cornerRadiusPercent->GetValue() / 100.0 );
        m_cornerRadiusPercentMode->SetValue( true );
    } );

    m_flexibleRouting->SetValue( m_settings.FlexibleRouting() );
    GetSizer()->Insert( 0, smoothSizer, 0, wxEXPAND | wxALL, 5 );
    Layout();
    GetSizer()->Fit( this );

    // Load widgets' values from settings
    switch( m_settings.Mode() )
    {
    case PNS::RM_MarkObstacles: m_rbMarkObstacles->SetValue( true ); break;
    case PNS::RM_Shove:         m_rbShove->SetValue( true );         break;
    case PNS::RM_Walkaround:    m_rbWalkaround->SetValue( true );    break;
    }

    m_shoveVias->SetValue( m_settings.ShoveVias() );
    m_backPressure->SetValue( m_settings.JumpOverObstacles() );
    m_removeLoops->SetValue( m_settings.RemoveLoops() );
    m_suggestEnding->SetValue( m_settings.SuggestFinish() );
    m_smartPads->SetValue( m_settings.SmartPads() );
    m_smoothDragged->SetValue( m_settings.SmoothDraggedSegments() );
    m_violateDrc->SetValue( m_settings.GetAllowDRCViolationsSetting() );
    m_freeAngleMode->SetValue( m_settings.GetFreeAngleMode() );
    m_optimizeEntireDraggedTrack->SetValue( m_settings.GetOptimizeEntireDraggedTrack() );
    m_autoPosture->SetValue( m_settings.GetAutoPosture() );
    m_fixAllSegments->SetValue( m_settings.GetFixAllSegments() );
    m_restrictAngles->SetValue( m_settings.GetRestrictAngles() );

    // Enable/disable some options
    wxCommandEvent event;
    onModeChange( event );

    // Don't show options that are not implemented
    m_suggestEnding->Hide();

    SetupStandardButtons();

    finishDialogSettings();
}


bool DIALOG_PNS_SETTINGS::TransferDataFromWindow()
{
    // Save widgets' values to settings
    if     ( m_rbMarkObstacles->GetValue() ) m_settings.SetMode( PNS::RM_MarkObstacles );
    else if( m_rbShove->GetValue() )         m_settings.SetMode( PNS::RM_Shove );
    else if( m_rbWalkaround->GetValue() )    m_settings.SetMode( PNS::RM_Walkaround );

    m_settings.SetShoveVias( m_shoveVias->GetValue() );
    m_settings.SetJumpOverObstacles( m_backPressure->GetValue() );
    m_settings.SetRemoveLoops( m_removeLoops->GetValue() );
    m_settings.SetSuggestFinish ( m_suggestEnding->GetValue() );
    m_settings.SetSmartPads( m_smartPads->GetValue() );
    m_settings.SetSmoothDraggedSegments( m_smoothDragged->GetValue() );
    m_settings.SetOptimizeEntireDraggedTrack( m_optimizeEntireDraggedTrack->GetValue() );
    m_settings.SetAutoPosture( m_autoPosture->GetValue() );
    m_settings.SetFixAllSegments( m_fixAllSegments->GetValue() );
    m_settings.SetAllowDRCViolations( m_violateDrc->GetValue() );
    m_settings.SetRestrictAngles( m_restrictAngles->GetValue() );
    m_settings.SetFreeAngleMode( m_freeAngleMode->GetValue() );

    m_settings.SetFlexibleRouting( m_flexibleRouting->GetValue() );
    m_settings.SetCornerRadius( pcbIUScale.mmToIU( m_cornerRadiusMm->GetValue() ) );
    m_settings.SetCornerRadiusPercent( m_cornerRadiusPercent->GetValue() );
    m_settings.SetCornerRadiusUsesPercent( m_cornerRadiusPercentMode->GetValue() );

    const bool roundedCorners = m_roundCorners->GetValue();
    const bool use90DegreeCorners = m_settings.GetCornerMode() == DIRECTION_45::CORNER_MODE::MITERED_90
                                     || m_settings.GetCornerMode() == DIRECTION_45::CORNER_MODE::ROUNDED_90;

    if( roundedCorners )
        m_settings.SetCornerMode( use90DegreeCorners ? DIRECTION_45::CORNER_MODE::ROUNDED_90
                                                      : DIRECTION_45::CORNER_MODE::ROUNDED_45 );
    else
        m_settings.SetCornerMode( use90DegreeCorners ? DIRECTION_45::CORNER_MODE::MITERED_90
                                                      : DIRECTION_45::CORNER_MODE::MITERED_45 );

    if( m_boardSettings )
    {
        m_boardSettings->SetSmoothConnections( m_smoothConnections->GetValue() );
        m_boardSettings->SetSmoothConnectionRadius( pcbIUScale.mmToIU( m_smoothRadiusMm->GetValue() ) );
        m_boardSettings->SetSmoothConnectionRadiusPercent( m_smoothRadiusPercent->GetValue() );
        m_boardSettings->SetSmoothConnectionRadiusUsesPercent( m_smoothRadiusPercentMode->GetValue() );
    }

    return true;
}


void DIALOG_PNS_SETTINGS::onModeChange( wxCommandEvent& aEvent )
{
    m_freeAngleMode->Enable( m_rbMarkObstacles->GetValue() );
    m_violateDrc->Enable( m_rbMarkObstacles->GetValue() );

    m_shoveVias->Enable( m_rbShove->GetValue() );
    m_backPressure->Enable( m_rbShove->GetValue() );
}
