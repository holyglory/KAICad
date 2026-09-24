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
#include <refdes_tracker.h>
#include <schematic_settings.h>
#include <api/api_sch_state_groups.h>
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


bool SCH_COMMIT::PersistsChange( SCHEMATIC& aSchematic ) const
{
    // Staged settings, library caches, embedded files and ERC markers keep no item copy to
    // compare.  Owners stage them only to change them.
    if( m_embeddedFilesUndo || m_pageSettingsUndo || m_libraryCacheChanged || !m_libraryCacheUndo.empty()
            || !m_ercMarkers.empty() )
    {
        return true;
    }

    // Designators handed out since the reference inventory was kept are saved with the project.
    if( m_referenceInventoryKept )
    {
        const std::shared_ptr<REFDES_TRACKER>& live = aSchematic.Settings().m_refDesTracker;
        const std::vector<std::string> now = live ? live->GetAllocatedReferences() : std::vector<std::string>();
        const std::vector<std::string> kept =
                m_referenceInventory ? m_referenceInventory->GetAllocatedReferences() : std::vector<std::string>();

        if( now != kept )
            return true;
    }

    for( const COMMIT_LINE& entry : m_entries )
    {
        if( ( entry.m_type & CHT_TYPE ) != CHT_MODIFY || !entry.m_copy || !entry.m_item->IsSCH_ITEM() )
            return true;

        const std::string before =
                SCH_STATE_GROUPS::PersistedItem( aSchematic, static_cast<SCH_ITEM*>( entry.m_copy ) );

        if( before.empty()
                || before != SCH_STATE_GROUPS::PersistedItem( aSchematic, static_cast<SCH_ITEM*>( entry.m_item ) ) )
        {
            return true;
        }
    }

    return false;
}


void SCH_COMMIT::Abandon()
{
    for( COMMIT_LINE& entry : m_entries )
        delete entry.m_copy;

    m_entries.clear();
    clear();
    m_ercMarkers.clear();
    m_embeddedFilesUndo.reset();
    m_pageSettingsUndo.reset();
    m_libraryCacheScopes.clear();
    m_libraryCacheUndo.clear();
    m_libraryCacheChanged = false;
    m_connectivitySettingsChanged = false;
    m_netSettingsChanged = false;
    m_referenceInventoryKept = false;
    m_referenceInventory.reset();
}


std::unique_ptr<REFDES_TRACKER> SCH_COMMIT::CopyReferenceInventory( SCHEMATIC& aSchematic )
{
    const std::shared_ptr<REFDES_TRACKER>& live = aSchematic.Settings().m_refDesTracker;

    if( !live )
        return nullptr;

    auto copy = std::make_unique<REFDES_TRACKER>();
    copy->CopyAllocatedFrom( *live );
    return copy;
}


void SCH_COMMIT::RestoreReferenceInventory( SCHEMATIC& aSchematic, const REFDES_TRACKER* aKept )
{
    std::shared_ptr<REFDES_TRACKER>& live = aSchematic.Settings().m_refDesTracker;

    if( aKept )
    {
        if( !live )
            live = std::make_shared<REFDES_TRACKER>();

        live->CopyAllocatedFrom( *aKept );
    }
    else if( live )
    {
        live->Clear();
    }
}


void SCH_COMMIT::KeepReferenceInventory()
{
    SCH_EDIT_FRAME* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );

    if( frame )
        KeepReferenceInventory( frame->Schematic() );
}


void SCH_COMMIT::KeepReferenceInventory( SCHEMATIC& aSchematic )
{
    // A symbol editor commit hands out no schematic designators.
    if( m_isLibEditor || m_referenceInventoryKept )
        return;

    m_referenceInventoryKept = true;
    m_referenceInventory = CopyReferenceInventory( aSchematic );
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
            // Native pin sorting deliberately ignores UUID and can leave equal
            // sort keys in different orders. These are complete owned objects,
            // not an ordered design instruction. Treat only this collection as
            // a multiset; all IDs, properties and duplicate counts remain exact.
            comparer.TreatAsSet( kiapi::schematic::types::SchematicSymbol::descriptor()->FindFieldByName( "items" ) );
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
    m_pageSettingsUndo->IncludePages();
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
    m_pageSettingsUndo->IncludePages();
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
    m_netSettingsChanged |= aBefore.contains( "net_settings" ) && aAfter.contains( "net_settings" )
            && aBefore.at( "net_settings" ) != aAfter.at( "net_settings" );
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

void SCH_COMMIT::SetFieldTemplates( const kiapi::schematic::types::SchematicFieldTemplates& aValue )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Project field templates require a schematic editor" );
    auto& templates = frame->Prj().GetProjectFile().m_TemplateFieldNames;
    if( SCH_FIELD_TEMPLATES::Capture( templates ).SerializeAsString() == aValue.SerializeAsString() ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeFieldTemplates();
    SCH_FIELD_TEMPLATES::Restore( templates, aValue );
}

void SCH_COMMIT::SetSymbolComparison( const kiapi::schematic::types::SchematicSymbolComparisonSettings& aValue )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Symbol comparison policy requires a schematic editor" );
    auto& policy = frame->Schematic().Settings().m_SymbolParity;
    if( SCH_SYMBOL_COMPARISON::Capture( policy ).SerializeAsString() == aValue.SerializeAsString() ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeSymbolComparison();
    SCH_SYMBOL_COMPARISON::Restore( policy, aValue );
}

void SCH_COMMIT::SetBomSettings( const kiapi::schematic::types::SchematicBomSettings& aValue )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "BOM settings require a schematic editor" );
    auto& settings = frame->Schematic().Settings();
    if( SCH_BOM_SETTINGS::Capture( settings ).SerializeAsString() == aValue.SerializeAsString() ) return;
    auto prepared = SCH_BOM_SETTINGS::Prepare( aValue );
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeBomSettings();
    SCH_BOM_SETTINGS::Swap( settings, prepared );
}

void SCH_COMMIT::SetNetSettings( const kiapi::schematic::types::SchematicNetSettings& aValue )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    if( !frame || m_isLibEditor ) throw std::runtime_error( "Net settings require a schematic editor" );
    auto settings = frame->Prj().GetProjectFile().NetSettings();
    if( !settings ) throw std::runtime_error( "Project net settings are unavailable" );
    auto prepared = SCH_NET_SETTINGS::PrepareDeclared( aValue );
    if( SCH_NET_SETTINGS::SameDeclared( SCH_NET_SETTINGS::Capture( *settings ), aValue ) ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeNetSettings();
    m_connectivitySettingsChanged = true;
    m_netSettingsChanged = true;
    SCH_NET_SETTINGS::ApplyPrepared( *settings, *prepared );
}

void SCH_COMMIT::SetReferenceInventory( const REFDES_TRACKER& aPrepared )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    wxCHECK_RET( frame && !m_isLibEditor, "Reference inventory requires a schematic editor" );
    auto& tracker = frame->Schematic().Settings().m_refDesTracker;
    if( tracker && tracker->GetAllocatedReferences() == aPrepared.GetAllocatedReferences() ) return;
    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }
    m_pageSettingsUndo->IncludeReferenceInventory();
    if( !tracker ) tracker = std::make_shared<REFDES_TRACKER>();
    tracker->CopyAllocatedFrom( aPrepared );
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

    // Changes the markers in place. History restores the exclusions by sort key and deletes
    // the markers created here by identity; it never keeps a marker pointer, because the ERC
    // dialog and Run ERC delete markers outside any commit.
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
    if( !StageErcEdit() )
    {
        aFailure = "ERC replacement requires a schematic editor";
        return false;
    }
    SCH_ERC_HISTORY::STATE& history = m_pageSettingsUndo->ErcMarkers();
    for( const auto& [screen, marker] : markers )
    {
        const std::string key = ERC_EXCLUSION::FromMarker( *marker ).GetSortKey();
        const auto found = desired.find( key );
        const bool excluded = found != desired.end();
        const wxString comment = excluded ? wxString::FromUTF8( found->second->comment ) : wxString();
        if( marker->IsExcluded() != excluded || marker->GetComment() != comment )
        {
            marker->SetExcluded( excluded, comment );
            if( screen == frame->GetScreen() ) frame->GetCanvas()->GetView()->Update( marker );
        }
        if( excluded ) found->second->marker.reset(); // existing native marker retains its UUID
    }
    for( auto& exclusion : aPrepared.exclusions )
    {
        if( !exclusion.marker ) continue;
        exclusion.marker->SetExcluded( true, wxString::FromUTF8( exclusion.comment ) );
        // Undo deletes this marker by identity; Revert does as well.
        history.created.insert( exclusion.marker->m_Uuid );
        frame->AddToScreen( exclusion.marker.release(), exclusion.screen );
    }
    SCH_ERC_SETTINGS::RestorePolicy( schematic.ErcSettings(), aPrepared.canonical );
    frame->RefreshErcDialog();
    return true;
}


bool SCH_COMMIT::StageErcEdit()
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );

    if( !frame || m_isLibEditor )
        return false;

    if( !m_pageSettingsUndo )
    {
        m_pageSettingsUndo = std::make_unique<SCH_PAGE_SETTINGS_UNDO_ITEM>( frame );
        m_pageSettingsUndo->SetFlags( UR_TRANSIENT );
    }

    m_pageSettingsUndo->IncludeErcPolicy();
    m_pageSettingsUndo->IncludeErcMarkers();
    return true;
}


bool SCH_COMMIT::ErcEditChanged() const
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    return frame && m_pageSettingsUndo && m_pageSettingsUndo->ErcChanged( frame->Schematic() );
}


std::map<std::string, wxString> SCH_ERC_HISTORY::CaptureExclusions( SCHEMATIC& aSchematic )
{
    std::map<std::string, wxString> exclusions;
    std::set<SCH_SCREEN*>           seen;

    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
    {
        SCH_SCREEN* screen = path.LastScreen();

        if( !screen || !seen.insert( screen ).second )
            continue;

        for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
        {
            auto* marker = static_cast<SCH_MARKER*>( item );

            if( marker->IsExcluded() )
                exclusions.emplace( ERC_EXCLUSION::FromMarker( *marker ).GetSortKey(), marker->GetComment() );
        }
    }

    return exclusions;
}


SCH_ERC_HISTORY::RECORD SCH_ERC_HISTORY::Record( const SCH_MARKER& aMarker, const SCH_SCREEN& aScreen )
{
    RECORD record;
    record.uuid = aMarker.m_Uuid;
    record.screen = aScreen.GetUuid();
    record.marker = ERC_EXCLUSION::FromMarker( aMarker ).GetSortKey();
    record.excluded = aMarker.IsExcluded();
    record.comment = aMarker.GetComment();

    // Keep a violation-specific message; the rule's default text follows the language.
    if( std::shared_ptr<RC_ITEM> item = aMarker.GetRCItem();
        item && item->GetErrorMessage( false ) != item->GetErrorText( false ) )
    {
        record.message = item->GetErrorMessage( false );
    }

    return record;
}


bool SCH_ERC_HISTORY::Restore( SCH_EDIT_FRAME* aFrame, const STATE& aState, STATE* aOpposite )
{
    SCHEMATIC&          schematic = aFrame->Schematic();
    const SCH_SHEET_LIST hierarchy = schematic.Hierarchy();
    SCH_SELECTION_TOOL* selTool = aFrame->GetToolManager()->GetTool<SCH_SELECTION_TOOL>();
    std::vector<SCH_SCREEN*> screens;
    bool                changed = false;

    for( const SCH_SHEET_PATH& path : hierarchy )
    {
        if( SCH_SCREEN* screen = path.LastScreen();
            screen && std::find( screens.begin(), screens.end(), screen ) == screens.end() )
        {
            screens.push_back( screen );
        }
    }

    auto liveMarkers = [&]()
    {
        std::vector<std::pair<SCH_SCREEN*, SCH_MARKER*>> markers;

        for( SCH_SCREEN* screen : screens )
        {
            for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
                markers.emplace_back( screen, static_cast<SCH_MARKER*>( item ) );
        }

        return markers;
    };

    // Markers the undone change created leave again.  A marker that Run ERC or the ERC dialog
    // already deleted is simply absent: identities, never pointers, are looked up here.
    for( const auto& [screen, marker] : liveMarkers() )
    {
        if( !aState.created.count( marker->m_Uuid ) )
            continue;

        if( aOpposite )
            aOpposite->removed.push_back( Record( *marker, *screen ) );

        if( marker->IsSelected() && selTool )
            selTool->RemoveItemFromSel( marker, true /* quiet mode */ );

        aFrame->RemoveFromScreen( marker, screen );
        delete marker;
        changed = true;
    }

    auto add = [&]( std::unique_ptr<SCH_MARKER> aMarker, SCH_SCREEN* aScreen )
    {
        // A marker whose sheet path names no loaded screen stays out rather than moving to
        // another sheet; its exclusion no longer applies to this schematic.
        if( !aScreen )
        {
            wxLogTrace( wxT( "KICAD_SCH_TRACKING" ),
                        wxS( "An ERC marker's sheet has no loaded screen; it is not restored" ) );
            return;
        }

        const KIID uuid = aMarker->m_Uuid;
        aFrame->AddToScreen( aMarker.release(), aScreen );

        if( aOpposite )
            aOpposite->created.insert( uuid );

        changed = true;
    };

    // Markers it removed or rewrote return with their identity, unless that marker or a marker
    // for the same violation (a later Run ERC computes new ones) is still present.
    std::set<KIID>        present;
    std::set<std::string> violations;

    for( const auto& [screen, marker] : liveMarkers() )
    {
        present.insert( marker->m_Uuid );
        violations.insert( ERC_EXCLUSION::FromMarker( *marker ).GetSortKey() );
    }

    for( const RECORD& record : aState.removed )
    {
        kiapi::schematic::ErcMarker data;

        if( present.count( record.uuid ) || violations.count( record.marker )
                || !data.ParseFromString( record.marker ) )
        {
            continue;
        }

        std::unique_ptr<SCH_MARKER> marker( SCH_MARKER::FromProto( data, hierarchy ) );

        if( !marker )
            continue;

        const_cast<KIID&>( marker->m_Uuid ) = record.uuid;
        marker->SetExcluded( record.excluded, record.comment );

        if( !record.message.IsEmpty() )
            marker->GetRCItem()->SetErrorMessage( record.message );

        SCH_SCREEN* screen = nullptr;

        for( SCH_SCREEN* candidate : screens )
        {
            if( candidate->GetUuid() == record.screen )
                screen = candidate;
        }

        present.insert( record.uuid );
        violations.insert( record.marker );
        add( std::move( marker ), screen ? screen : SCH_ERC_SETTINGS::OwnerScreen( data, hierarchy, schematic ) );
    }

    // Exactly the saved exclusions are excluded, each on every marker with its sort key.
    std::set<std::string> resolved;

    for( const auto& [screen, marker] : liveMarkers() )
    {
        const auto     found = aState.exclusions.find( ERC_EXCLUSION::FromMarker( *marker ).GetSortKey() );
        const bool     excluded = found != aState.exclusions.end();
        const wxString comment = excluded ? found->second : wxString();

        if( excluded )
            resolved.insert( found->first );

        if( marker->IsExcluded() != excluded || marker->GetComment() != comment )
        {
            marker->SetExcluded( excluded, comment );

            if( screen == aFrame->GetScreen() )
                aFrame->GetCanvas()->GetView()->Update( marker );

            changed = true;
        }
    }

    // An exclusion whose marker is gone gets a marker again, as loading the project does.
    for( const auto& [key, comment] : aState.exclusions )
    {
        kiapi::schematic::ErcMarker data;

        if( resolved.count( key ) || !data.ParseFromString( key ) )
            continue;

        std::unique_ptr<SCH_MARKER> marker( SCH_MARKER::FromProto( data, hierarchy ) );

        if( !marker )
        {
            wxLogTrace( wxT( "KICAD_SCH_TRACKING" ), wxS( "An ERC exclusion no longer resolves in this schematic" ) );
            continue;
        }

        marker->SetExcluded( true, comment );
        add( std::move( marker ), SCH_ERC_SETTINGS::OwnerScreen( data, hierarchy, schematic ) );
    }

    return changed;
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
    m_pageSettingsUndo->IncludePages();
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

    // ERC markers never become undo entries: see stageErcMarker().
    if( aItem->Type() == SCH_MARKER_T && stageErcMarker( static_cast<SCH_MARKER*>( aItem ), aChangeType, aScreen ) )
        return *this;

    // A removal supersedes the earlier modify entry. Automation wire cleanup
    // may have removed an already-moved wire from its screen; retain the original
    // geometry before COMMIT::Stage discards that image. Do not update an absent
    // screen entry, or change the ownership rules for remapped child objects.
    const bool appliedAutomationWire = m_automationBatch && aItem->Type() == SCH_LINE_T
            && aChangeType == ( CHT_REMOVE | CHT_DONE );
    if( !m_isLibEditor && ( aChangeType == CHT_REMOVE || appliedAutomationWire )
        && undoLevelItem( aItem ) == aItem )
    {
        COMMIT_LINE* previous = findEntry( aItem, aScreen );

        if( previous && ( previous->m_type & CHT_TYPE ) == CHT_MODIFY && previous->m_copy )
        {
            auto item = static_cast<SCH_ITEM*>( aItem );
            item->SwapItemData( static_cast<SCH_ITEM*>( previous->m_copy ) );

            if( appliedAutomationWire )
            {
                // SwapItemData intentionally retains edit flags. These were
                // set by the finished drag, not by another live user edit.
                item->ClearEditFlags();
                item->ClearFlags( IN_EDIT | SELECTED_BY_DRAG );
            }
            else if( auto screen = dynamic_cast<SCH_SCREEN*>( aScreen ) )
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


/// Run ERC, Delete Marker and Delete All Markers delete ERC markers outside any commit, so an
/// undo entry holding a marker pointer could later touch freed memory.  A staged marker is
/// therefore applied or reverted by this commit itself, and history keeps what it was as a
/// detached record (SCH_ERC_HISTORY): Undo rebuilds it and Redo deletes it by identity.
bool SCH_COMMIT::stageErcMarker( SCH_MARKER* aMarker, int aChangeType, BASE_SCREEN* aScreen )
{
    auto* frame = dynamic_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );

    if( !frame || m_isLibEditor || !StageErcEdit() )
        return false;

    SCH_SCREEN* screen = aScreen ? dynamic_cast<SCH_SCREEN*>( aScreen ) : frame->GetScreen();

    if( !screen )
        return false;

    SCH_ERC_HISTORY::STATE& history = m_pageSettingsUndo->ErcMarkers();
    const int               type = aChangeType & CHT_TYPE;
    auto                    staged = std::find_if( m_ercMarkers.begin(), m_ercMarkers.end(),
                                                   [&]( const STAGED_MARKER& aStaged )
                                                   {
                                                       return aStaged.marker == aMarker;
                                                   } );

    if( staged != m_ercMarkers.end() )
    {
        // A later modification belongs to the first staging; only a removal supersedes it.
        if( type == CHT_REMOVE && staged->type != CHT_REMOVE )
        {
            // A marker added by this commit never existed before it.
            if( staged->type == CHT_ADD )
                history.created.erase( aMarker->m_Uuid );

            staged->type = CHT_REMOVE;
        }

        return true;
    }

    STAGED_MARKER entry{ aMarker, screen, type, type == CHT_ADD, nullptr };

    if( type == CHT_ADD )
    {
        history.created.insert( aMarker->m_Uuid );
    }
    else
    {
        history.removed.push_back( SCH_ERC_HISTORY::Record( *aMarker, *screen ) );

        if( type == CHT_MODIFY )
        {
            // The edited marker is replaced by its record when this change is undone.
            history.created.insert( aMarker->m_Uuid );
            entry.image.reset( static_cast<SCH_MARKER*>( aMarker->Clone() ) );
        }
    }

    m_ercMarkers.push_back( std::move( entry ) );
    return true;
}


void SCH_COMMIT::pushErcMarkers()
{
    auto*               frame = static_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );
    SCH_SELECTION_TOOL* selTool = m_toolMgr->GetTool<SCH_SELECTION_TOOL>();

    for( STAGED_MARKER& staged : m_ercMarkers )
    {
        const bool listed = staged.screen->CheckIfOnDrawList( staged.marker );

        if( staged.type == CHT_REMOVE )
        {
            if( staged.marker->IsSelected() && selTool )
                selTool->RemoveItemFromSel( staged.marker, true /* quiet mode */ );

            if( listed )
                frame->RemoveFromScreen( staged.marker, staged.screen );

            delete staged.marker;
        }
        else if( staged.type == CHT_ADD && !listed )
        {
            frame->AddToScreen( staged.marker, staged.screen );
        }
        else if( staged.screen == frame->GetScreen() )
        {
            frame->GetCanvas()->GetView()->Update( staged.marker );
        }
    }

    if( !m_ercMarkers.empty() )
        frame->RefreshErcDialog();

    m_ercMarkers.clear();
}


void SCH_COMMIT::revertErcMarkers()
{
    auto* frame = static_cast<SCH_EDIT_FRAME*>( m_toolMgr->GetToolHolder() );

    if( m_ercMarkers.empty() || !frame )
        return;

    SCH_ERC_HISTORY::STATE& history = m_pageSettingsUndo->ErcMarkers();

    for( auto it = m_ercMarkers.rbegin(); it != m_ercMarkers.rend(); ++it )
    {
        STAGED_MARKER& staged = *it;
        const bool     listed = staged.screen->CheckIfOnDrawList( staged.marker );

        // The live markers are reverted in place here, so history must not rebuild them.
        history.created.erase( staged.marker->m_Uuid );
        std::erase_if( history.removed, [&]( const SCH_ERC_HISTORY::RECORD& aRecord )
                       {
                           return aRecord.uuid == staged.marker->m_Uuid;
                       } );

        if( staged.added )
        {
            if( listed )
                frame->RemoveFromScreen( staged.marker, staged.screen );

            delete staged.marker;
            continue;
        }

        if( staged.image )
            staged.marker->SwapItemData( staged.image.get() );

        if( !listed )
            frame->AddToScreen( staged.marker, staged.screen );
        else if( staged.screen == frame->GetScreen() )
            frame->GetCanvas()->GetView()->Update( staged.marker );
    }

    m_ercMarkers.clear();
    frame->RefreshErcDialog();
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
    SCH_GROUP*          enteredGroup = selTool && !m_automationBatch ? selTool->GetEnteredGroup() : nullptr;
    bool                itemsDeselected = false;
    bool                selectedModified = false;
    bool                dirtyConnectivity = m_connectivitySettingsChanged;
    bool                cleanupVisibleScreen = m_connectivitySettingsChanged;
    bool                refreshHierarchy = false;
    SCH_CLEANUP_FLAGS   connectivityCleanUp = m_connectivitySettingsChanged ? GLOBAL_CLEANUP : NO_CLEANUP;

    if( Empty() )
        return;

    undoList.SetDescription( aMessage );
    undoList.SetPreserveSchematicGeometry( m_automationBatch );
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
            [&]( SCH_ITEM* schItem, SCH_SCREEN* screen )
            {
                if( schItem->IsConnectable() || ( schItem->Type() == SCH_RULE_AREA_T ) )
                {
                    dirtyConnectivity = true;
                    cleanupVisibleScreen |= screen == currentScreen;

                    // Do a local clean up if there are any connectable objects in the commit.
                    if( connectivityCleanUp == NO_CLEANUP )
                        connectivityCleanUp = LOCAL_CLEANUP;

                    // Do a full rebuild of the connectivity if there is a sheet in the commit.
                    if( schItem->Type() == SCH_SHEET_T )
                        connectivityCleanUp = GLOBAL_CLEANUP;
                }
            };

    // Only additions on the entered group's own screen can acquire membership.
    if( enteredGroup && frame && std::any_of( m_entries.begin(), m_entries.end(),
            [&]( const COMMIT_LINE& entry )
            {
                return entry.m_screen == currentScreen && ( entry.m_type & CHT_TYPE ) == CHT_ADD;
            } ) )
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
            if( enteredGroup && screen == currentScreen && schItem->IsGroupableType() && !schItem->GetParentGroup() )
                selTool->GetEnteredGroup()->AddItem( schItem );

            updateConnectivityFlag( schItem, screen );

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
            updateConnectivityFlag( schItem, screen );

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
                updateConnectivityFlag( schItem, screen );
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
    if( frame )
        pushErcMarkers();

    if( m_pageSettingsUndo && frame )
    {
        // ERC settings and markers change no sheet, page or title.
        if( !m_pageSettingsUndo->IncludesOnlyErc() )
        {
            schematic->RefreshHierarchy();
            frame->UpdateHierarchyNavigator();
        }

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
            frame->RecalculateConnections( this,
                    connectivityCleanUp == LOCAL_CLEANUP && !cleanupVisibleScreen
                            ? NO_CLEANUP : connectivityCleanUp, nullptr, m_automationBatch );
        else if( schematic )
            schematic->RecalculateConnections( this, connectivityCleanUp, m_toolMgr );
    }

    if( m_netSettingsChanged && frame )
        SCH_PAGE_SETTINGS_UNDO_ITEM::RefreshNetSettings( frame );

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
    // Designators handed out for pushed items stay handed out, as they always have.
    m_referenceInventoryKept = false;
    m_referenceInventory.reset();

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
    m_ercMarkers.clear();
    m_libraryCacheScopes.clear();
    m_libraryCacheUndo.clear();
    m_libraryCacheChanged = false;
    m_connectivitySettingsChanged = false;
    m_netSettingsChanged = false;
    m_originId.clear();
    m_operationId.clear();
    m_automationBatch = false;
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

    // Nothing this commit annotated is placed any more, so the designators handed out since
    // the reference inventory was kept are returned, whether or not anything else was staged.
    if( m_referenceInventoryKept )
    {
        if( frame )
            RestoreReferenceInventory( frame->Schematic(), m_referenceInventory.get() );

        m_referenceInventoryKept = false;
        m_referenceInventory.reset();
    }

    if( Empty() && m_libraryCacheUndo.empty() )
        return;

    if( m_isLibEditor )
    {
        revertLibEdit();
        return;
    }

    // An unchanged ERC dialog edit restores nothing but ERC settings and markers.
    const bool ercOnly = COMMIT::Empty() && !m_embeddedFilesUndo && m_libraryCacheUndo.empty() && frame
                         && m_pageSettingsUndo && m_pageSettingsUndo->IncludesOnlyErc()
                         && !m_connectivitySettingsChanged && !m_netSettingsChanged;

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

    if( frame )
        revertErcMarkers();

    if( m_pageSettingsUndo && frame && ercOnly )
    {
        m_pageSettingsUndo->RestoreErc( frame );
        m_pageSettingsUndo.reset();
    }
    else if( m_pageSettingsUndo && frame )
    {
        m_pageSettingsUndo->RestoreAll( frame, true );
        m_pageSettingsUndo.reset();
        frame->GetCanvas()->GetView()->MarkDirty();
        frame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
    }

    m_ercMarkers.clear();

    if( frame && !ercOnly )
        frame->RecalculateConnections( nullptr, m_connectivitySettingsChanged ? GLOBAL_CLEANUP : NO_CLEANUP );

    if( m_netSettingsChanged && frame )
        SCH_PAGE_SETTINGS_UNDO_ITEM::RefreshNetSettings( frame );

    m_connectivitySettingsChanged = false;
    m_netSettingsChanged = false;
    m_originId.clear();
    m_operationId.clear();
    m_automationBatch = false;
    clear();
}
