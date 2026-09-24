/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <api/api_sch_state_groups.h>

#include <api/document_change_journal.h>
#include <erc/erc_exclusion.h>
#include <io/kicad/kicad_io_utils.h>
#include <nlohmann/json.hpp>
#include <project.h>
#include <project/project_file.h>
#include <lib_symbol.h>
#include <richio.h>
#include <sch_commit.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr_lib_cache.h>
#include <sch_marker.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <sch_symbol.h>
#include <schematic.h>
#include <tools/sch_selection.h>
#include <wx/log.h>

#include <algorithm>
#include <chrono>
#include <exception>
#include <set>
#include <stdexcept>
#include <utility>

// Identities, sizes and timings only: never design or settings text.
static const wxChar* const traceSchTracking = wxT( "KICAD_SCH_TRACKING" );


namespace
{
/// The sheet every loaded screen is written through, keyed by screen identity.  The first
/// top-level sheet also writes the schematic-wide embedded files and net chains, so its
/// screen is written through that sheet, as the save path does.
std::map<std::string, SCH_SHEET*> writtenScreens( SCHEMATIC& aSchematic )
{
    std::map<std::string, SCH_SHEET*> screens;

    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
    {
        if( SCH_SCREEN* screen = path.LastScreen() )
            screens.try_emplace( screen->GetUuid().AsStdString(), path.Last() );
    }

    SCH_SHEET* first = aSchematic.GetTopLevelSheet( 0 );

    if( !first || !first->GetScreen() )
        throw std::runtime_error( "The schematic has no top-level sheet to write" );

    screens[first->GetScreen()->GetUuid().AsStdString()] = first;
    return screens;
}


/// The project settings exactly as saving writes them.  Saving first records the live ERC
/// exclusions (SCHEMATIC::RecordERCExclusions), so the stored exclusion list can lag an
/// unsaved exclusion edit.  Digest the list the save will write, computed the same way on a
/// copy: capturing must never update the saved cache behind the editor.  A project without
/// its ERC section cannot be digested the way it is saved, so the capture fails instead of
/// silently digesting the stale stored exclusion list.
nlohmann::json persistedProjectSettings( SCHEMATIC& aSchematic )
{
    nlohmann::json settings = aSchematic.Project().GetProjectFile().CaptureCurrentState();
    auto           erc = settings.find( "erc" );

    if( erc == settings.end() || !erc->is_object() )
        throw std::runtime_error( "The project settings have no ERC section to digest" );

    std::set<ERC_EXCLUSION, ERC_EXCLUSION_COMPARE> exclusions;

    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
    {
        if( SCH_SCREEN* screen = path.LastScreen() )
        {
            for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
            {
                SCH_MARKER* marker = static_cast<SCH_MARKER*>( item );

                if( marker->IsExcluded() )
                    exclusions.insert( ERC_EXCLUSION::FromMarker( *marker ) );
            }
        }
    }

    nlohmann::json recorded = nlohmann::json::array();

    for( const ERC_EXCLUSION& exclusion : exclusions )
        recorded.push_back( exclusion );

    ( *erc )["erc_exclusions"] = std::move( recorded );
    return settings;
}
} // namespace


void SCH_STATE_GROUPS::add( const std::string& aName, const NATIVE_STATE_DIGEST& aState )
{
    m_document.Add( aName, aState );
    m_groups.emplace( aName, std::to_string( aState.Bytes() ) + ":" + aState.Hex() );
    m_bytes += aState.Bytes();
}


SCH_STATE_GROUPS SCH_STATE_GROUPS::CaptureScreens( SCHEMATIC& aSchematic,
                                                   const std::vector<const SCH_SCREEN*>& aScreens,
                                                   bool aWithProjectSettings )
{
    if( !aSchematic.IsValid() )
        throw std::runtime_error( "The schematic is not loaded" );

    const auto        started = std::chrono::steady_clock::now();
    SCH_STATE_GROUPS  result;

    for( const auto& [id, sheet] : writtenScreens( aSchematic ) )
    {
        if( !aScreens.empty()
                && std::find( aScreens.begin(), aScreens.end(), sheet->GetScreen() ) == aScreens.end() )
        {
            continue;
        }

        NATIVE_STATE_DIGEST state;
        SCH_IO_KICAD_SEXPR  writer;

        // No resource preparation: capturing must not embed fonts or otherwise edit.
        writer.FormatSchematicToFormatter( &state, sheet, &aSchematic, nullptr, false );
        result.add( "screen:" + id, state );
        result.m_sheets.push_back( sheet );
    }

    if( aScreens.empty() || aWithProjectSettings )
    {
        NATIVE_STATE_DIGEST settings;
        settings.Append( persistedProjectSettings( aSchematic ).dump() );
        result.add( "project-settings", settings );
    }

    if( !aScreens.empty() && result.m_sheets.size() != aScreens.size() )
        throw std::runtime_error( "A tracked screen is not part of the schematic" );

    wxLogTrace( traceSchTracking, wxS( "Captured %zu persisted group(s), %llu bytes, in %lld us" ),
                result.m_groups.size(), static_cast<unsigned long long>( result.m_bytes ),
                static_cast<long long>( std::chrono::duration_cast<std::chrono::microseconds>(
                        std::chrono::steady_clock::now() - started ).count() ) );

    return result;
}


SCH_STATE_GROUPS SCH_STATE_GROUPS::Capture( SCHEMATIC& aSchematic )
{
    return CaptureScreens( aSchematic, {} );
}


std::string SCH_STATE_GROUPS::PersistedItem( SCHEMATIC& aSchematic, SCH_ITEM* aItem )
{
    if( !aItem )
        return {};

    // The item types the schematic writer saves at screen level.  Fields, pins and sheet
    // pins are saved with their parent, which is what a commit stages; ERC markers are
    // staged separately.  Anything else has no saved form here and counts as changed.
    switch( aItem->Type() )
    {
    case SCH_SYMBOL_T:
    case SCH_BITMAP_T:
    case SCH_SHEET_T:
    case SCH_JUNCTION_T:
    case SCH_NO_CONNECT_T:
    case SCH_BUS_WIRE_ENTRY_T:
    case SCH_BUS_BUS_ENTRY_T:
    case SCH_LINE_T:
    case SCH_SHAPE_T:
    case SCH_RULE_AREA_T:
    case SCH_TEXT_T:
    case SCH_LABEL_T:
    case SCH_GLOBAL_LABEL_T:
    case SCH_HIER_LABEL_T:
    case SCH_DIRECTIVE_LABEL_T:
    case SCH_TEXTBOX_T:
    case SCH_TABLE_T:
    case SCH_GROUP_T:
        break;

    default:
        return {};
    }

    // The writer looks cached library definitions up through the selection's screen.  Both
    // sides of a comparison would write the same, current, cache, so give it none and write
    // the symbol's own definition below instead.  Without clipboard mode every instance of
    // the item is written and the selection path is not consulted.
    SCH_SCREEN         noCache;
    SCH_SELECTION      selection( &noCache );
    SCH_SHEET_PATH     unusedPath;
    STRING_FORMATTER   out;
    SCH_IO_KICAD_SEXPR writer;

    selection.Add( aItem );
    writer.Format( &selection, &unusedPath, aSchematic, &out, false );

    if( aItem->Type() == SCH_SYMBOL_T )
    {
        SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( aItem );

        // The screen caches this definition: a pin map, embedded file or other definition
        // edit reaches the saved cache through it.
        if( const std::unique_ptr<LIB_SYMBOL>& definition = symbol->GetLibSymbolRef() )
        {
            SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol( definition.get(), out, symbol->GetSchSymbolLibraryName(),
                                                      true, true );
        }
    }

    // Never empty for a written item: the writer always opens its own s-expression.
    return out.GetString();
}


std::vector<std::string> SCH_STATE_GROUPS::ChangedGroups( const SCH_STATE_GROUPS& aAfter ) const
{
    std::vector<std::string> changed;

    for( const auto& [name, digest] : m_groups )
    {
        auto it = aAfter.m_groups.find( name );

        if( it == aAfter.m_groups.end() || it->second != digest )
            changed.push_back( name );
    }

    for( const auto& [name, digest] : aAfter.m_groups )
    {
        if( !m_groups.count( name ) )
            changed.push_back( name );
    }

    return changed;
}


SCH_PERSISTED_PARTS SCH_PERSISTED_PARTS::PageSettings()
{
    SCH_PERSISTED_PARTS parts;
    parts.pages = true;
    parts.schematicWide = true;
    return parts;
}


SCH_PERSISTED_PARTS SCH_PERSISTED_PARTS::SheetImport( const SCH_SCREEN* aScreen )
{
    if( !aScreen )
        throw std::invalid_argument( "A sheet import is tracked on the screen it loads into" );

    SCH_PERSISTED_PARTS parts;
    parts.identities = true;
    parts.schematicWide = true;
    parts.libraryCaches = { aScreen };
    return parts;
}


SCH_PERSISTED_PARTS_STATE SCH_PERSISTED_PARTS_STATE::Capture( SCHEMATIC& aSchematic,
                                                              const SCH_PERSISTED_PARTS& aParts )
{
    if( !aSchematic.IsValid() )
        throw std::runtime_error( "The schematic is not loaded" );

    const auto                started = std::chrono::steady_clock::now();
    SCH_PERSISTED_PARTS_STATE result;
    size_t                    cachesFound = 0;

    auto keep = [&]( const std::string& aName, std::string aText )
    {
        result.m_bytes += aText.size();
        result.m_texts.emplace( aName, std::move( aText ) );
    };

    // Every screen is taken once, keyed by its identity, as the whole-state capture takes it.
    for( const auto& [id, sheet] : writtenScreens( aSchematic ) )
    {
        SCH_SCREEN* screen = sheet->GetScreen();

        if( aParts.pages )
        {
            // SCH_IO_KICAD_SEXPR::Format writes exactly these two, one after the other.
            STRING_FORMATTER out;
            screen->GetPageSettings().Format( &out );
            screen->GetTitleBlock().Format( &out );
            keep( "page:" + id, out.GetString() );
        }

        if( aParts.identities )
        {
            // Markers are not saved; every other item on the screen is, under its identity.
            std::vector<KIID>& identities = result.m_identities[id];

            for( SCH_ITEM* item : screen->Items() )
            {
                if( item->Type() != SCH_MARKER_T )
                    identities.push_back( item->m_Uuid );
            }

            std::sort( identities.begin(), identities.end() );
            result.m_bytes += identities.size() * sizeof( KIID );
        }

        if( std::find( aParts.libraryCaches.begin(), aParts.libraryCaches.end(), screen )
                != aParts.libraryCaches.end() )
        {
            // As SCH_IO_KICAD_SEXPR::Format saves the screen's cache library.
            STRING_FORMATTER out;
            out.Print( "(lib_symbols" );

            for( const auto& [libItemName, libSymbol] : screen->GetLibSymbols() )
                SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol( libSymbol, out, libItemName, true, true );

            out.Print( ")" );
            keep( "lib_symbols:" + id, out.GetString() );
            ++cachesFound;
        }
    }

    // Restricting the capture to a screen that is not loaded would compare nothing.
    if( cachesFound != aParts.libraryCaches.size() )
        throw std::runtime_error( "A tracked library cache is not part of the schematic" );

    if( aParts.schematicWide )
    {
        // As the first top-level sheet saves them: the embedded fonts flag and embedded files
        // follow its items.  Its net chains are written from these definitions; whether a chain
        // is committed in the live connection graph is not saved.
        STRING_FORMATTER out;
        KICAD_FORMAT::FormatBool( &out, "embedded_fonts", aSchematic.GetAreFontsEmbedded() );

        if( !aSchematic.GetEmbeddedFiles()->IsEmpty() )
            aSchematic.WriteEmbeddedFiles( out, true );

        keep( "schematic-wide", out.GetString() );

        std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION> chains;

        if( CONNECTION_GRAPH* graph = aSchematic.ConnectionGraph() )
            chains = graph->GetNetChainDefinitions();

        for( auto& [name, definition] : chains )
            definition.committed = false;

        result.m_netChains = std::move( chains );
    }

    keep( "project-settings", persistedProjectSettings( aSchematic ).dump() );

    wxLogTrace( traceSchTracking, wxS( "Captured %zu persisted part(s) and %zu identity list(s), %llu bytes, in %lld us" ),
                result.m_texts.size(), result.m_identities.size(), static_cast<unsigned long long>( result.m_bytes ),
                static_cast<long long>( std::chrono::duration_cast<std::chrono::microseconds>(
                        std::chrono::steady_clock::now() - started ).count() ) );

    return result;
}


bool SCH_PERSISTED_PARTS_STATE::operator==( const SCH_PERSISTED_PARTS_STATE& aOther ) const
{
    return m_texts == aOther.m_texts && m_identities == aOther.m_identities && m_netChains == aOther.m_netChains;
}


std::vector<std::string> SCH_PERSISTED_PARTS_STATE::ChangedParts( const SCH_PERSISTED_PARTS_STATE& aAfter ) const
{
    std::vector<std::string> changed;

    auto compare = [&]( const auto& aBefore, const auto& aAfterParts, const std::string& aPrefix )
    {
        for( const auto& [name, value] : aBefore )
        {
            auto it = aAfterParts.find( name );

            if( it == aAfterParts.end() || it->second != value )
                changed.push_back( aPrefix + name );
        }

        for( const auto& [name, value] : aAfterParts )
        {
            if( !aBefore.count( name ) )
                changed.push_back( aPrefix + name );
        }
    };

    compare( m_texts, aAfter.m_texts, "" );
    compare( m_identities, aAfter.m_identities, "identities:" );

    if( m_netChains != aAfter.m_netChains )
        changed.push_back( "net-chains" );

    return changed;
}


SCH_TRACKED_CHANGE::MARK SCH_TRACKED_CHANGE::Mark( const SCHEMATIC& aSchematic )
{
    return { aSchematic.ChangeJournal().Epoch(), aSchematic.ChangeJournal().Sequence() };
}


SCH_TRACKED_CHANGE::SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription ) :
        m_schematic( aSchematic ),
        m_description( std::move( aDescription ) ),
        m_start( Mark( aSchematic ) )
{
    m_before = capture();
}


SCH_TRACKED_CHANGE::SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription,
                                        SCH_COMMIT& aCommit ) :
        m_schematic( aSchematic ),
        m_description( std::move( aDescription ) ),
        m_commit( &aCommit ),
        m_start( Mark( aSchematic ) )
{
    // Nothing is captured: the commit keeps a copy of every item it stages.
}


SCH_TRACKED_CHANGE::SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription,
                                        SCH_PERSISTED_PARTS aParts ) :
        m_schematic( aSchematic ),
        m_description( std::move( aDescription ) ),
        m_parts( std::move( aParts ) ),
        m_start( Mark( aSchematic ) )
{
    // Naming no part would compare only the project settings; an owner states what it reaches.
    if( !m_parts->pages && !m_parts->schematicWide && !m_parts->identities && m_parts->libraryCaches.empty() )
        throw std::invalid_argument( "A part-restricted tracked change needs its parts" );

    if( std::find( m_parts->libraryCaches.begin(), m_parts->libraryCaches.end(), nullptr )
            != m_parts->libraryCaches.end() )
    {
        throw std::invalid_argument( "A tracked library cache needs its screen" );
    }

    m_partsBefore = captureParts();
}


SCH_TRACKED_CHANGE::SCH_TRACKED_CHANGE( SCHEMATIC& aSchematic, std::string aDescription,
                                        std::vector<const SCH_SCREEN*> aScreens, const MARK& aSince ) :
        m_schematic( aSchematic ),
        m_description( std::move( aDescription ) ),
        m_screens( std::move( aScreens ) ),
        m_start( aSince )
{
    // Restricting the capture to nothing would silently widen it to the whole state.
    if( m_screens.empty() || std::find( m_screens.begin(), m_screens.end(), nullptr ) != m_screens.end() )
        throw std::invalid_argument( "A screen-restricted tracked change needs its screens" );

    // An action that is already a revision needs no comparison.
    if( !recordedSinceStart() )
        m_before = capture();
}


SCH_TRACKED_CHANGE::~SCH_TRACKED_CHANGE()
{
    if( m_complete )
        return;

    try
    {
        Complete();
    }
    catch( const std::exception& error )
    {
        wxLogTrace( traceSchTracking, wxS( "Unable to complete tracked change '%s': %s" ),
                    m_description, error.what() );
    }
}


void SCH_TRACKED_CHANGE::ChangedOutsideCommit()
{
    if( !m_commit )
        throw std::logic_error( "Only a staged tracked change counts a change outside its commit" );

    m_changedOutsideCommit = true;
}


std::optional<SCH_STATE_GROUPS> SCH_TRACKED_CHANGE::capture() const
{
    try
    {
        // The screen-restricted form compares the project settings as well.
        return SCH_STATE_GROUPS::CaptureScreens( m_schematic, m_screens, true );
    }
    catch( const std::exception& error )
    {
        wxLogTrace( traceSchTracking, wxS( "Unable to capture persisted state for '%s': %s" ),
                    m_description, error.what() );
        return std::nullopt;
    }
}


std::optional<SCH_PERSISTED_PARTS_STATE> SCH_TRACKED_CHANGE::captureParts() const
{
    try
    {
        return SCH_PERSISTED_PARTS_STATE::Capture( m_schematic, *m_parts );
    }
    catch( const std::exception& error )
    {
        wxLogTrace( traceSchTracking, wxS( "Unable to capture persisted parts for '%s': %s" ),
                    m_description, error.what() );
        return std::nullopt;
    }
}


bool SCH_TRACKED_CHANGE::replaced() const
{
    return m_schematic.ChangeJournal().Epoch() != m_start.epoch;
}


bool SCH_TRACKED_CHANGE::recordedSinceStart() const
{
    const DOCUMENT_CHANGE_JOURNAL& journal = m_schematic.ChangeJournal();
    return journal.Epoch() == m_start.epoch && journal.Sequence() != m_start.sequence;
}


bool SCH_TRACKED_CHANGE::changedSinceStart()
{
    // A commit inside the owner already recorded the action, so it changed and needs no
    // second comparison.
    if( recordedSinceStart() )
        return true;

    // Staged: compare only what the commit staged with the copies it saved, unless the owner
    // already reported a change it made outside the commit.
    if( m_commit )
    {
        if( m_changedOutsideCommit )
            return true;

        try
        {
            return m_commit->PersistsChange( m_schematic );
        }
        catch( const std::exception& error )
        {
            // An item that cannot be written is conservatively treated as changed.
            wxLogTrace( traceSchTracking, wxS( "Unable to compare the staged items of '%s': %s" ),
                        m_description, error.what() );
            return true;
        }
    }

    // Parts: compare only the named parts and the project settings.
    if( m_parts )
    {
        std::optional<SCH_PERSISTED_PARTS_STATE> afterParts = captureParts();

        // A part that cannot be written is conservatively treated as changed.
        if( !m_partsBefore || !afterParts )
            return true;

        if( *m_partsBefore == *afterParts )
            return false;

        for( const std::string& part : m_partsBefore->ChangedParts( *afterParts ) )
            wxLogTrace( traceSchTracking, wxS( "'%s' changed persisted part %s" ), m_description, part );

        return true;
    }

    std::optional<SCH_STATE_GROUPS> after = capture();

    // A state that cannot be written is conservatively treated as changed.
    if( !m_before || !after )
        return true;

    if( *m_before == *after )
        return false;

    for( const std::string& group : m_before->ChangedGroups( *after ) )
    {
        wxLogTrace( traceSchTracking, wxS( "'%s' changed persisted group %s" ), m_description,
                    group );
    }

    return true;
}


void SCH_TRACKED_CHANGE::recordIfUntracked( bool aChanged )
{
    const DOCUMENT_CHANGE_JOURNAL& journal = m_schematic.ChangeJournal();

    // A commit inside the owner already made older requests stale; do not add a second
    // revision for the same user action.
    if( aChanged && journal.Epoch() == m_start.epoch && journal.Sequence() == m_start.sequence )
    {
        m_schematic.RecordCommittedChange( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT, m_description );
    }
}


bool SCH_TRACKED_CHANGE::Complete()
{
    if( m_complete )
        return false;

    m_complete = true;

    // A replaced document starts a new journal epoch.  Its load is not an edit by this
    // owner, and the new document has nothing of this owner's to record or mark modified.
    if( replaced() )
    {
        wxLogTrace( traceSchTracking, wxS( "'%s' finished after the document was replaced; nothing recorded" ),
                    m_description );
        return false;
    }

    bool changed = changedSinceStart();
    recordIfUntracked( changed );
    return changed;
}


bool SCH_TRACKED_CHANGE::PushOrRevert( SCH_COMMIT& aCommit, const wxString& aMessage,
                                       int aCommitFlags )
{
    if( m_complete )
        return false;

    m_complete = true;

    if( m_commit && m_commit != &aCommit )
        throw std::invalid_argument( "A staged tracked change must finish the commit it compares" );

    if( replaced() )
    {
        // The replaced document freed the items this commit staged: neither pushing nor
        // reverting may touch them.  Drop the saved copies and record nothing.
        aCommit.Abandon();
        wxLogTrace( traceSchTracking, wxS( "'%s' abandoned its commit after the document was replaced" ),
                    m_description );
        return false;
    }

    bool changed = changedSinceStart();

    if( !aCommit.Empty() )
    {
        if( changed )
            aCommit.Push( aMessage, aCommitFlags );
        else
            aCommit.Revert();
    }

    recordIfUntracked( changed );
    return changed;
}
