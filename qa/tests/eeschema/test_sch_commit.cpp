/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
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

#include <boost/test/unit_test.hpp>
#include <tool/tool_manager.h>
#include <sch_commit.h>
#include <sch_group.h>
#include <sch_text.h>
#include <sch_line.h>
#include <sch_screen.h>
#include <sch_symbol.h>
#include <sch_pin.h>
#include <lib_symbol.h>

BOOST_AUTO_TEST_SUITE( SchCommit )

BOOST_AUTO_TEST_CASE( CacheValidationIgnoresOnlyChildEnumeration )
{
    TOOL_MANAGER manager;
    // Library-cache undo owns a reference and deletes an unreferenced screen.
    // Retain the fixture's own reference until the commit has been destroyed.
    auto releaseScreen = []( SCH_SCREEN* aScreen )
    {
        aScreen->DecRefCount();
        if( aScreen->GetRefCount() == 0 )
            delete aScreen;
    };
    std::unique_ptr<SCH_SCREEN, decltype( releaseScreen )> owner( new SCH_SCREEN, releaseScreen );
    owner->IncRefCount();
    SCH_SCREEN& screen = *owner;
    LIB_SYMBOL library( wxS( "StyleTie" ) );
    library.SetLibId( LIB_ID( wxS( "Automation" ), wxS( "StyleTie" ) ) );
    library.SetBodyStyleNames( { "Primary", "Alternate" } );
    for( int style : { 1, 2 } )
    {
        auto* pin = new SCH_PIN( &library );
        pin->SetNumber( wxS( "1" ) );
        pin->SetUnit( 1 ); pin->SetBodyStyle( style );
        library.AddDrawItem( pin );
    }
    const wxString key = wxS( "Automation:StyleTie" );
    auto* symbol = new SCH_SYMBOL;
    symbol->SetLibId( library.GetLibId() );
    symbol->SetLibSymbol( new LIB_SYMBOL( library ) );
    symbol->SetSchSymbolLibraryName( key );
    screen.Append( symbol, false );
    screen.AddLibSymbol( key, std::make_unique<LIB_SYMBOL>( library ) );
    auto& placed = *symbol->GetLibSymbolRef();
    auto& storedPins = placed.GetDrawItems()[SCH_PIN_T].base();
    std::swap( storedPins[0], storedPins[1] );
    SCH_COMMIT commit( &manager );
    commit.CaptureLibraryCache( screen );
    wxString failure;
    BOOST_CHECK_MESSAGE( commit.ValidateLibraryCaches( failure ), failure );

    const wxString original = placed.GetValueField().GetText();
    placed.GetValueField().SetText( wxS( "different" ) );
    BOOST_CHECK( !commit.ValidateLibraryCaches( failure ) );
    placed.GetValueField().SetText( original );
    auto& pin = placed.GetDrawItems()[SCH_PIN_T].front();
    const KIID id = pin.m_Uuid;
    const_cast<KIID&>( pin.m_Uuid ) = KIID();
    BOOST_CHECK( !commit.ValidateLibraryCaches( failure ) );
    const_cast<KIID&>( pin.m_Uuid ) = id;
    BOOST_CHECK( commit.ValidateLibraryCaches( failure ) );
    placed.AddDrawItem( new SCH_PIN( static_cast<SCH_PIN&>( pin ) ) );
    BOOST_CHECK( !commit.ValidateLibraryCaches( failure ) );
}

BOOST_AUTO_TEST_CASE( AppliedAutomationWireRemovalRetainsTheOriginalImage )
{
    TOOL_MANAGER manager;
    SCH_SCREEN screen;
    auto line = std::make_unique<SCH_LINE>( VECTOR2I( 0, 0 ), LAYER_WIRE );
    line->SetEndPoint( VECTOR2I( 1000, 0 ) );
    screen.Append( line.get() );
    SCH_COMMIT commit( &manager );
    commit.SetAutomationOrigin( "fixture", "applied-wire-removal" );
    commit.Modify( line.get(), &screen );
    line->SetEndPoint( VECTOR2I( 2000, 0 ) );
    line->SetFlags( IS_MOVING | STRUCT_DELETED );
    screen.Remove( line.get() );
    commit.Removed( line.get(), &screen );
    BOOST_CHECK( line->GetEndPoint() == VECTOR2I( 1000, 0 ) );
    BOOST_CHECK( !( line->GetFlags() & ( IS_MOVING | IS_NEW | IN_EDIT | STRUCT_DELETED ) ) );
    BOOST_CHECK_EQUAL( commit.GetStatus( line.get(), &screen ), CHT_REMOVE | CHT_DONE );
}

BOOST_AUTO_TEST_CASE( RecursesThroughGroups )
{
    TOOL_MANAGER mgr;
    SCH_COMMIT commit( &mgr );

    SCH_TEXT t1;
    SCH_TEXT t2;
    SCH_GROUP group;
    group.AddItem( &t1 );
    group.AddItem( &t2 );

    commit.Stage( &group, CHT_MODIFY, nullptr, RECURSE_MODE::RECURSE );

    BOOST_CHECK_EQUAL( commit.GetStatus( &t1 ), CHT_MODIFY );
    BOOST_CHECK_EQUAL( commit.GetStatus( &t2 ), CHT_MODIFY );
}

BOOST_AUTO_TEST_CASE( ClearsSelectedByDragFlag )
{
    TOOL_MANAGER mgr;
    SCH_COMMIT commit( &mgr );

    SCH_TEXT text;
    text.SetFlags( SELECTED_BY_DRAG );
    text.SetSelected();

    commit.Stage( &text, CHT_MODIFY );

    BOOST_CHECK( text.IsSelected() );
    BOOST_CHECK_EQUAL( commit.GetStatus( &text ), CHT_MODIFY );
}

BOOST_AUTO_TEST_SUITE_END()
