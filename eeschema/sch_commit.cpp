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
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#include <macros.h>
#include <tool/tool_manager.h>
#include <tools/sch_tool_base.h>

#include <lib_symbol.h>

#include <sch_group.h>
#include <sch_screen.h>
#include <schematic.h>

#include <view/view.h>
#include <sch_commit.h>
#include <sch_library_cache_undo.h>
#include <api/api_sch_symbol_definition.h>
#include <google/protobuf/util/message_differencer.h>
#include <sch_embedded_files_undo.h>
#include <sch_page_settings_undo.h>
#include <drawing_sheet/ds_data_model.h>
#include <connection_graph.h>

#include <functional>
#include <wx/log.h>


SCH_COMMIT::SCH_COMMIT( TOOL_MANAGER* aToolMgr ) :
        COMMIT(),
        m_toolMgr( aToolMgr ),
        m_isLibEditor( false )
{
    SCH_BASE_FRAME* frame = static_cast<SCH_BASE_FRAME*>( m_toolMgr->GetToolHolder() );
    m_isLibEditor = frame && frame->IsType( FRAME_SCH_SYMBOL_EDITOR );
}


SCH_COMMIT::SCH_COMMIT( SCH_TOOL_BASE<SCH_BASE_FRAME>* aTool )
{
    m_toolMgr = aTool->GetManager();
    m_isLibEditor = aTool->IsSymbolEditor();
}


SCH_COMMIT::SCH_COMMIT( EDA_DRAW_FRAME* aFrame )
{
    m_toolMgr = aFrame->GetToolManager();
    m_isLibEditor = aFrame->IsType( FRAME_SCH_SYMBOL_EDITOR );
}


SCH_COMMIT::~SCH_COMMIT()
{
}


bool SCH_COMMIT::Empty() const
{
    return COMMIT::Empty() && !m_embeddedFilesUndo && !m_pageSettingsUndo && !m_libraryCacheChanged;
}


void SCH_COMMIT::CaptureLibraryCache( SCH_SCREEN& aScreen )
{
    if( !m_libraryCacheUndo.count( &aScreen ) )
    {
        m_libraryCacheUndo.emplace( &aScreen, std::make_unique<SCH_LIBRARY_CACHE_UNDO_ITEM>( aScreen ) );
        m_libraryCacheScopes.emplace( &aScreen, std::make_unique<SCH_SYMBOL_CACHE_EDIT_SCOPE>( aScreen ) );
    }
}


void SCH_COMMIT::ReplaceLibraryCache( SCH_SCREEN& aScreen, SCH_SYMBOL_CACHE_STATE& aCandidate )
{
    CaptureLibraryCache( aScreen );
    aScreen.SwapLibSymbolCache( aCandidate );
    aScreen.SetContentModified();
    m_libraryCacheChanged = true;
}


bool SCH_COMMIT::ValidateLibraryCaches( wxString& aFailure )
{
    for( const auto& [screen, undo] : m_libraryCacheUndo )
    {
        wxString difference;
        auto validate = [&]( SCH_SYMBOL* symbol )
        {
            difference.clear();
            const auto& definitions = screen->GetLibSymbols();
            auto found = definitions.find( symbol->GetSchSymbolLibraryName() );
            if( found == definitions.end() || !symbol->GetLibSymbolRef() )
                return false;
            // Ordinary Append sorts both definitions before native comparison.
            // Explicit cache transactions suppress Append's cache maintenance,
            // so compare private copies in that same native order instead.
            LIB_SYMBOL cachedDefinition( *found->second );
            LIB_SYMBOL placedDefinition( *symbol->GetLibSymbolRef() );
            cachedDefinition.GetDrawItems().sort();
            placedDefinition.GetDrawItems().sort();
            kiapi::schematic::types::SchematicCachedSymbol cached, placed;
            if( !PackCachedSymbol( cached, found->first, cachedDefinition )
                    || !PackCachedSymbol( placed, found->first, placedDefinition ) )
                return false;

            google::protobuf::util::MessageDifferencer comparer;
            std::string details;
            comparer.ReportDifferencesToString( &details );
            bool equal = comparer.Compare( cached, placed );
            if( !equal )
                difference = wxString::FromUTF8( details ).Left( 1024 );
            return equal;
        };
        for( SCH_ITEM* item : screen->Items() )
        {
            if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( item ); symbol
                    && ( GetStatus( symbol, screen ) & CHT_TYPE ) != CHT_REMOVE && !validate( symbol ) )
            {
                aFailure = wxT( "Cache definition disagrees with a remaining placed symbol: " )
                           + symbol->m_Uuid.AsString();
                if( !difference.IsEmpty() )
                    aFailure += wxT( "\n" ) + difference;
                return false;
            }
        }
        for( const COMMIT_LINE& entry : m_entries )
        {
            if( entry.m_screen != screen || ( entry.m_type & CHT_TYPE ) != CHT_ADD )
                continue;
            if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( entry.m_item ); symbol && !validate( symbol ) )
            {
                aFailure = wxT( "Cache definition disagrees with a newly placed symbol: " )
                           + symbol->m_Uuid.AsString();
                if( !difference.IsEmpty() )
                    aFailure += wxT( "\n" ) + difference;
                return false;
            }
        }
    }
    return true;
}


void SCH_COMMIT::ReplaceEmbeddedFiles( EMBEDDED_FILES& aCandidate )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Embedded-file changes require a schematic editor" );
    EMBEDDED_FILES* files = frame->Schematic().GetEmbeddedFiles();
    if( !m_embeddedFilesUndo )
        m_embeddedFilesUndo = std::make_unique<SCH_EMBEDDED_FILES_UNDO_ITEM>( *files );
    files->SwapData( aCandidate );
}


void SCH_COMMIT::SetPageSettings( SCH_SCREEN* aScreen, const PAGE_INFO& aPage,
                                  const wxString& aDrawingSheet, const wxString& aPreparedLayout )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && aScreen && !m_isLibEditor, "Page changes require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    DS_DATA_MODEL::GetTheInstance().SetPageLayout( aPreparedLayout.ToUTF8() );
    aScreen->SetPageSettings( aPage );
    BASE_SCREEN::m_DrawingSheetFileName = aDrawingSheet;
    frame->Schematic().Settings().m_SchDrawingSheetFileName = aDrawingSheet;
    aScreen->SetContentModified();
}


void SCH_COMMIT::SetTitleBlock( SCH_SCREEN* aScreen, const TITLE_BLOCK& aTitle )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && aScreen && !m_isLibEditor, "Title changes require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    aScreen->SetTitleBlock( aTitle );
    aScreen->SetContentModified();
}


void SCH_COMMIT::SetBusAliases( const std::vector<std::shared_ptr<BUS_ALIAS>>& aAliases )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Bus aliases require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeBusAliases();
    frame->Schematic().SetBusAliases( aAliases );
    m_connectivitySettingsChanged = true;
}

void SCH_COMMIT::SetTextVariables( const std::map<wxString, wxString>& aVariables )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Text variables require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeTextVariables();
    SCH_PAGE_SETTINGS_UNDO_ITEM::ApplyTextVariables( frame, aVariables );
    m_connectivitySettingsChanged = true;
}

void SCH_COMMIT::SetSetupSettings( const nlohmann::json& aBefore, const nlohmann::json& aAfter )
{
    if( aBefore == aAfter ) return;
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    if( !frame || m_isLibEditor )
        throw std::runtime_error( "Setup changes require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->ApplySetupDelta( frame, aBefore, aAfter );
    m_connectivitySettingsChanged = true;
}


void SCH_COMMIT::StageVariantRegistry()
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Variant registry changes require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeVariantRegistry();
}

void SCH_COMMIT::SetVariantRegistry( const std::map<wxString, wxString>& aDescriptions )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Variant registry changes require a schematic editor" );
    if( frame->Schematic().Settings().m_VariantDescriptions == aDescriptions )
        return;
    StageVariantRegistry();
    const wxString current = frame->Schematic().GetCurrentVariant();
    SCH_PAGE_SETTINGS_UNDO_ITEM::ApplyVariantDescriptions( frame, aDescriptions );
    frame->Schematic().LoadVariants();
    frame->UpdateVariantSelectionCtrl( frame->Schematic().GetVariantNamesForUI() );
    frame->SetCurrentVariant( current );
}

void SCH_COMMIT::SetDrawingRatios( const std::array<double, 5>& aRatios )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Drawing ratios require a schematic editor" );
    if( frame->Schematic().Settings().DrawingRatios() == aRatios ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeDrawingRatios();
    SCH_PAGE_SETTINGS_UNDO_ITEM::ApplyDrawingRatios( frame, aRatios );
}

void SCH_COMMIT::SetAnnotation( const kiapi::schematic::types::SchematicAnnotationSettings& aAnnotation )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Annotation policy requires a schematic editor" );
    if( SCH_ANNOTATION::Capture( frame->Schematic().Settings() ).SerializeAsString()
            == aAnnotation.SerializeAsString() ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeAnnotation();
    SCH_ANNOTATION::Restore( frame->Schematic().Settings(), aAnnotation );
}

void SCH_COMMIT::SetFormatting( const kiapi::schematic::types::SchematicFormattingSettings& aFormatting )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Formatting requires a schematic editor" );
    if( SCH_FORMATTING::Capture( frame->Schematic().Settings() ).SerializeAsString()
            == aFormatting.SerializeAsString() ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeFormatting();
    const bool updateFields = frame->Schematic().Settings().m_IntersheetRefsShow
                             != aFormatting.show_intersheet_references();
    if( updateFields )
    {
        // Native reference visibility may also autoplace a label field. Keep
        // those object edits in the same commit as the project setting.
        auto* screen = frame->GetScreen();
        for( SCH_ITEM* item : screen->Items().OfType( SCH_GLOBAL_LABEL_T ) )
            Modify( item, screen );
    }
    SCH_PAGE_SETTINGS_UNDO_ITEM::ApplyFormatting( frame, aFormatting );
    if( updateFields ) frame->RecomputeIntersheetRefs();
}

void SCH_COMMIT::SetVariantDescription( const wxString& aName, const wxString& aDescription )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor && frame->Schematic().GetVariantNames().contains( aName ),
                 "Variant descriptions require an existing schematic variant" );
    if( frame->Schematic().GetVariantDescription( aName ) == aDescription )
        return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeVariantDescriptions();
    auto descriptions = frame->Schematic().Settings().m_VariantDescriptions;
    descriptions[aName] = aDescription;
    SCH_PAGE_SETTINGS_UNDO_ITEM::ApplyVariantDescriptions( frame, descriptions );
}


bool SCH_COMMIT::SetErcSettings( SCH_ERC_SETTINGS::PREPARED& aPrepared, std::string& aFailure )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    if( !frame || m_isLibEditor )
    {
        aFailure = "ERC replacement requires a schematic editor";
        return false;
    }
    auto& schematic = frame->Schematic();
    if( SCH_ERC_SETTINGS::Capture( schematic ).SerializeAsString() == aPrepared.canonical.SerializeAsString() )
        return true;

    std::map<std::string, SCH_ERC_SETTINGS::EXCLUSION*> desired;
    for( auto& exclusion : aPrepared.exclusions ) desired.emplace( exclusion.key, &exclusion );
    std::vector<std::pair<SCH_SCREEN*, SCH_MARKER*>> markers;
    std::set<SCH_SCREEN*> seen;
    for( const SCH_SHEET_PATH& path : schematic.Hierarchy() )
    {
        SCH_SCREEN* screen = path.LastScreen();
        if( !screen || !seen.insert( screen ).second ) continue;
        for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
        {
            auto* marker = static_cast<SCH_MARKER*>( item );
            const auto found = desired.find( ERC_EXCLUSION::FromMarker( *marker ).GetSortKey() );
            const bool excluded = found != desired.end();
            const wxString comment = excluded ? wxString::FromUTF8( found->second->comment ) : wxString();
            if( marker->IsLocked() && ( marker->IsExcluded() != excluded || marker->GetComment() != comment ) )
            {
                aFailure = "A locked ERC marker would be changed";
                return false;
            }
            markers.emplace_back( screen, marker );
        }
    }
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeErcPolicy();
    m_ercAddedMarkers.reserve( m_ercAddedMarkers.size() + aPrepared.exclusions.size() );
    for( const auto& [screen, marker] : markers )
    {
        const std::string key = ERC_EXCLUSION::FromMarker( *marker ).GetSortKey();
        const auto found = desired.find( key );
        const bool excluded = found != desired.end();
        const wxString comment = excluded ? wxString::FromUTF8( found->second->comment ) : wxString();
        if( marker->IsExcluded() != excluded || marker->GetComment() != comment )
        {
            Modify( marker, screen );
            marker->SetExcluded( excluded, comment );
        }
        if( excluded ) found->second->marker.reset(); // existing native marker retains its UUID
    }
    for( auto& exclusion : aPrepared.exclusions )
    {
        if( !exclusion.marker ) continue;
        exclusion.marker->SetExcluded( true, wxString::FromUTF8( exclusion.comment ) );
        SCH_MARKER* marker = exclusion.marker.get();
        m_ercAddedMarkers.push_back( std::move( exclusion.marker ) );
        // Stage ownership before insertion, so rollback never loses a prepared marker.
        Added( marker, exclusion.screen );
        exclusion.screen->Append( marker );
        if( exclusion.screen == frame->GetScreen() ) frame->GetCanvas()->GetView()->Add( marker );
    }
    SCH_ERC_SETTINGS::RestorePolicy( schematic.ErcSettings(), aPrepared.canonical );
    frame->RefreshErcDialog();
    return true;
}

bool SCH_COMMIT::StageNetChainEdit( const std::set<SCH_SYMBOL*>& aSymbols )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    if( !frame || m_isLibEditor || !frame->Schematic().ConnectionGraph() )
        return false;

    std::vector<std::pair<SCH_SYMBOL*, SCH_SCREEN*>> owned;
    std::set<SCH_SCREEN*> seen;
    if( !aSymbols.empty() )
    {
        for( const SCH_SHEET_PATH& path : frame->Schematic().Hierarchy() )
        {
            SCH_SCREEN* screen = path.LastScreen();
            if( !screen || !seen.insert( screen ).second ) continue;
            for( SCH_ITEM* item : screen->Items() )
                if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( item ); symbol && aSymbols.contains( symbol ) )
                    owned.emplace_back( symbol, screen );
        }
        if( owned.size() != aSymbols.size() ) return false;
    }

    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeNetChains();
    m_connectivitySettingsChanged = true;
    for( const auto& [symbol, screen] : owned ) Modify( symbol, screen );
    return true;
}

void SCH_COMMIT::SetNetChainDefinitions( const std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION>& aDefinitions )
{
    wxCHECK_RET( StageNetChainEdit( {} ), "Net chain changes require a schematic editor" );
    auto* frame = static_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    frame->Schematic().ConnectionGraph()->SetNetChainDefinitions( aDefinitions );
}

bool SCH_COMMIT::SetNetChainClasses( const std::set<wxString>& aDefinitions,
                                   const std::map<wxString, wxString>& aAssignments )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    auto settings = frame ? frame->Prj().GetProjectFile().NetSettings() : nullptr;
    if( !settings || !StageNetChainEdit( {} ) ) return false;
    settings->SetNetChainClassDefinitions( aDefinitions );
    settings->ClearNetChainClasses();
    for( const auto& [chain, name] : aAssignments ) settings->SetNetChainClass( chain, name );
    return true;
}

void SCH_COMMIT::SetRootInstance( SCH_SHEET* aSheet, const std::optional<wxString>& aPageNumber )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && aSheet && !m_isLibEditor, "Root page changes require a schematic editor" );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    SCH_SHEET_INSTANCE record;
    if( aSheet->HasRootInstance() )
        record = aSheet->GetRootInstance();
    aSheet->RemoveInstance( KIID_PATH{} );
    if( aPageNumber )
    {
        record.m_PageNumber = *aPageNumber;
        aSheet->AddInstance( record );
    }
    if( aSheet->GetScreen() )
        aSheet->GetScreen()->SetContentModified();
}


COMMIT& SCH_COMMIT::Stage( EDA_ITEM *aItem, CHANGE_TYPE aChangeType, BASE_SCREEN *aScreen,
                           RECURSE_MODE aRecurse )
{
    wxCHECK( aItem, *this );

    // A deferred removal supersedes earlier modifications to this object.
    // Restore its pre-commit value before COMMIT::Stage discards the modify
    // entry, so both cancellation and the deletion's undo retain that value.
    // Already-applied removals and child-to-parent undo remapping have different
    // ownership semantics and must not update an absent screen item here.
    if( !m_isLibEditor && aChangeType == CHT_REMOVE && undoLevelItem( aItem ) == aItem )
    {
        COMMIT_LINE* previous = findEntry( aItem, aScreen );

        if( previous && ( previous->m_type & CHT_TYPE ) == CHT_MODIFY && previous->m_copy )
        {
            auto item = static_cast<SCH_ITEM*>( aItem );
            item->SwapItemData( static_cast<SCH_ITEM*>( previous->m_copy ) );

            if( auto screen = dynamic_cast<SCH_SCREEN*>( aScreen ) )
                screen->Update( item );

            Unmodify( aItem, aScreen );
        }
    }

    if( aRecurse == RECURSE_MODE::RECURSE )
    {
        if( SCH_GROUP* group = dynamic_cast<SCH_GROUP*>( aItem ) )
        {
            for( EDA_ITEM* member : group->GetItems() )
                Stage( member, aChangeType, aScreen, aRecurse );
        }
    }

    // IS_SELECTED flag should not be set on undo items which were added for a drag operation.
    if( aItem->IsSelected() && aItem->HasFlag( SELECTED_BY_DRAG ) )
    {
        aItem->ClearSelected();
        COMMIT::Stage( aItem, aChangeType, aScreen );
        aItem->SetSelected();
    }
    else
    {
        COMMIT::Stage( aItem, aChangeType, aScreen );
    }

    return *this;
}


COMMIT& SCH_COMMIT::Stage( std::vector<EDA_ITEM*> &container, CHANGE_TYPE aChangeType,
                           BASE_SCREEN *aScreen )
{
    for( EDA_ITEM* item : container )
        Stage( item, aChangeType, aScreen );

    return *this;
}


void SCH_COMMIT::pushLibEdit( const wxString& aMessage, int aCommitFlags )
{
    // Symbol editor just saves copies of the whole symbol, so grab the first and discard the rest
    LIB_SYMBOL* symbol = dynamic_cast<LIB_SYMBOL*>( m_entries.front().m_item );
    LIB_SYMBOL* copy = dynamic_cast<LIB_SYMBOL*>( m_entries.front().m_copy );

    if( symbol )
    {
        if( KIGFX::VIEW* view = m_toolMgr->GetView() )
        {
            view->Update( symbol );

            symbol->RunOnChildren(
                    [&]( SCH_ITEM* aChild )
                    {
                        view->Update( aChild );
                    },
                    RECURSE_MODE::NO_RECURSE );
        }

        if( SYMBOL_EDIT_FRAME* frame = static_cast<SYMBOL_EDIT_FRAME*>( m_toolMgr->GetToolHolder() ) )
        {
            if( !( aCommitFlags & SKIP_UNDO ) )
            {
                if( copy )
                {
                    frame->PushSymbolToUndoList( aMessage, copy );
                    copy = nullptr;   // we've transferred ownership to the undo stack
                }
            }
        }

        if( copy )
        {
            // if no undo entry was needed, the copy would create a memory leak
            delete copy;
            copy = nullptr;
        }
    }

    m_toolMgr->PostEvent( { TC_MESSAGE, TA_MODEL_CHANGE, AS_GLOBAL } );
    m_toolMgr->ProcessEvent( EVENTS::SelectedItemsModified );
}


void SCH_COMMIT::pushSchEdit( const wxString& aMessage, int aCommitFlags )
{
    // Objects potentially interested in changes:
    PICKED_ITEMS_LIST   undoList;
    KIGFX::VIEW*        view = m_toolMgr->GetView();

    SCH_EDIT_FRAME*     frame = static_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    SCH_SCREEN*         currentScreen = frame ? frame->GetScreen() : nullptr;
    SCH_SELECTION_TOOL* selTool = m_toolMgr->GetTool<SCH_SELECTION_TOOL>();
    SCH_GROUP*          enteredGroup = selTool ? selTool->GetEnteredGroup() : nullptr;
    bool                itemsDeselected = false;
    bool                selectedModified = false;
    bool                dirtyConnectivity = m_connectivitySettingsChanged;
    bool                refreshHierarchy = false;
    SCH_CLEANUP_FLAGS   connectivityCleanUp = m_connectivitySettingsChanged ? GLOBAL_CLEANUP : NO_CLEANUP;

    if( Empty() )
        return;

    undoList.SetDescription( aMessage );
    // Graphical/hierarchy entries undo first, then page records resolve their
    // exact sheet identities in the restored hierarchy.
    if( m_pageSettingsUndo && frame && !( aCommitFlags & SKIP_UNDO ) )
        undoList.PushItem( ITEM_PICKER( currentScreen, m_pageSettingsUndo.get(), UNDO_REDO::PAGESETTINGS ) );

    SCHEMATIC*             schematic = ( m_embeddedFilesUndo || m_pageSettingsUndo || !m_libraryCacheUndo.empty() ) && frame
                                              ? &frame->Schematic() : nullptr;
    std::vector<SCH_ITEM*> bulkAddedItems;
    std::vector<SCH_ITEM*> bulkRemovedItems;
    std::vector<SCH_ITEM*> itemsChanged;

    auto updateConnectivityFlag =
            [&]( SCH_ITEM* schItem )
            {
                if( schItem->IsConnectable() || ( schItem->Type() == SCH_RULE_AREA_T ) )
                {
                    dirtyConnectivity = true;

                    // Do a local clean up if there are any connectable objects in the commit.
                    if( connectivityCleanUp == NO_CLEANUP )
                        connectivityCleanUp = LOCAL_CLEANUP;

                    // Do a full rebuild of the connectivity if there is a sheet in the commit.
                    if( schItem->Type() == SCH_SHEET_T )
                        connectivityCleanUp = GLOBAL_CLEANUP;
                }
            };

    // We don't know that anything will be added to the entered group, but it does no harm to
    // add it to the commit anyway.
    if( enteredGroup && frame )
        Modify( enteredGroup, frame->GetScreen() );

    // Handle wires with Hop Over shapes (view update only; skipped headless):
    if( frame )
    {
        for( COMMIT_LINE& entry : m_entries )
        {
            SCH_ITEM* schCopyItem = dynamic_cast<SCH_ITEM*>( entry.m_copy );
            SCH_ITEM* schItem = dynamic_cast<SCH_ITEM*>( entry.m_item );

            if( schCopyItem && schCopyItem->Type() == SCH_LINE_T )
                frame->UpdateHopOveredWires( schCopyItem );

            if( schItem && schItem->Type() == SCH_LINE_T )
                frame->UpdateHopOveredWires( schItem );
        }
    }


    // Modify() appends to m_entries, so collect first and stage after the loop.
    std::vector<std::pair<EDA_GROUP*, BASE_SCREEN*>> removedItemGroups;

    for( COMMIT_LINE& entry : m_entries )
    {
        SCH_ITEM* schItem = dynamic_cast<SCH_ITEM*>( entry.m_item );
        int       changeType = entry.m_type & CHT_TYPE;

        wxCHECK2( schItem, continue );

        if( changeType == CHT_REMOVE && schItem->GetParentGroup() )
            removedItemGroups.emplace_back( schItem->GetParentGroup(), entry.m_screen );
    }

    for( const auto& [group, screen] : removedItemGroups )
    {
        // A parent removed by this same commit needs no membership update.
        // Modify would replace its remove entry and resurrect an empty group.
        if( ( GetStatus( group->AsEdaItem(), screen ) & CHT_TYPE ) != CHT_REMOVE )
            Modify( group->AsEdaItem(), screen );
    }

    for( COMMIT_LINE& entry : m_entries )
    {
        int         changeType = entry.m_type & CHT_TYPE;
        int         changeFlags = entry.m_type & CHT_FLAGS;
        SCH_ITEM*   schItem = dynamic_cast<SCH_ITEM*>( entry.m_item );
        SCH_SCREEN* screen = dynamic_cast<SCH_SCREEN*>( entry.m_screen );

        wxCHECK2( schItem, continue );
        wxCHECK2( screen, continue );

        if( !schematic )
            schematic = schItem->Schematic();

        if( schItem->IsSelected() )
        {
            selectedModified = true;
        }
        else
        {
            schItem->RunOnChildren(
                    [&selectedModified]( SCH_ITEM* aChild )
                    {
                        if( aChild->IsSelected() )
                            selectedModified = true;
                    },
                    RECURSE_MODE::NO_RECURSE );
        }

        switch( changeType )
        {
        case CHT_ADD:
        {
            if( enteredGroup && schItem->IsGroupableType() && !schItem->GetParentGroup() )
                selTool->GetEnteredGroup()->AddItem( schItem );

            updateConnectivityFlag( schItem );

            if( !( aCommitFlags & SKIP_UNDO ) )
                undoList.PushItem( ITEM_PICKER( screen, schItem, UNDO_REDO::NEWITEM ) );

            if( !( changeFlags & CHT_DONE ) )
            {
                if( !screen->CheckIfOnDrawList( schItem ) )  // don't want a loop!
                    screen->Append( schItem );

                if( view && screen == currentScreen )
                    view->Add( schItem );
            }

            if( frame && screen == currentScreen )
                frame->UpdateItem( schItem, true, true );
            else if( screen )
                screen->Update( schItem );

            bulkAddedItems.push_back( schItem );

            if( schItem->Type() == SCH_SHEET_T )
                refreshHierarchy = true;

            break;
        }

        case CHT_REMOVE:
        {
            updateConnectivityFlag( schItem );

            if( !( aCommitFlags & SKIP_UNDO ) )
            {
                ITEM_PICKER itemWrapper( screen, schItem, UNDO_REDO::DELETED );
                itemWrapper.SetLink( entry.m_copy );
                entry.m_copy = nullptr;   // We've transferred ownership to the undo list
                undoList.PushItem( itemWrapper );
            }

            if( schItem->IsSelected() )
            {
                if( selTool )
                    selTool->RemoveItemFromSel( schItem, true /* quiet mode */ );

                itemsDeselected = true;
            }

            if( schItem->Type() == SCH_FIELD_T )
            {
                static_cast<SCH_FIELD*>( schItem )->SetVisible( false );
                break;
            }

            if( EDA_GROUP* group = schItem->GetParentGroup() )
                group->RemoveItem( schItem );

            if( schItem->Type() == SCH_GROUP_T )
            {
                auto* group = static_cast<SCH_GROUP*>( schItem );
                // A later operation may already have reparented a surviving
                // member. Preserve that new owner while retiring this group.
                for( EDA_ITEM* member : group->GetItems() )
                    if( member->GetParentGroup() == group ) member->SetParentGroup( nullptr );
                group->GetItems().clear();
            }

            if( !( changeFlags & CHT_DONE ) )
            {
                screen->Remove( schItem );

                if( view && screen == currentScreen )
                    view->Remove( schItem );
            }

            if( frame && screen == currentScreen )
                frame->UpdateItem( schItem, true, true );
            else if( screen )
                screen->Update( schItem );

            if( schItem->Type() == SCH_SHEET_T )
                refreshHierarchy = true;

            bulkRemovedItems.push_back( schItem );
            break;
        }

        case CHT_MODIFY:
        {
            const SCH_ITEM* itemCopy = static_cast<const SCH_ITEM*>( entry.m_copy );
            SCH_SHEET_PATH  currentSheet;

            if( frame )
                currentSheet = frame->GetCurrentSheet();

            if( schItem->IsConnectivityDirty()
                || itemCopy->HasConnectivityChanges( schItem, &currentSheet )
                || ( itemCopy->Type() == SCH_RULE_AREA_T ) )
            {
                updateConnectivityFlag( schItem );
            }

            if( schItem->Type() == SCH_SYMBOL_T )
            {
                const SCH_SYMBOL* origSymbol = static_cast<const SCH_SYMBOL*>( itemCopy );
                const SCH_SYMBOL* modSymbol = static_cast<const SCH_SYMBOL*>( schItem );

                if( origSymbol->GetPins().size() != modSymbol->GetPins().size() )
                    connectivityCleanUp = GLOBAL_CLEANUP;
            }

            if( !( aCommitFlags & SKIP_UNDO ) )
            {
#if 0
                // While this keeps us from marking documents modified when someone OK's a dialog with
                // no changes, it depends on our various SCH_ITEM::operator=='s being bullet-proof. They
                // currently aren't.
                if( *itemCopy == *schItem )
                {
                    // No actual changes made; short-circuit undo
                    delete entry.m_copy;
                    entry.m_copy = nullptr;
                    break;
                }
#endif

                ITEM_PICKER itemWrapper( screen, schItem, UNDO_REDO::CHANGED );
                itemWrapper.SetLink( entry.m_copy );
                entry.m_copy = nullptr;   // We've transferred ownership to the undo list
                undoList.PushItem( itemWrapper );
            }

            if( schItem->Type() == SCH_SHEET_T )
            {
                const SCH_SHEET* modifiedSheet = static_cast<const SCH_SHEET*>( schItem );
                const SCH_SHEET* originalSheet = static_cast<const SCH_SHEET*>( itemCopy );
                wxCHECK2( modifiedSheet && originalSheet, continue );

                if( originalSheet->HasPageNumberChanges( *modifiedSheet ) )
                    refreshHierarchy = true;
            }

            if( frame && screen == currentScreen )
                frame->UpdateItem( schItem, false, true );
            else if( screen )
                screen->Update( schItem );

            itemsChanged.push_back( schItem );
            break;
        }

        default:
            wxASSERT( false );
            break;
        }

        // Delete any copies we still have ownership of
        delete entry.m_copy;
        entry.m_copy = nullptr;

        // Clear all flags but SELECTED and others used to move and rotate commands,
        // after edition (selected items must keep their selection flag).
        const int selected_mask = ( SELECTED | STARTPOINT | ENDPOINT );
        schItem->ClearFlags( EDA_ITEM_ALL_FLAGS - selected_mask );

        if( schItem->Type() == SCH_SHEET_T || schItem->Type() == SCH_SYMBOL_T )
        {
            schItem->RunOnChildren(
                    [&]( SCH_ITEM* child )
                    {
                        child->ClearFlags( EDA_ITEM_ALL_FLAGS - selected_mask );
                    },
                    RECURSE_MODE::NO_RECURSE );
        }
    }

    if( schematic )
    {
        if( bulkAddedItems.size() > 0 )
            schematic->OnItemsAdded( bulkAddedItems );

        if( bulkRemovedItems.size() > 0 )
            schematic->OnItemsRemoved( bulkRemovedItems );

        if( itemsChanged.size() > 0 )
            schematic->OnItemsChanged( itemsChanged );

        if( refreshHierarchy )
        {
            schematic->RefreshHierarchy();

            if( frame )
                frame->UpdateHierarchyNavigator();
        }
    }

    if( m_embeddedFilesUndo && frame && !( aCommitFlags & SKIP_UNDO ) )
    {
        undoList.PushItem( ITEM_PICKER( currentScreen, m_embeddedFilesUndo.get(), UNDO_REDO::EMBEDDED_FILES ) );
    }
    if( frame && !( aCommitFlags & SKIP_UNDO ) )
    {
        for( const auto& [screen, undo] : m_libraryCacheUndo )
            undoList.PushItem( ITEM_PICKER( screen, undo.get(), UNDO_REDO::LIBRARY_CACHE ) );
    }
    if( m_pageSettingsUndo && frame )
    {
        schematic->RefreshHierarchy();
        frame->UpdateHierarchyNavigator();
        // Page/title/layout changes are outside the item update list. Invalidate
        // their cached drawing content just as the standalone settings command
        // does, before any subsequent observation requests a render.
        frame->GetCanvas()->GetView()->MarkDirty();
        frame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
    }

    if( !( aCommitFlags & SKIP_UNDO ) && frame && undoList.GetCount() > 0 )
    {
        frame->SaveCopyInUndoList( undoList, UNDO_REDO::UNSPECIFIED, false );
        // Retain rollback ownership until the native undo entry was saved.
        m_embeddedFilesUndo.release();
        m_pageSettingsUndo.release();
        for( auto& [screen, undo] : m_libraryCacheUndo )
            undo.release();
    }

    if( dirtyConnectivity )
    {
        wxLogTrace( wxS( "CONN_PROFILE" ),
                    wxS( "SCH_COMMIT::pushSchEdit() %s clean up connectivity rebuild." ),
                    connectivityCleanUp == LOCAL_CLEANUP ? wxS( "local" ) : wxS( "global" ) );

        if( frame )
            frame->RecalculateConnections( this, connectivityCleanUp );
        else if( schematic )
            schematic->RecalculateConnections( this, connectivityCleanUp, m_toolMgr );
    }

    m_toolMgr->PostEvent( { TC_MESSAGE, TA_MODEL_CHANGE, AS_GLOBAL } );

    if( itemsDeselected )
        m_toolMgr->PostEvent( EVENTS::UnselectedEvent );

    if( selectedModified )
        m_toolMgr->ProcessEvent( EVENTS::SelectedItemsModified );

    if( schematic )
        schematic->RecordCommittedChange( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT,
                                           aMessage.ToStdString(), m_originId, m_operationId );
}


void SCH_COMMIT::Push( const wxString& aMessage, int aCommitFlags )
{
    if( Empty() )
    {
        m_libraryCacheScopes.clear();
        m_libraryCacheUndo.clear();
        return;
    }

    if( m_isLibEditor )
        pushLibEdit( aMessage, aCommitFlags );
    else
        pushSchEdit( aMessage, aCommitFlags );

    if( SCH_BASE_FRAME* frame = static_cast<SCH_BASE_FRAME*>( m_toolMgr->GetToolHolder() ) )
    {
        if( !( aCommitFlags & SKIP_SET_DIRTY ) )
            frame->OnModify();

        if( frame && frame->GetCanvas() )
            frame->GetCanvas()->Refresh();
    }

    m_embeddedFilesUndo.reset();
    m_pageSettingsUndo.reset();
    for( auto& marker : m_ercAddedMarkers ) marker.release();
    m_ercAddedMarkers.clear();
    m_libraryCacheScopes.clear();
    m_libraryCacheUndo.clear();
    m_libraryCacheChanged = false;
    m_connectivitySettingsChanged = false;
    m_originId.clear();
    m_operationId.clear();
    clear();
}


EDA_ITEM* SCH_COMMIT::undoLevelItem( EDA_ITEM* aItem ) const
{
    EDA_ITEM* parent = aItem->GetParent();

    if( m_isLibEditor )
        return static_cast<SYMBOL_EDIT_FRAME*>( m_toolMgr->GetToolHolder() )->GetCurSymbol();

    if( parent && parent->IsType( { SCH_SYMBOL_T, SCH_TABLE_T, SCH_SHEET_T, SCH_LABEL_LOCATE_ANY_T } ) )
        return parent;

    return aItem;
}


EDA_ITEM* SCH_COMMIT::makeImage( EDA_ITEM* aItem ) const
{
    if( m_isLibEditor )
    {
        SYMBOL_EDIT_FRAME* frame = static_cast<SYMBOL_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
        LIB_SYMBOL*        symbol = frame->GetCurSymbol();
        std::vector<KIID>  selected;

        // Cloning will clear the selected flags, but we want to keep them.
        for( const SCH_ITEM& item : symbol->GetDrawItems() )
        {
            if( item.IsSelected() )
                selected.push_back( item.m_Uuid );
        }

        symbol = new LIB_SYMBOL( *symbol );

        // Restore selected flags.
        for( SCH_ITEM& item : symbol->GetDrawItems() )
        {
            if( alg::contains( selected, item.m_Uuid ) )
                item.SetSelected();
        }

        return symbol;
    }

    return aItem->Clone();
}


void SCH_COMMIT::revertLibEdit()
{
    if( Empty() )
        return;

    // Symbol editor just saves copies of the whole symbol, so grab the first and discard the rest
    SYMBOL_EDIT_FRAME*  frame = dynamic_cast<SYMBOL_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    LIB_SYMBOL*         copy = dynamic_cast<LIB_SYMBOL*>( m_entries.front().m_copy );
    SCH_SELECTION_TOOL* selTool = m_toolMgr->GetTool<SCH_SELECTION_TOOL>();

    if( frame && copy )
    {
        frame->SetCurSymbol( copy, false );
        m_toolMgr->ResetTools( TOOL_BASE::MODEL_RELOAD );
    }

    if( selTool )
        selTool->RebuildSelection();

    clear();
}


void SCH_COMMIT::Revert()
{
    KIGFX::VIEW*        view = m_toolMgr->GetView();
    SCH_EDIT_FRAME*     frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    SCH_SELECTION_TOOL* selTool = m_toolMgr->GetTool<SCH_SELECTION_TOOL>();
    SCH_SHEET_LIST      sheets;

    if( Empty() && m_libraryCacheUndo.empty() )
        return;

    if( m_isLibEditor )
    {
        revertLibEdit();
        return;
    }

    if( m_embeddedFilesUndo && frame )
    {
        m_embeddedFilesUndo->Swap( *frame->Schematic().GetEmbeddedFiles() );
        m_embeddedFilesUndo.reset();
    }

    SCHEMATIC*             schematic = nullptr;
    std::vector<SCH_ITEM*> bulkAddedItems;
    std::vector<SCH_ITEM*> bulkRemovedItems;
    std::vector<SCH_ITEM*> itemsChanged;

    for( COMMIT_LINE& ent : m_entries )
    {
        int         changeType = ent.m_type & CHT_TYPE;
        int         changeFlags = ent.m_type & CHT_FLAGS;
        SCH_ITEM*   item = dynamic_cast<SCH_ITEM*>( ent.m_item );
        SCH_ITEM*   copy = dynamic_cast<SCH_ITEM*>( ent.m_copy );
        SCH_SCREEN* screen = dynamic_cast<SCH_SCREEN*>( ent.m_screen );

        wxCHECK2( item && screen, continue );

        KIGFX::VIEW* itemView = ( !frame || screen == frame->GetScreen() ) ? view : nullptr;

        if( !schematic )
            schematic = item->Schematic();

        switch( changeType )
        {
        case CHT_ADD:
            if( !( changeFlags & CHT_DONE ) )
                break;

            if( itemView )
                itemView->Remove( item );

            screen->Remove( item );
            bulkRemovedItems.push_back( item );
            break;

        case CHT_REMOVE:
            item->SetConnectivityDirty();

            if( !( changeFlags & CHT_DONE ) )
            {
                // API group removal releases ownership before commit so a
                // batch can reparent survivors. Cancellation restores it.
                if( item->Type() == SCH_GROUP_T )
                {
                    auto* group = static_cast<SCH_GROUP*>( item );
                    const auto members = group->GetItems();
                    for( EDA_ITEM* member : members ) group->AddItem( member );
                }
                break;
            }

            if( itemView )
                itemView->Add( item );

            screen->Append( item );
            bulkAddedItems.push_back( item );
            break;

        case CHT_MODIFY:
        {
            wxCHECK2( copy, break );

            if( itemView )
                itemView->Remove( item );

            bool unselect = !item->IsSelected();

            item->SwapItemData( copy );

            if( unselect )
            {
                item->ClearSelected();
                item->RunOnChildren( []( SCH_ITEM* aChild )
                                     {
                                         aChild->ClearSelected();
                                     },
                                     RECURSE_MODE::NO_RECURSE );
            }

            // Special cases for items which have instance data
            if( item->GetParent() && item->GetParent()->Type() == SCH_SYMBOL_T && item->Type() == SCH_FIELD_T )
            {
                SCH_FIELD*  field = static_cast<SCH_FIELD*>( item );
                SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( item->GetParent() );

                if( field->GetId() == FIELD_T::REFERENCE )
                {
                    // Lazy eval of sheet list; this is expensive even when unsorted
                    if( sheets.empty() )
                        sheets = schematic->Hierarchy();

                    SCH_SHEET_PATH sheet = sheets.FindSheetForScreen( screen );
                    symbol->SetRef( &sheet, field->GetText() );
                }
            }

            // This must be called before any calls that require stable object pointers.
            screen->Update( item );

            // This hack is to prevent incorrectly parented symbol pins from breaking the
            // connectivity algorithm.
            if( item->Type() == SCH_SYMBOL_T )
            {
                SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( item );
                symbol->UpdatePins();

                CONNECTION_GRAPH* graph = schematic->ConnectionGraph();

                SCH_SYMBOL* symbolCopy = static_cast<SCH_SYMBOL*>( copy );
                graph->RemoveItem( symbolCopy );

                for( SCH_PIN* pin : symbolCopy->GetPins() )
                    graph->RemoveItem( pin );
            }

            item->SetConnectivityDirty();

            if( itemView )
                itemView->Add( item );

            delete copy;
            break;
        }

        default:
            wxASSERT( false );
            break;
        }
    }

    if( schematic )
    {
        if( bulkAddedItems.size() > 0 )
            schematic->OnItemsAdded( bulkAddedItems );

        if( bulkRemovedItems.size() > 0 )
            schematic->OnItemsRemoved( bulkRemovedItems );

        if( itemsChanged.size() > 0 )
            schematic->OnItemsChanged( itemsChanged );
    }

    for( auto& [screen, undo] : m_libraryCacheUndo )
        undo->RestoreForRollback();
    m_libraryCacheScopes.clear();
    m_libraryCacheUndo.clear();
    m_libraryCacheChanged = false;

    if( selTool )
        selTool->RebuildSelection();

    if( m_pageSettingsUndo && frame )
    {
        m_pageSettingsUndo->RestoreAll( frame, true );
        m_pageSettingsUndo.reset();
        frame->GetCanvas()->GetView()->MarkDirty();
        frame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
    }

    m_ercAddedMarkers.clear();

    if( frame )
        frame->RecalculateConnections( nullptr, m_connectivitySettingsChanged ? GLOBAL_CLEANUP : NO_CLEANUP );

    m_connectivitySettingsChanged = false;
    m_originId.clear();
    m_operationId.clear();
    clear();
}
