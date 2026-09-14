/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2023 Alex Shvartzkop <dudesuchamazing@gmail.com>
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
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#include "generator_tool_pns_proxy.h"

#include <board_commit.h>
#include <pad.h>
#include <pcb_shape.h>
#include <tools/pcb_grid_helper.h>

#include <router/pns_kicad_iface.h>
#include <router/pns_solid.h>
#include <router/pns_router.h>
#include <router/pns_routing_settings.h>
#include <stdexcept>


class PNS_KICAD_IFACE_GENERATOR : public PNS_KICAD_IFACE
{
public:
    explicit PNS_KICAD_IFACE_GENERATOR( bool aDetached = false ) : m_detached( aDetached ) {}

    void EraseView() override
    { if( !m_detached ) PNS_KICAD_IFACE::EraseView(); }
    void HideItem( PNS::ITEM* aItem ) override
    { if( !m_detached ) PNS_KICAD_IFACE::HideItem( aItem ); }
    void DisplayItem( const PNS::ITEM* aItem, int aClearance, bool aEdit = false, int aFlags = 0 ) override
    { if( !m_detached ) PNS_KICAD_IFACE::DisplayItem( aItem, aClearance, aEdit, aFlags ); }
    void DisplayPathLine( const SHAPE_LINE_CHAIN& aLine, int aImportance ) override
    { if( !m_detached ) PNS_KICAD_IFACE::DisplayPathLine( aLine, aImportance ); }
    void DisplayRatline( const SHAPE_LINE_CHAIN& aLine, PNS::NET_HANDLE aNet ) override
    { if( !m_detached ) PNS_KICAD_IFACE::DisplayRatline( aLine, aNet ); }
    bool IsAnyLayerVisible( const PNS_LAYER_RANGE& aLayers ) const override
    { return m_detached || PNS_KICAD_IFACE::IsAnyLayerVisible( aLayers ); }
    bool IsItemVisible( const PNS::ITEM* aItem ) const override
    { return m_detached || PNS_KICAD_IFACE::IsItemVisible( aItem ); }
    EDA_UNITS GetUnits() const override
    { return m_detached ? EDA_UNITS::MM : PNS_KICAD_IFACE::GetUnits(); }
    void SetHostTool( PCB_TOOL_BASE* aTool ) override
    {
        m_tool = aTool;
        m_commit = nullptr;

        ClearCommits();
    }

    void Commit() override
    { //
        m_changes.emplace_back();
    }

    void ClearCommits()
    {
        m_updatedItems.clear();
        m_fpOffsets.clear();
        m_changes.clear();
        m_changes.emplace_back();
        m_createdItems.clear();
    }

    void AddItem( PNS::ITEM* aItem ) override
    {
        if( aItem->OfKind( PNS::ITEM::SOLID_T ) )
        {
            UpdateItem( aItem );
            return;
        }

        BOARD_ITEM* brdItem = createBoardItem( aItem );

        if( brdItem )
        {
            aItem->SetParent( brdItem );
            brdItem->ClearFlags();

            m_createdItems.insert( brdItem );
            m_changes.back().addedItems.emplace( brdItem );
        }
    }

    void UpdateItem( PNS::ITEM* aItem ) override
    {
        if( !aItem || !aItem->Parent() || aItem->Parent()->GetBoard() != GetBoard() )
            throw std::invalid_argument( "Generator update requires an item from its board" );

        // A preview may have no BOARD_COMMIT at all. Keep the latest geometry owned
        // here until EditFinish supplies the same transaction as its add/remove work.
        m_updatedItems[aItem->Parent()] = std::unique_ptr<PNS::ITEM>( aItem->Clone() );
    }

    void ApplyUpdates( BOARD_COMMIT& aCommit )
    {
        if( aCommit.GetBoard() != GetBoard() )
            throw std::invalid_argument( "Generator transaction belongs to another board" );

        for( const auto& [parent, item] : m_updatedItems )
        {
            int status = aCommit.GetStatus( parent ) & CHT_TYPE;

            // Ignore deleted originals and transient generated baselines which were
            // never staged for addition. Modifying either would resurrect lost work.
            if( status == CHT_REMOVE || ( IsGeneratedItem( parent ) && status != CHT_ADD ) )
                continue;

            modifyBoardItem( item.get(), aCommit );
        }

        applyFootprintOffsets( aCommit );
        m_updatedItems.clear();
    }

    void RemoveItem( PNS::ITEM* aItem ) override
    {
        BOARD_ITEM* parent = aItem->Parent();

        if( aItem->OfKind( PNS::ITEM::SOLID_T ) )
        {
            // Routing can move a pad's footprint, never remove it. Its matching
            // AddItem/UpdateItem carries the final position without touching it here.
            return;
        }

        if( parent )
        {
            m_changes.back().removedItems.emplace( parent );
        }
    }

    std::vector<GENERATOR_PNS_CHANGES>& Changes() { return m_changes; };

    bool IsGeneratedItem( BOARD_ITEM* aItem ) const { return m_createdItems.contains( aItem ); }

private:
    bool m_detached;
    std::set<BOARD_ITEM*> m_createdItems;
    std::map<BOARD_ITEM*, std::unique_ptr<PNS::ITEM>> m_updatedItems;

    std::vector<GENERATOR_PNS_CHANGES> m_changes;
};


void GENERATOR_TOOL_PNS_PROXY::ClearRouterChanges()
{
    static_cast<PNS_KICAD_IFACE_GENERATOR*>( GetInterface() )->ClearCommits();
}


const std::vector<GENERATOR_PNS_CHANGES>& GENERATOR_TOOL_PNS_PROXY::GetRouterChanges()
{
    return static_cast<PNS_KICAD_IFACE_GENERATOR*>( GetInterface() )->Changes();
}


void GENERATOR_TOOL_PNS_PROXY::ApplyRouterUpdates( BOARD_COMMIT& aCommit )
{
    static_cast<PNS_KICAD_IFACE_GENERATOR*>( GetInterface() )->ApplyUpdates( aCommit );
}


bool GENERATOR_TOOL_PNS_PROXY::ItemCreatedBySession( BOARD_ITEM* aItem ) const
{
    return static_cast<PNS_KICAD_IFACE_GENERATOR*>( GetInterface() )->IsGeneratedItem( aItem );
}


GENERATOR_TOOL_PNS_PROXY::GENERATOR_TOOL_PNS_PROXY( const std::string& aToolName ) :
        PNS::TOOL_BASE( aToolName )
{
}


GENERATOR_TOOL_PNS_PROXY::~GENERATOR_TOOL_PNS_PROXY()
{
    // ROUTER borrows its settings. Destroy it before the owned snapshot settings.
    Reset( RESET_REASON::SHUTDOWN );
}

void GENERATOR_TOOL_PNS_PROXY::InitializeSnapshot( std::unique_ptr<PNS::ROUTING_SETTINGS> aSettings )
{
    if( !aSettings || !m_toolMgr || !board() )
        throw std::invalid_argument( "Detached generator requires a board and routing settings" );
    Reset( RESET_REASON::SHUTDOWN );
    m_snapshotSettings = std::move( aSettings );
    Reset( RESET_REASON::MODEL_RELOAD );
}


void GENERATOR_TOOL_PNS_PROXY::Reset( RESET_REASON aReason )
{
    delete m_gridHelper;
    delete m_router;
    delete m_iface; // Delete after m_router because PNS::NODE dtor needs m_ruleResolver
    m_gridHelper = nullptr;
    m_router = nullptr;
    m_iface = nullptr;

    if( aReason == RESET_REASON::SHUTDOWN )
    {
        return;
    }

    if( !m_snapshotSettings && !frame() )
        throw std::logic_error( "A generator without an editor requires detached routing settings" );

    m_iface = new PNS_KICAD_IFACE_GENERATOR( m_snapshotSettings != nullptr );
    m_iface->SetBoard( board() );
    if( !m_snapshotSettings ) m_iface->SetView( getView() );
    m_iface->SetHostTool( this );

    m_router = new PNS::ROUTER;
    m_router->SetInterface( m_iface );
    m_router->ClearWorld();
    m_router->SyncWorld();

    m_router->UpdateSizes( m_savedSizes );

    if( m_snapshotSettings )
    {
        m_router->LoadSettings( m_snapshotSettings.get() );
        m_gridHelper = nullptr; // No pointer/keyboard grid interaction in this context.
        return;
    }

    PCBNEW_SETTINGS* settings = frame()->GetPcbNewSettings();

    if( !settings->m_PnsSettings )
        settings->m_PnsSettings = std::make_unique<PNS::ROUTING_SETTINGS>( settings, "tools.pns" );

    m_router->LoadSettings( settings->m_PnsSettings.get() );

    m_gridHelper = new PCB_GRID_HELPER( m_toolMgr, frame()->GetMagneticItemsSettings() );
}
