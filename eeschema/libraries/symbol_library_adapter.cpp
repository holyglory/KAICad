/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * @author Jon Evans <jon@craftyjon.com>
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

#include <magic_enum.hpp>
#include <chrono>
#include <memory>
#include <utility>

#include <common.h>
#include <dialog_shim.h>
#include <file_content_baseline.h>
#include <richio.h>
#include <ki_exception.h>
#include <wx/log.h>
#include <wx/filename.h>

#include <lib_symbol.h>
#include <libraries/symbol_library_adapter.h>

#include <env_vars.h>
#include <pgm_base.h>
#include <project.h>
#include <thread_pool.h>
#include <trace_helpers.h>
#include <settings/settings_manager.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_io/sch_io_mgr.h>

using namespace std::chrono_literals;


namespace
{
using SNAPSHOT_RESULT = tl::expected<LIBRARY_LIFECYCLE_SNAPSHOT, LIBRARY_LIFECYCLE_ERROR>;


SNAPSHOT_RESULT SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE aCode, std::string aMessage )
{
    return tl::unexpected( LIBRARY_LIFECYCLE_ERROR { .code = aCode, .message = std::move( aMessage ) } );
}


wxString NormalizeDirectory( const wxString& aPath )
{
    if( aPath.empty() )
        return {};

    wxFileName path = wxFileName::DirName( aPath );

    if( !path.IsAbsolute() || !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) )
        return {};

    return path.GetPath();
}


bool IsAbsoluteFile( const wxString& aPath )
{
    if( aPath.empty() )
        return false;

    wxFileName path( aPath );
    return path.IsAbsolute() && path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
}


bool SameBaseline( const FILE_CONTENT_BASELINE& aLeft, const FILE_CONTENT_BASELINE& aRight )
{
    return aLeft.Known() && aRight.Known() && aLeft.Exists() == aRight.Exists()
           && aLeft.Bytes() == aRight.Bytes() && aLeft.Sha256() == aRight.Sha256()
           && FILE_CONTENT_BASELINE::SamePath( aLeft.Path(), aRight.Path() );
}


bool SameTableRow( const LIBRARY_TABLE_ROW& aLeft, const LIBRARY_TABLE_ROW& aRight )
{
    return aLeft.Nickname() == aRight.Nickname() && aLeft.URI() == aRight.URI()
           && aLeft.Type() == aRight.Type() && aLeft.Options() == aRight.Options()
           && aLeft.Scope() == aRight.Scope() && aLeft.Disabled() == aRight.Disabled()
           && aLeft.Hidden() == aRight.Hidden();
}


bool HasNoNul( const wxString& aValue )
{
    return aValue.Find( '\0' ) == wxNOT_FOUND;
}


bool HasNoNul( const std::string& aValue )
{
    return aValue.find( '\0' ) == std::string::npos;
}


bool IsValidDocumentForProject( const kiapi::common::types::DocumentSpecifier& aDocument,
                                const PROJECT& aProject )
{
    using namespace kiapi::common::types;

    if( aDocument.type() != DOCTYPE_SYMBOL || !aDocument.has_project() || !aDocument.has_lib_id()
            || aDocument.project().name().empty() || aDocument.project().path().empty()
            || aDocument.lib_id().library_nickname().empty() || aDocument.lib_id().entry_name().empty()
            || !HasNoNul( aDocument.project().name() ) || !HasNoNul( aDocument.project().path() )
            || !HasNoNul( aDocument.lib_id().library_nickname() )
            || !HasNoNul( aDocument.lib_id().entry_name() ) )
    {
        return false;
    }

    const wxString documentProjectPath = NormalizeDirectory(
            wxString::FromUTF8( aDocument.project().path() ) );

    return aDocument.project().name() == aProject.GetProjectName().ToStdString( wxConvUTF8 )
           && !documentProjectPath.empty()
           && documentProjectPath == NormalizeDirectory( aProject.GetProjectDirectory() );
}
} // namespace


const char* SYMBOL_LIBRARY_ADAPTER::PropPowerSymsOnly = "pwr_sym_only";
const char* SYMBOL_LIBRARY_ADAPTER::PropNonPowerSymsOnly = "non_pwr_sym_only";

LEAK_AT_EXIT<std::map<wxString, LIB_DATA>> SYMBOL_LIBRARY_ADAPTER::GlobalLibraries;

LEAK_AT_EXIT<std::shared_mutex> SYMBOL_LIBRARY_ADAPTER::GlobalLibraryMutex;


SYMBOL_LIBRARY_ADAPTER::SYMBOL_LIBRARY_ADAPTER( LIBRARY_MANAGER& aManager ) :
            LIBRARY_MANAGER_ADAPTER( aManager )
{
}


SYMBOL_LIBRARY_ADAPTER::~SYMBOL_LIBRARY_ADAPTER()
{
    evictOwnedGlobalEntries();
}


wxString SYMBOL_LIBRARY_ADAPTER::GlobalPathEnvVariableName()
{
    return ENV_VAR::GetVersionedEnvVarName( wxS( "SYMBOL_DIR" ) );
}


SCH_IO* SYMBOL_LIBRARY_ADAPTER::schplugin( const LIB_DATA* aRow )
{
    SCH_IO* ret = dynamic_cast<SCH_IO*>( aRow->plugin.get() );
    wxCHECK( aRow->plugin && ret, nullptr );
    return ret;
}


void SYMBOL_LIBRARY_ADAPTER::enumerateLibrary( LIB_DATA* aLib, const wxString& aUri )
{
    wxArrayString dummyList;

    std::lock_guard pluginGuard( pluginMutex( aLib->row->Nickname() ) );

    std::map<std::string, UTF8> options = aLib->row->GetOptionsMap();
    schplugin( aLib )->EnumerateSymbolLib( dummyList, aUri, &options );
}


std::optional<LIB_STATUS> SYMBOL_LIBRARY_ADAPTER::LoadOne( LIB_DATA* aLib )
{
    aLib->status.load_status = LOAD_STATUS::LOADING;

    std::lock_guard pluginGuard( pluginMutex( aLib->row->Nickname() ) );

    std::map<std::string, UTF8> options = aLib->row->GetOptionsMap();

    try
    {
        wxArrayString dummyList;
        schplugin( aLib )->EnumerateSymbolLib( dummyList, getUri( aLib->row ), &options );
        wxLogTrace( traceLibraries, "Sym: %s: library enumerated %zu items", aLib->row->Nickname(), dummyList.size() );
        aLib->status.load_status = LOAD_STATUS::LOADED;
    }
    catch( IO_ERROR& e )
    {
        aLib->status.load_status = LOAD_STATUS::LOAD_ERROR;
        aLib->status.error = LIBRARY_ERROR( e.Problem(), e.Where() );
        wxLogTrace( traceLibraries, "Sym: %s: plugin threw exception: %s", aLib->row->Nickname(), e.What() );
    }

    return aLib->status;
}


std::optional<LIB_STATUS> SYMBOL_LIBRARY_ADAPTER::CheckLibrary( LIB_DATA* aLib )
{
    aLib->status.load_status = LOAD_STATUS::LOADING;

    std::lock_guard pluginGuard( pluginMutex( aLib->row->Nickname() ) );

    std::map<std::string, UTF8> options = aLib->row->GetOptionsMap();

    try
    {
        schplugin( aLib )->CheckLibrary( getUri( aLib->row ), &options );
        aLib->status.load_status = LOAD_STATUS::LOADED;
    }
    catch( IO_ERROR& e )
    {
        aLib->status.load_status = LOAD_STATUS::LOAD_ERROR;
        aLib->status.error = LIBRARY_ERROR( e.Problem(), e.Where() );
        wxLogTrace( traceLibraries, "Sym: %s: library check failed: %s", aLib->row->Nickname(), e.What() );
    }

    return aLib->status;
}


std::optional<LIB_STATUS> SYMBOL_LIBRARY_ADAPTER::LoadOne( const wxString& nickname )
{
    LIBRARY_RESULT<LIB_DATA*> result = loadIfNeeded( nickname );

    if( result.has_value() )
        return LoadOne( *result );

    return LIB_STATUS { .load_status = LOAD_STATUS::LOAD_ERROR,
                        .error = LIBRARY_ERROR( { result.error() } ) };
}


LIBRARY_RESULT<IO_BASE*> SYMBOL_LIBRARY_ADAPTER::createPlugin( const LIBRARY_TABLE_ROW* row )
{
    SCH_IO_MGR::SCH_FILE_T type = SCH_IO_MGR::EnumFromStr( row->Type() );

    if( type == SCH_IO_MGR::SCH_NESTED_TABLE )
    {
        wxString   msg;
        wxFileName fileName( m_manager.GetFullURI( row, true ) );

        if( fileName.FileExists() )
            return tl::unexpected( LIBRARY_TABLE_OK() );
        else
            msg = wxString::Format( _( "Nested table '%s' not found." ), row->URI() );

        return tl::unexpected( LIBRARY_ERROR( msg ) );
    }
    else if( type == SCH_IO_MGR::SCH_FILE_UNKNOWN )
    {
        wxLogTrace( traceLibraries, "Sym: Plugin type %s is unknown!", row->Type() );
        wxString msg = wxString::Format( _( "Unknown library type %s " ), row->Type() );
        return tl::unexpected( LIBRARY_ERROR( msg ) );
    }

    SCH_IO* plugin = SCH_IO_MGR::FindPlugin( type );
    wxCHECK( plugin, tl::unexpected( LIBRARY_ERROR( _( "Internal error" ) ) ) );

    plugin->SetLibraryManagerAdapter( this );

    wxLogTrace( traceLibraries, "Sym: Library %s (%s) plugin created", row->Nickname(),
                magic_enum::enum_name( row->Scope() ) );

    return plugin;
}


std::vector<LIB_SYMBOL*> SYMBOL_LIBRARY_ADAPTER::GetSymbols( const wxString& aNickname, SYMBOL_TYPE aType )
{
    std::vector<LIB_SYMBOL*> symbols;

    std::optional<const LIB_DATA*> maybeLib = fetchIfLoaded( aNickname );

    if( !maybeLib )
        return symbols;

    const LIB_DATA* lib = *maybeLib;

    std::lock_guard pluginGuard( pluginMutex( aNickname ) );

    std::map<std::string, UTF8> options = lib->row->GetOptionsMap();

    if( aType == SYMBOL_TYPE::POWER_ONLY )
        options[PropPowerSymsOnly] = "";

    try
    {
        schplugin( lib )->EnumerateSymbolLib( symbols, getUri( lib->row ), &options );
    }
    catch( IO_ERROR& e )
    {
        wxLogTrace( traceLibraries, "Sym: Exception enumerating library %s: %s", lib->row->Nickname(), e.What() );
    }

    for( LIB_SYMBOL* symbol : symbols )
    {
        LIB_ID id = symbol->GetLibId();
        id.SetLibNickname( lib->row->Nickname() );
        symbol->SetLibId( id );
    }

    return symbols;
}


std::vector<wxString> SYMBOL_LIBRARY_ADAPTER::GetSymbolNames( const wxString& aNickname, SYMBOL_TYPE aType )
{
    wxArrayString namesAS;
    std::vector<wxString> names;

    if( std::optional<const LIB_DATA*> maybeLib = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* lib = *maybeLib;

        std::lock_guard pluginGuard( pluginMutex( aNickname ) );

        std::map<std::string, UTF8> options = lib->row->GetOptionsMap();

        if( aType == SYMBOL_TYPE::POWER_ONLY )
            options[PropPowerSymsOnly] = "";

        try
        {
            schplugin( lib )->EnumerateSymbolLib( namesAS, getUri( lib->row ), &options );
        }
        catch( const IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "Sym: Exception enumerating library %s: %s",
                        lib->row->Nickname(), e.What() );
        }
    }

    for( const wxString& name : namesAS )
        names.emplace_back( name );

    return names;
}


LIB_SYMBOL* SYMBOL_LIBRARY_ADAPTER::LoadSymbol( const wxString& aNickname, const wxString& aName )
{
    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aNickname ) )
    {
        std::lock_guard pluginGuard( pluginMutex( aNickname ) );

        if( LIB_SYMBOL* symbol = schplugin( *lib )->LoadSymbol( getUri( ( *lib )->row ), aName ) )
        {
            LIB_ID id = symbol->GetLibId();
            id.SetLibNickname( ( *lib )->row->Nickname() );
            symbol->SetLibId( id );
            return symbol;
        }
    }
    else
    {
        wxLogTrace( traceLibraries, "LoadSymbol: requested library %s not loaded", aNickname );
    }

    return nullptr;
}


tl::expected<LIBRARY_LIFECYCLE_SNAPSHOT, LIBRARY_LIFECYCLE_ERROR>
SYMBOL_LIBRARY_ADAPTER::CaptureLibrarySnapshot(
        const wxString& aNickname, const wxString& aName,
        const kiapi::common::types::DocumentSpecifier& aDocument, const PROJECT& aProject )
{
    using namespace kiapi::common::types;

    // Validate all request/project identity before resolving a nickname or expanding a URI.  URI
    // expansion consults project state, so doing this first prevents a foreign document from
    // selecting a local library by accident.
    if( aNickname.empty() || aName.empty() || !HasNoNul( aNickname ) || !HasNoNul( aName )
            || !IsValidDocumentForProject( aDocument, aProject )
            || aDocument.lib_id().library_nickname() != aNickname.ToStdString( wxConvUTF8 )
            || aDocument.lib_id().entry_name() != aName.ToStdString( wxConvUTF8 ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_DOCUMENT,
                              "the symbol snapshot document is not a valid identity for this project" );
    }

    if( aProject.IsNullProject() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::PROJECT_MISMATCH,
                              "a symbol snapshot requires a non-null owning project" );
    }

    std::optional<LIBRARY_TABLE_ROW*> rowResult = Manager().GetRow( Type(), aNickname,
                                                                      LIBRARY_TABLE_SCOPE::BOTH );

    if( !rowResult || !( *rowResult ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the requested symbol nickname is absent from the library tables" );
    }

    const LIBRARY_TABLE_ROW* row = *rowResult;

    if( row->Scope() != LIBRARY_TABLE_SCOPE::GLOBAL && row->Scope() != LIBRARY_TABLE_SCOPE::PROJECT )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the symbol row does not identify one concrete library-table scope" );
    }

    // The manager flattens nested tables when returning Rows().  Until a table-chain identity is
    // available, refuse those rows rather than claiming the root table file is the exact source.
    std::optional<LIBRARY_TABLE*> tableResult = Manager().Table( Type(), row->Scope() );

    if( !tableResult || !( *tableResult ) || ( *tableResult )->Path().empty()
            || !( *tableResult )->IsOk() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the symbol row has no readable owning library-table file" );
    }

    LIBRARY_TABLE* table = *tableResult;
    bool           rowBelongsToRootTable = false;

    for( const LIBRARY_TABLE_ROW& candidate : table->Rows() )
    {
        if( &candidate == row )
        {
            rowBelongsToRootTable = true;
            break;
        }
    }

    if( !rowBelongsToRootTable )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "nested symbol-table rows are not covered by this snapshot owner" );
    }

    if( SCH_IO_MGR::EnumFromStr( row->Type() ) != SCH_IO_MGR::SCH_KICAD )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER,
                              "only direct KiCad symbol-file providers are covered by this snapshot owner" );
    }

    const wxString expandedUri = LIBRARY_MANAGER::ExpandURI( row->URI(), aProject );

    if( !IsAbsoluteFile( table->Path() ) || !IsAbsoluteFile( expandedUri )
            || !wxFileName( expandedUri ).FileExists() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "the symbol table or library path is not an existing absolute file" );
    }

    std::string tableContents;
    const FILE_CONTENT_BASELINE tableBefore = FILE_CONTENT_BASELINE::Read( table->Path(), &tableContents );
    const FILE_CONTENT_BASELINE libraryBefore = FILE_CONTENT_BASELINE::Read( expandedUri );

    if( !tableBefore.Known() || !tableBefore.Exists() || !libraryBefore.Known() || !libraryBefore.Exists() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "the symbol table and library need complete readable pre-load baselines" );
    }

    // Parse the table bytes captured above and compare the row fields.  This ties the in-memory
    // row to the exact table file that will be retained in the snapshot, rather than relying on a
    // path or nickname alone.
    LIBRARY_TABLE tableFromDisk( true, wxString::FromUTF8( tableContents ), row->Scope() );
    std::optional<LIBRARY_TABLE_ROW*> diskRow = tableFromDisk.Row( aNickname );

    if( !tableFromDisk.IsOk() || !diskRow || !( *diskRow ) || !SameTableRow( **diskRow, *row ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the in-memory symbol row does not match the captured table file" );
    }

    // LoadOne performs the normal adapter resolution and locking.  A fresh KiCad provider below
    // then forces a disk read, avoiding a cache keyed only by a modification timestamp.  The
    // adapter's per-library lock and the selected global/project cache lock remain held through
    // that read, detached copy and serialization.
    std::optional<LIB_STATUS> loadedStatus;

    try
    {
        loadedStatus = LoadOne( aNickname );
    }
    catch( const IO_ERROR& e )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the symbol provider threw while refreshing: " + e.What().ToStdString() );
    }

    if( !loadedStatus || loadedStatus->load_status != LOAD_STATUS::LOADED )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the symbol provider could not be refreshed to a loaded state" );
    }

    std::shared_lock<std::shared_mutex> tableLock(
            row->Scope() == LIBRARY_TABLE_SCOPE::GLOBAL ? globalLibsMutex() : m_librariesMutex );
    std::lock_guard pluginGuard( pluginMutex( aNickname ) );

    // LoadOne above forces the adapter's provider refresh. Reuse the adapter's detached load
    // path here rather than constructing a second plugin instance: this keeps provider ownership
    // and cache synchronization in one native owner while the plugin mutex is held.
    std::unique_ptr<LIB_SYMBOL> loadedSymbol;

    try
    {
        std::optional<const LIB_DATA*> loaded = fetchIfLoaded( aNickname );
        if( !loaded || !( *loaded ) || !schplugin( *loaded ) )
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                                  "the refreshed symbol provider is not available" );

        std::map<std::string, UTF8> options = row->GetOptionsMap();
        loadedSymbol.reset( schplugin( *loaded )->LoadSymbol( getUri( ( *loaded )->row ), aName,
                                                               &options ) );
    }
    catch( const IO_ERROR& e )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the symbol provider threw while reading: " + e.What().ToStdString() );
    }

    if( !loadedSymbol )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the requested symbol was not present in the refreshed library" );
    }

    // A derived symbol is only a partial definition: its extends parent is another provider
    // entry, and this snapshot has no field for that parent identity or its dependency baseline.
    // Refuse it instead of silently serializing an incomplete detached definition.
    if( loadedSymbol->IsDerived() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "derived symbols require a parent definition and dependency baseline" );
    }

    LIB_ID detachedId = loadedSymbol->GetLibId();
    detachedId.SetLibNickname( row->Nickname() );
    loadedSymbol->SetLibId( detachedId );

    std::unique_ptr<LIB_SYMBOL> detachedSymbol = std::make_unique<LIB_SYMBOL>( *loadedSymbol );
    STRING_FORMATTER         formatter;
    SCH_IO_KICAD_SEXPR::FormatLibSymbol( detachedSymbol.get(), formatter );
    const std::string detachedDefinition = formatter.GetString();

    if( detachedDefinition.empty() || detachedDefinition.find( "(symbol" ) == std::string::npos )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the provider did not produce a complete detached symbol definition" );
    }

    const FILE_CONTENT_BASELINE tableAfter = FILE_CONTENT_BASELINE::Read( table->Path() );
    const FILE_CONTENT_BASELINE libraryAfter = FILE_CONTENT_BASELINE::Read( expandedUri );

    if( !SameBaseline( tableBefore, tableAfter ) || !SameBaseline( libraryBefore, libraryAfter ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "a table or library file changed while the symbol snapshot was captured" );
    }

    LIBRARY_LIFECYCLE_SNAPSHOT snapshot;
    snapshot.document = aDocument;
    snapshot.tablePath = table->Path();
    snapshot.tableScope = row->Scope();
    snapshot.nickname = row->Nickname();
    snapshot.rawUri = row->URI();
    snapshot.expandedUri = expandedUri;
    snapshot.provider = row->Type().ToStdString( wxConvUTF8 );
    snapshot.providerOptions = row->Options().ToStdString( wxConvUTF8 );
    snapshot.detachedDefinition = detachedDefinition;
    snapshot.dependencyBaselines = { tableAfter, libraryAfter };
    snapshot.forcedRefresh = true;
    snapshot.providerObservable = true;
    snapshot.contentSnapshotComplete = true;
    snapshot.diskBaselineChecked = true;
    snapshot.completeChangeTracking = false;
    snapshot.postverify = false;
    return snapshot;
}


SYMBOL_LIBRARY_ADAPTER::SAVE_T SYMBOL_LIBRARY_ADAPTER::SaveSymbol( const wxString& aNickname,
                                                                   const LIB_SYMBOL* aSymbol, bool aOverwrite )
{
    wxCHECK( aSymbol, SAVE_SKIPPED );

    LIBRARY_RESULT<LIB_DATA*> libResult = loadIfNeeded( aNickname );

    if( !libResult.has_value() )
    {
        wxLogTrace( traceLibraries, "SaveSymbol: unable to load library %s: %s", aNickname,
                    libResult.error().message );
        return SAVE_SKIPPED;
    }

    LIB_DATA* lib = *libResult;

    if( !lib )
    {
        wxLogTrace( traceLibraries, "SaveSymbol: library %s not found", aNickname );
        return SAVE_SKIPPED;
    }

    SCH_IO* plugin = schplugin( lib );
    wxCHECK( plugin, SAVE_SKIPPED );

    std::map<std::string, UTF8> options = lib->row->GetOptionsMap();

    if( !aOverwrite )
    {
        try
        {
            std::unique_ptr<LIB_SYMBOL> existing( plugin->LoadSymbol( getUri( lib->row ), aSymbol->GetName(),
                                                                      &options ) );

            if( existing )
                return SAVE_SKIPPED;
        }
        catch( const IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "SaveSymbol: error checking for existing symbol %s:%s: %s", aNickname,
                        aSymbol->GetName(), e.What() );
            return SAVE_SKIPPED;
        }
    }

    try
    {
        plugin->SaveSymbol( getUri( lib->row ), aSymbol, &options );
    }
    catch( const IO_ERROR& e )
    {
        wxLogTrace( traceLibraries, "SaveSymbol: error saving %s:%s: %s", aNickname, aSymbol->GetName(), e.What() );
        return SAVE_SKIPPED;
    }

    return SAVE_OK;
}


void SYMBOL_LIBRARY_ADAPTER::DeleteSymbol( const wxString& aNickname, const wxString& aSymbolName )
{
    LIBRARY_RESULT<LIB_DATA*> libResult = loadIfNeeded( aNickname );

    if( !libResult.has_value() || !*libResult )
        THROW_IO_ERROR( wxString::Format( wxS( "Unable to load symbol library '%s'" ), aNickname ) );

    LIB_DATA* lib = *libResult;
    SCH_IO* plugin = schplugin( lib );

    if( !plugin )
        THROW_IO_ERROR( wxString::Format( wxS( "Symbol library '%s' has no schematic plugin" ), aNickname ) );

    std::map<std::string, UTF8> options = lib->row->GetOptionsMap();
    plugin->DeleteSymbol( getUri( lib->row ), aSymbolName, &options );
}


bool SYMBOL_LIBRARY_ADAPTER::IsSymbolLibWritable( const wxString& aLib )
{
    // Route through fetchIfLoaded() so LOAD_ERROR sentinel entries, which carry a null
    // plugin, are filtered out instead of dereferenced.
    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aLib ) )
        return ( *lib )->plugin->IsLibraryWritable( getUri( ( *lib )->row ) );

    return false;
}


std::vector<wxString> SYMBOL_LIBRARY_ADAPTER::GetAvailableExtraFields( const wxString& aNickname )
{
    std::vector<wxString> fields;

    if( std::optional<LIB_DATA*> result = fetchIfLoaded( aNickname ) )
    {
        LIB_DATA* rowData = *result;

        int hash = schplugin( rowData )->GetModifyHash();

        if( hash != rowData->modify_hash )
        {
            rowData->modify_hash = hash;
            schplugin( rowData )->GetAvailableSymbolFields( rowData->available_fields_cache );
        }

        return rowData->available_fields_cache;
    }

    return fields;
}


bool SYMBOL_LIBRARY_ADAPTER::SupportsSubLibraries( const wxString& aNickname ) const
{
    if( std::optional<const LIB_DATA*> result = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* rowData = *result;
        return schplugin( rowData )->SupportsSubLibraries();
    }

    return false;
}


std::vector<SUB_LIBRARY> SYMBOL_LIBRARY_ADAPTER::GetSubLibraries( const wxString& aNickname ) const
{
    std::vector<SUB_LIBRARY> ret;

    if( std::optional<const LIB_DATA*> result = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* rowData = *result;

        try
        {
            std::vector<wxString> names;
            schplugin( rowData )->GetSubLibraryNames( names );

            for( const wxString& name : names )
            {
                ret.emplace_back( SUB_LIBRARY {
                        .nickname = name,
                        .description = schplugin( rowData )->GetSubLibraryDescription( name ) } );
            }
        }
        catch( const IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "Sym: Exception getting sub-libraries for %s: %s",
                        aNickname, e.What() );
        }
    }

    return ret;
}


bool SYMBOL_LIBRARY_ADAPTER::SupportsConfigurationDialog( const wxString& aNickname ) const
{
    if( std::optional<const LIB_DATA*> result = fetchIfLoaded( aNickname ) )
        return ( *result )->plugin->SupportsConfigurationDialog();

    return false;
}


int SYMBOL_LIBRARY_ADAPTER::ShowConfigurationDialog( const wxString& aNickname, wxWindow* aParent ) const
{
    std::optional<const LIB_DATA*> optRow = fetchIfLoaded( aNickname );

    if( !optRow )
        return wxID_CANCEL;

    if( !( *optRow )->plugin->SupportsConfigurationDialog() )
        return wxID_CANCEL;

    DIALOG_SHIM* dialog = ( *optRow )->plugin->CreateConfigurationDialog( aParent );

    return dialog->ShowModal();
}


int SYMBOL_LIBRARY_ADAPTER::GetModifyHash() const
{
    int hash = 0;

    for( const LIBRARY_TABLE_ROW* row : m_manager.Rows( Type() ) )
    {
        if( std::optional<const LIB_DATA*> result = fetchIfLoaded( row->Nickname() ) )
        {
            const LIB_DATA* rowData = *result;
            wxCHECK2( rowData->row, continue );
            hash += schplugin( rowData )->GetModifyHash();
        }
    }

    return hash;
}


std::optional<int> SYMBOL_LIBRARY_ADAPTER::GetLibraryModifyHash( const wxString& aNickname ) const
{
    if( std::optional<const LIB_DATA*> result = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* rowData = *result;
        wxCHECK( rowData->row, std::nullopt );
        return schplugin( rowData )->GetModifyHash();
    }

    return std::nullopt;
}
