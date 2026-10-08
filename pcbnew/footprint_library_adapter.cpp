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

#include <footprint_library_adapter.h>

#include <chrono>
#include <env_vars.h>
#include <footprint_info_impl.h>
#include <thread_pool.h>
#include <trace_helpers.h>
#include <footprint.h>
#include <api/library_lifecycle_snapshot.h>
#include <file_content_baseline.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcb_io/pcb_io_mgr.h>
#include <string_utils.h>
#include <wildcards_and_files_ext.h>

#include <magic_enum.hpp>
#include <wx/hash.h>
#include <wx/dir.h>
#include <wx/filename.h>
#include <wx/log.h>

#include <utility>

using namespace std::chrono_literals;


namespace
{
using SNAPSHOT_RESULT = tl::expected<LIBRARY_LIFECYCLE_SNAPSHOT, LIBRARY_LIFECYCLE_ERROR>;


SNAPSHOT_RESULT SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE aCode, std::string aMessage )
{
    return tl::unexpected( LIBRARY_LIFECYCLE_ERROR { .code = aCode, .message = std::move( aMessage ) } );
}


bool HasNoNul( const wxString& aValue )
{
    return aValue.Find( '\0' ) == wxNOT_FOUND;
}


wxString NormalizeDirectory( const wxString& aPath )
{
    if( aPath.empty() || !HasNoNul( aPath ) )
        return {};

    wxFileName path = wxFileName::DirName( aPath );

    if( !path.IsAbsolute() || !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) )
        return {};

    return path.GetPath();
}


bool IsAbsoluteFile( const wxString& aPath )
{
    if( aPath.empty() || !HasNoNul( aPath ) )
        return false;

    wxFileName path( aPath );
    return path.IsAbsolute() && path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
}


bool IsFootprintDirectoryUri( const wxString& aUri )
{
    if( aUri.empty() || !HasNoNul( aUri ) )
        return false;

    wxString normalized = aUri;
    while( normalized.Last() == '/' || normalized.Last() == '\\' )
        normalized.RemoveLast();

    return normalized.Lower().EndsWith( wxS( ".pretty" ) );
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


bool IsValidDocumentForProject( const kiapi::common::types::DocumentSpecifier& aDocument,
                                const PROJECT& aProject )
{
    using namespace kiapi::common::types;

    if( aDocument.type() != DOCTYPE_FOOTPRINT || !aDocument.has_project()
            || !aDocument.has_lib_id() || aDocument.project().name().empty()
            || aDocument.project().path().empty() || aDocument.lib_id().library_nickname().empty()
            || aDocument.lib_id().entry_name().empty()
            || aDocument.project().name().find( '\0' ) != std::string::npos
            || aDocument.project().path().find( '\0' ) != std::string::npos
            || aDocument.lib_id().library_nickname().find( '\0' ) != std::string::npos
            || aDocument.lib_id().entry_name().find( '\0' ) != std::string::npos )
    {
        return false;
    }

    return aDocument.project().name() == aProject.GetProjectName().ToStdString( wxConvUTF8 )
           && NormalizeDirectory( wxString::FromUTF8( aDocument.project().path() ) )
                      == NormalizeDirectory( aProject.GetProjectDirectory() );
}
} // namespace


LEAK_AT_EXIT<std::map<wxString, LIB_DATA>> FOOTPRINT_LIBRARY_ADAPTER::GlobalLibraries;

LEAK_AT_EXIT<std::shared_mutex> FOOTPRINT_LIBRARY_ADAPTER::GlobalLibraryMutex;

LEAK_AT_EXIT<std::map<wxString, std::vector<std::unique_ptr<FOOTPRINT>>>> FOOTPRINT_LIBRARY_ADAPTER::PreloadedFootprints;

std::shared_mutex FOOTPRINT_LIBRARY_ADAPTER::PreloadedFootprintsMutex;

LEAK_AT_EXIT<std::map<wxString, long long>> FOOTPRINT_LIBRARY_ADAPTER::PreloadedTimestamps;


FOOTPRINT_LIBRARY_ADAPTER::FOOTPRINT_LIBRARY_ADAPTER( LIBRARY_MANAGER& aManager ) :
        LIBRARY_MANAGER_ADAPTER( aManager )
{
}


FOOTPRINT_LIBRARY_ADAPTER::~FOOTPRINT_LIBRARY_ADAPTER()
{
    evictOwnedGlobalEntries();
}


wxString FOOTPRINT_LIBRARY_ADAPTER::GlobalPathEnvVariableName()
{
    return ENV_VAR::GetVersionedEnvVarName( wxS( "FOOTPRINT_DIR" ) );
}


void FOOTPRINT_LIBRARY_ADAPTER::enumerateLibrary( LIB_DATA* aLib, const wxString& aUri )
{
    wxArrayString namesAS;
    std::map<std::string, UTF8> options = aLib->row->GetOptionsMap();
    PCB_IO* plugin = pcbplugin( aLib );
    wxString nickname = aLib->row->Nickname();

    long long timestamp = plugin->GetLibraryTimestamp( aUri );

    // Hold across the enumerate-then-borrow sequence: GetEnumeratedFootprint returns borrowed
    // FP_CACHE pointers, so no other thread may rebuild the cache until we finish cloning.
    std::lock_guard pluginGuard( pluginMutex( nickname ) );

    // FootprintEnumerate populates the plugin's internal FP_CACHE with parsed footprints
    plugin->FootprintEnumerate( namesAS, aUri, false, &options );

    std::vector<std::unique_ptr<FOOTPRINT>> footprints;
    footprints.reserve( namesAS.size() );

    // For plugins with internal caches (like kicad_sexpr), GetEnumeratedFootprint returns
    // a borrowed pointer and ClearCachedFootprints handles cleanup. For other plugins,
    // GetEnumeratedFootprint allocates new memory that we must delete after cloning.
    const bool pluginCaches = plugin->CachesEnumeratedFootprints();

    for( const wxString& footprintName : namesAS )
    {
        try
        {
            const FOOTPRINT* cached = plugin->GetEnumeratedFootprint( aUri, footprintName, &options );

            if( !cached )
                continue;

            // Preserve disk UUIDs in the cache that serves keep-UUID loads
            FOOTPRINT* footprint = static_cast<FOOTPRINT*>( cached->Clone() );
            footprint->SetParent( nullptr );
            footprint->SetParentGroup( nullptr );

            // For non-caching plugins, delete the allocated footprint now that we've cloned it
            if( !pluginCaches )
                delete cached;

            LIB_ID id = footprint->GetFPID();
            id.SetLibNickname( nickname );
            footprint->SetFPID( id );
            footprints.emplace_back( footprint );
        }
        catch( IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "FP: %s:%s enumeration error: %s",
                        nickname, footprintName, e.What() );
        }
    }

    {
        std::unique_lock lock( PreloadedFootprintsMutex );
        PreloadedFootprints.Get()[nickname] = std::move( footprints );
        PreloadedTimestamps.Get()[nickname] = timestamp;
    }

    // Clear the plugin's FP_CACHE now that we've copied footprints to PreloadedFootprints.
    // This eliminates the double-caching that was consuming ~1.2GB of extra RAM.
    plugin->ClearCachedFootprints( aUri );
}


std::optional<LIB_STATUS> FOOTPRINT_LIBRARY_ADAPTER::LoadOne( LIB_DATA* aLib )
{
    aLib->status.load_status = LOAD_STATUS::LOADING;

    std::map<std::string, UTF8> options = aLib->row->GetOptionsMap();

    try
    {
        std::lock_guard pluginGuard( pluginMutex( aLib->row->Nickname() ) );

        wxArrayString dummyList;
        pcbplugin( aLib )->FootprintEnumerate( dummyList, getUri( aLib->row ), false, &options );
        aLib->status.load_status = LOAD_STATUS::LOADED;
    }
    catch( IO_ERROR& e )
    {
        aLib->status.load_status = LOAD_STATUS::LOAD_ERROR;
        aLib->status.error = LIBRARY_ERROR( e.Problem(), e.Where() );
        wxLogTrace( traceLibraries, "FP: %s: plugin threw exception: %s", aLib->row->Nickname(), e.What() );
    }

    return aLib->status;
}


std::optional<LIB_STATUS> FOOTPRINT_LIBRARY_ADAPTER::LoadOne( const wxString& nickname )
{
    LIBRARY_RESULT<LIB_DATA*> result = loadIfNeeded( nickname );

    if( result.has_value() )
        return LoadOne( *result );

    return LIB_STATUS{
        .load_status = LOAD_STATUS::LOAD_ERROR,
        .error = LIBRARY_ERROR( { result.error() } )
    };
}


std::vector<FOOTPRINT*> FOOTPRINT_LIBRARY_ADAPTER::GetFootprints( const wxString& aNickname, bool aBestEfforts )
{
    std::vector<FOOTPRINT*> footprints;

    std::shared_lock lock( PreloadedFootprintsMutex );
    auto it = PreloadedFootprints.Get().find( aNickname );

    if( it == PreloadedFootprints.Get().end() )
        return footprints;

    footprints.reserve( it->second.size() );

    for( const auto& fp : it->second )
        footprints.push_back( fp.get() );

    return footprints;
}



std::vector<wxString> FOOTPRINT_LIBRARY_ADAPTER::GetFootprintNames( const wxString& aNickname, bool aBestEfforts )
{
    // TODO(JE) can we kill wxArrayString in internal API?
    wxArrayString namesAS;
    std::vector<wxString> names;

    if( std::optional<const LIB_DATA*> maybeLib = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* lib = *maybeLib;
        std::map<std::string, UTF8> options = lib->row->GetOptionsMap();

        try
        {
            std::lock_guard pluginGuard( pluginMutex( aNickname ) );

            pcbplugin( lib )->FootprintEnumerate( namesAS, getUri( lib->row ), true, &options );
        }
        catch( IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "FP: Exception enumerating library %s: %s", lib->row->Nickname(), e.What() );
        }
    }

    for( const wxString& name : namesAS )
        names.emplace_back( name );

    return names;
}


long long FOOTPRINT_LIBRARY_ADAPTER::GenerateTimestamp( const wxString* aNickname )
{
    long long hash = 0;

    if( aNickname )
    {
        wxCHECK( HasLibrary( *aNickname, true ), hash );

        if( std::optional<const LIB_DATA*> r = fetchIfLoaded( *aNickname ); r.has_value() )
        {
            PCB_IO* plugin = dynamic_cast<PCB_IO*>( ( *r )->plugin.get() );
            wxCHECK( plugin, hash );
            return plugin->GetLibraryTimestamp( LIBRARY_MANAGER::GetFullURI( ( *r )->row, true ) )
                    + wxHashTable::MakeKey( *aNickname );
        }
    }

    for( const wxString& nickname : GetLibraryNames() )
    {
        if( std::optional<const LIB_DATA*> r = fetchIfLoaded( nickname ); r.has_value() )
        {
            wxCHECK2( ( *r )->plugin->IsPCB_IO(), continue );
            PCB_IO* plugin = static_cast<PCB_IO*>( ( *r )->plugin.get() );
            hash += plugin->GetLibraryTimestamp( LIBRARY_MANAGER::GetFullURI( ( *r )->row, true ) )
                        + wxHashTable::MakeKey( nickname );
        }
    }

    return hash;
}


void FOOTPRINT_LIBRARY_ADAPTER::RefreshLibraryIfChanged( const wxString& aNickname, bool aForceReload )
{
    std::optional<LIB_DATA*> maybeLib = fetchIfLoaded( aNickname );

    if( !maybeLib )
        return;

    LIB_DATA* lib = *maybeLib;
    PCB_IO*   plugin = dynamic_cast<PCB_IO*>( lib->plugin.get() );

    if( !plugin )
        return;

    wxString  uri = getUri( lib->row );
    long long currentTimestamp = plugin->GetLibraryTimestamp( uri );

    {
        std::shared_lock lock( PreloadedFootprintsMutex );
        auto             tsIt = PreloadedTimestamps.Get().find( aNickname );

        if( !aForceReload && tsIt != PreloadedTimestamps.Get().end() && tsIt->second == currentTimestamp )
            return;

        wxLogTrace( traceLibraries, "FP: %s changed on disk, re-enumerating", aNickname );
    }

    if( aForceReload )
    {
        std::lock_guard pluginGuard( pluginMutex( aNickname ) );
        plugin->ClearCachedFootprints( uri );
    }
    enumerateLibrary( lib, uri );
}


tl::expected<LIBRARY_LIFECYCLE_SNAPSHOT, LIBRARY_LIFECYCLE_ERROR>
FOOTPRINT_LIBRARY_ADAPTER::CaptureLibrarySnapshot(
        const wxString& aNickname, const wxString& aName,
        const kiapi::common::types::DocumentSpecifier& aDocument, const PROJECT& aProject,
        bool aAllowMissingEntry )
{
    using namespace kiapi::common::types;

    if( aNickname.empty() || aName.empty() || !HasNoNul( aNickname ) || !HasNoNul( aName )
            || !IsValidDocumentForProject( aDocument, aProject )
            || aDocument.lib_id().library_nickname() != aNickname.ToStdString( wxConvUTF8 )
            || aDocument.lib_id().entry_name() != aName.ToStdString( wxConvUTF8 ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_DOCUMENT,
                              "the footprint snapshot document is not a valid identity for this project" );
    }

    if( aProject.IsNullProject() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::PROJECT_MISMATCH,
                              "a footprint snapshot requires a non-null owning project" );
    }

    std::optional<LIBRARY_TABLE_ROW*> rowResult = GetRow( aNickname, LIBRARY_TABLE_SCOPE::BOTH );

    if( !rowResult || !( *rowResult ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the requested footprint nickname is absent from the library tables" );
    }

    const LIBRARY_TABLE_ROW* row = *rowResult;

    if( row->Scope() != LIBRARY_TABLE_SCOPE::GLOBAL && row->Scope() != LIBRARY_TABLE_SCOPE::PROJECT )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the footprint row does not identify one concrete library-table scope" );
    }

    std::optional<LIBRARY_TABLE*> tableResult = Manager().Table( Type(), row->Scope() );

    if( !tableResult || !( *tableResult ) || ( *tableResult )->Path().empty()
            || !( *tableResult )->IsOk() )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the footprint row has no readable owning library-table file" );
    }

    LIBRARY_TABLE* table = *tableResult;
    bool rowBelongsToRootTable = false;

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
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER,
                              "nested footprint-table rows are not covered by this snapshot owner" );
    }

    if( row->Disabled() || PCB_IO_MGR::EnumFromStr( row->Type() ) != PCB_IO_MGR::KICAD_SEXP )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER,
                              "only direct KiCad footprint .pretty providers are covered by this snapshot owner" );
    }

    const wxString expandedDirectory = LIBRARY_MANAGER::ExpandURI( row->URI(), aProject );

    if( !IsFootprintDirectoryUri( row->URI() ) || !IsAbsoluteFile( table->Path() )
            || !IsAbsoluteFile( expandedDirectory )
            || !wxFileName( expandedDirectory ).DirExists()
            || wxFileName( expandedDirectory ).GetExt().CmpNoCase( wxS( "pretty" ) ) != 0 )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "the footprint table or .pretty provider path is not an existing absolute directory" );
    }

    wxString fileName = aName;
    ReplaceIllegalFileNameChars( fileName, '_' );
    const wxString expandedEntry = wxFileName( expandedDirectory, fileName,
                                               FILEEXT::KiCadFootprintFileExtension ).GetFullPath();

    std::string tableContents;
    const FILE_CONTENT_BASELINE tableBefore = FILE_CONTENT_BASELINE::Read( table->Path(), &tableContents );
    std::string entryContents;
    const FILE_CONTENT_BASELINE entryBefore = FILE_CONTENT_BASELINE::Read( expandedEntry, &entryContents );

    if( !tableBefore.Known() || !tableBefore.Exists() || !entryBefore.Known()
            || ( !entryBefore.Exists() && !aAllowMissingEntry ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "the footprint table and exact .kicad_mod entry need complete readable baselines" );
    }

    LIBRARY_TABLE tableFromDisk( true, wxString::FromUTF8( tableContents ), row->Scope() );
    std::optional<LIBRARY_TABLE_ROW*> diskRow = tableFromDisk.Row( aNickname );

    if( !tableFromDisk.IsOk() || !diskRow || !( *diskRow ) || !SameTableRow( **diskRow, *row ) )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                              "the in-memory footprint row does not match the captured table file" );
    }

    try
    {
        std::optional<LIB_STATUS> loadedStatus = LoadOne( aNickname );

        if( !loadedStatus || loadedStatus->load_status != LOAD_STATUS::LOADED )
        {
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                                  "the footprint provider could not be loaded" );
        }

        // Force the provider to discard timestamp-based caches before reading the requested entry.
        RefreshLibraryIfChanged( aNickname, true );
        std::unique_ptr<FOOTPRINT> loaded( entryBefore.Exists()
                                                   ? LoadFootprint( aNickname, aName, true )
                                                   : nullptr );

        if( entryBefore.Exists() && !loaded )
        {
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                                  "the requested footprint was not present in the refreshed provider" );
        }

        // Model paths point to files outside the table and entry baselines.  Do not fold those
        // references into detachedDefinition and accidentally claim dependency coverage.
        if( loaded && !loaded->Models().empty() )
        {
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_DEPENDENCY,
                                  "footprint 3D model dependencies are outside the captured table and entry baseline scope" );
        }

        if( loaded )
        {
            loaded->SetParent( nullptr );
            loaded->SetParentGroup( nullptr );
            loaded->ClearAllNets();
            loaded->SetFPID( LIB_ID( aNickname, aName ) );
        }

        const wxString tempDirectory = wxFileName::GetTempDir() + wxFileName::GetPathSeparator()
                + wxS( "kicad-footprint-snapshot-" ) + wxString::FromUTF8( ::KIID().AsStdString() );

        if( !wxFileName::Mkdir( tempDirectory, 0700, wxPATH_MKDIR_FULL ) )
        {
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                                  "could not allocate a private native temporary footprint library" );
        }

        struct TEMP_DIRECTORY
        {
            wxString path;
            ~TEMP_DIRECTORY()
            {
                if( !path.empty() )
                    wxFileName::Rmdir( path, wxPATH_RMDIR_RECURSIVE );
            }
        } temporary{ tempDirectory };

        std::string detachedDefinition;
        if( loaded )
        {
            PCB_IO_KICAD_SEXPR writer;
            writer.FootprintSave( tempDirectory, loaded.get(), nullptr );

            wxString canonicalName = aName;
            ReplaceIllegalFileNameChars( canonicalName, '_' );
            const wxString canonicalPath = wxFileName( tempDirectory, canonicalName,
                                                       FILEEXT::KiCadFootprintFileExtension ).GetFullPath();
            const FILE_CONTENT_BASELINE canonicalBaseline =
                    FILE_CONTENT_BASELINE::Read( canonicalPath, &detachedDefinition );

            if( !canonicalBaseline.Known() || !canonicalBaseline.Exists() || detachedDefinition.empty()
                    || detachedDefinition.find( "(footprint" ) == std::string::npos )
            {
                return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                                      "the native footprint writer did not produce a complete detached definition" );
            }
        }

        const FILE_CONTENT_BASELINE tableAfter = FILE_CONTENT_BASELINE::Read( table->Path() );
        const FILE_CONTENT_BASELINE entryAfter = FILE_CONTENT_BASELINE::Read( expandedEntry );

        if( !SameBaseline( tableBefore, tableAfter ) || !SameBaseline( entryBefore, entryAfter ) )
        {
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                                  "the footprint table or entry changed while the snapshot was captured" );
        }

        LIBRARY_LIFECYCLE_SNAPSHOT snapshot;
        snapshot.document = aDocument;
        snapshot.tablePath = table->Path();
        snapshot.tableScope = row->Scope();
        snapshot.nickname = row->Nickname();
        snapshot.rawUri = row->URI();
        // The expanded URI is the exact sanitized entry file, not the .pretty directory.  This
        // makes the detached definition and its file baseline refer to one concrete source.
        snapshot.expandedUri = expandedEntry;
        snapshot.provider = "kicad-footprint-file";
        snapshot.providerOptions = row->Options().ToStdString( wxConvUTF8 );
        snapshot.detachedDefinition = std::move( detachedDefinition );
        snapshot.rawLibraryContents = std::move( entryContents );
        snapshot.entryExists = entryBefore.Exists();
        snapshot.dependencyBaselines = { tableAfter };
        wxArrayString siblingFiles;
        wxDir::GetAllFiles( expandedDirectory, &siblingFiles,
                            wxS( "*.kicad_mod" ), wxDIR_FILES );
        siblingFiles.Sort();
        for( const wxString& sibling : siblingFiles )
        {
            FILE_CONTENT_BASELINE baseline = FILE_CONTENT_BASELINE::Read( sibling );
            if( !baseline.Known() || !baseline.Exists() )
                return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                                      "every sibling footprint entry needs a readable content baseline" );
            snapshot.dependencyBaselines.push_back( std::move( baseline ) );
        }
        if( !entryBefore.Exists() )
            snapshot.dependencyBaselines.push_back( entryAfter );

        bool hasEntryBaseline = false;
        for( const FILE_CONTENT_BASELINE& baseline : snapshot.dependencyBaselines )
        {
            if( FILE_CONTENT_BASELINE::SamePath( baseline.Path(), expandedEntry ) )
            {
                hasEntryBaseline = true;
                if( !SameBaseline( entryBefore, baseline ) )
                    return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                                          "the exact footprint entry changed during sibling capture" );
            }
        }
        if( !hasEntryBaseline )
            return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                                  "the requested footprint entry has no dependency baseline" );
        snapshot.forcedRefresh = true;
        snapshot.providerObservable = true;
        snapshot.contentSnapshotComplete = true;
        snapshot.diskBaselineChecked = true;
        snapshot.mcpDirectFootprintFile = true;
        snapshot.completeChangeTracking = false;
        snapshot.postverify = false;
        return snapshot;
    }
    catch( const IO_ERROR& error )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              "the footprint provider threw while refreshing: " + error.What().ToStdString() );
    }
    catch( const std::exception& error )
    {
        return SnapshotError( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                              std::string( "the footprint provider failed while capturing: " ) + error.what() );
    }
}


void FOOTPRINT_LIBRARY_ADAPTER::RefreshChangedLibraries()
{
    for( const wxString& nickname : GetLibraryNames() )
    {
        // An unreadable library must not stop the others, nor escape into the caller.
        try
        {
            RefreshLibraryIfChanged( nickname );
        }
        catch( const IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "FP: %s: refresh failed: %s", nickname, e.What() );
        }
    }
}


bool FOOTPRINT_LIBRARY_ADAPTER::FootprintExists( const wxString& aNickname, const wxString& aName )
{
    if( std::optional<const LIB_DATA*> maybeLib = fetchIfLoaded( aNickname ) )
    {
        const LIB_DATA* lib = *maybeLib;
        std::map<std::string, UTF8> options = lib->row->GetOptionsMap();

        std::lock_guard pluginGuard( pluginMutex( aNickname ) );

        return pcbplugin( lib )->FootprintExists( getUri( lib->row ), aName, &options );
    }

    return false;
}


FOOTPRINT* FOOTPRINT_LIBRARY_ADAPTER::LoadFootprint( const wxString& aNickname, const wxString& aName, bool aKeepUUID )
{
    // First check if the footprint is in PreloadedFootprints and clone from there.
    // This avoids re-parsing the file and keeps FP_CACHE from being repopulated.
    {
        std::shared_lock lock( PreloadedFootprintsMutex );
        auto libIt = PreloadedFootprints.Get().find( aNickname );

        if( libIt != PreloadedFootprints.Get().end() )
        {
            for( const auto& fp : libIt->second )
            {
                if( fp->GetFPID().GetLibItemName() == UTF8( aName ) )
                {
                    FOOTPRINT* copy;

                    if( aKeepUUID )
                        copy = static_cast<FOOTPRINT*>( fp->Clone() );
                    else
                        copy = static_cast<FOOTPRINT*>( fp->Duplicate( IGNORE_PARENT_GROUP ) );

                    copy->SetParent( nullptr );
                    return copy;
                }
            }
        }
    }

    // Footprint not found in PreloadedFootprints, fall back to plugin.
    // This re-parses the file but is needed for footprints not yet enumerated.
    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aNickname ) )
    {
        try
        {
            std::lock_guard pluginGuard( pluginMutex( aNickname ) );

            if( FOOTPRINT* footprint = pcbplugin( *lib )->FootprintLoad( getUri( ( *lib )->row ), aName, aKeepUUID ) )
            {
                LIB_ID id = footprint->GetFPID();
                id.SetLibNickname( ( *lib )->row->Nickname() );
                footprint->SetFPID( id );
                return footprint;
            }
        }
        catch( const IO_ERROR& ioe )
        {
            wxLogTrace( traceLibraries, "LoadFootprint: error loading %s:%s: %s", aNickname, aName, ioe.What() );
        }
    }
    else
    {
        wxLogTrace( traceLibraries, "LoadFootprint: requested library %s not loaded", aNickname );
    }

    return nullptr;
}


FOOTPRINT* FOOTPRINT_LIBRARY_ADAPTER::LoadFootprintWithOptionalNickname( const LIB_ID& aFootprintId, bool aKeepUUID )
{
    wxString nickname = aFootprintId.GetLibNickname();
    wxString footprintName = aFootprintId.GetLibItemName();

    if( nickname.size() )
        return LoadFootprint( nickname, footprintName, aKeepUUID );

    // nickname is empty, sequentially search (alphabetically) all libs/nicks for first match:
    for( const wxString& library : GetLibraryNames() )
    {
        // FootprintLoad() returns NULL on not found, does not throw exception
        // unless there's an IO_ERROR.
        if( FOOTPRINT* ret = LoadFootprint( library, footprintName, aKeepUUID ) )
            return ret;
    }

    return nullptr;
}


FOOTPRINT_LIBRARY_ADAPTER::CHECKED_WRITE_RESULT FOOTPRINT_LIBRARY_ADAPTER::SaveFootprintChecked(
        const LIBRARY_LIFECYCLE_SNAPSHOT& aExpected, const PROJECT& aProject,
        const FOOTPRINT* aFootprint, bool aOverwrite )
{
    CHECKED_WRITE_RESULT result;
    auto reject = [&]( const char* aCode, const std::string& aMessage )
    {
        result.errorCode = aCode;
        result.errorMessage = aMessage;
        return result;
    };

    if( auto admitted = AdmitLibraryLifecycleMutation( aExpected, aProject.GetProjectName(),
                                                       aProject.GetProjectDirectory() ); !admitted )
        return reject( "library_mutation_unavailable", admitted.error().message );

    if( !aExpected.mcpDirectFootprintFile || !aExpected.providerObservable
            || !aExpected.contentSnapshotComplete || !aExpected.diskBaselineChecked
            || aExpected.dependencyBaselines.empty() || aExpected.document.type()
                    != kiapi::common::types::DOCTYPE_FOOTPRINT )
        return reject( "library_mutation_unavailable",
                       "The native footprint owner has no complete direct-file lifecycle checkpoint." );

    const wxString nickname = wxString::FromUTF8( aExpected.document.lib_id().library_nickname() );
    const wxString name = wxString::FromUTF8( aExpected.document.lib_id().entry_name() );
    if( nickname != aExpected.nickname || name.empty() || !aFootprint
            || aFootprint->GetFPID().GetUniStringLibItemName() != name )
        return reject( "invalid_footprint_definition",
                       "The checked footprint must identify the exact requested entry." );

    std::optional<LIBRARY_TABLE_ROW*> rowResult = GetRow( nickname, LIBRARY_TABLE_SCOPE::BOTH );
    if( !rowResult || !( *rowResult ) )
        return reject( "library_table_row_mismatch", "The requested footprint row is no longer present." );

    const LIBRARY_TABLE_ROW* row = *rowResult;
    if( row->Scope() != aExpected.tableScope || PCB_IO_MGR::EnumFromStr( row->Type() ) != PCB_IO_MGR::KICAD_SEXP
            || row->URI() != aExpected.rawUri || row->Options().ToStdString( wxConvUTF8 ) != aExpected.providerOptions
            || LIBRARY_MANAGER::ExpandURI( row->URI(), aProject )
                    != wxFileName( aExpected.expandedUri ).GetPath() )
        return reject( "stale_library_state", "The footprint library table row changed after the checked snapshot." );

    LIBRARY_RESULT<LIB_DATA*> loaded = loadIfNeeded( nickname );
    if( !loaded || !( *loaded ) )
        return reject( "library_provider_unknown", "The direct footprint provider is no longer loaded." );

    std::lock_guard pluginGuard( pluginMutex( nickname ) );
    PCB_IO_KICAD_SEXPR* directProvider = dynamic_cast<PCB_IO_KICAD_SEXPR*>( pcbplugin( *loaded ) );
    if( !directProvider )
        return reject( "library_provider_unsupported", "The checked lane cannot call this footprint provider." );

    PCB_KICAD_SEXPR_CHECKED_WRITE_RESULT native = directProvider->FootprintSaveChecked(
            aExpected.expandedUri, aFootprint, aOverwrite, aExpected.dependencyBaselines,
            aExpected.rawLibraryContents, name );
    result.writeStarted = native.writeStarted;
    result.committed = native.committed;
    result.postverified = native.postverified;
    result.targetVerified = native.targetVerified;
    result.unrelatedEntriesVerified = native.unrelatedEntriesVerified;
    result.rollbackAttempted = native.rollbackAttempted;
    result.rollbackVerified = native.rollbackVerified;
    result.afterBaselines = std::move( native.afterBaselines );
    result.rollbackBaselines = std::move( native.rollbackBaselines );
    result.beforeDefinition = std::move( native.beforeDefinition );
    result.afterDefinition = std::move( native.afterDefinition );
    result.rollbackDefinition = std::move( native.rollbackDefinition );
    result.errorCode = std::move( native.errorCode );
    result.errorMessage = std::move( native.errorMessage );
    return result;
}


FOOTPRINT_LIBRARY_ADAPTER::SAVE_T FOOTPRINT_LIBRARY_ADAPTER::SaveFootprint( const wxString& aNickname,
                                                                            const FOOTPRINT* aFootprint,
                                                                            bool aOverwrite )
{
    wxCHECK( aFootprint, SAVE_SKIPPED );

    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aNickname ) )
    {
        // Serialize the load-check / save / cache-update sequence against a concurrent rebuild.
        std::lock_guard pluginGuard( pluginMutex( aNickname ) );

        if( !aOverwrite )
        {
            wxString fpname = aFootprint->GetFPID().GetLibItemName();

            try
            {
                FOOTPRINT* existing = pcbplugin( *lib )->FootprintLoad( getUri( ( *lib )->row ), fpname, false );

                if( existing )
                {
                    delete existing;
                    return SAVE_SKIPPED;
                }
            }
            catch( IO_ERROR& e )
            {
                wxLogTrace( traceLibraries, "SaveFootprint: error checking for existing footprint %s: %s",
                            aFootprint->GetFPIDAsString(), e.What() );
                return SAVE_SKIPPED;
            }
        }

        try
        {
            pcbplugin( *lib )->FootprintSave( getUri( ( *lib )->row ), aFootprint );
        }
        catch( IO_ERROR& e )
        {
            // Re-throw rather than returning SAVE_SKIPPED; swallowing a write failure (read-only
            // file, full disk) would report a successful save while the file is unchanged.
            wxLogTrace( traceLibraries, "SaveFootprint: error saving %s: %s",
                        aFootprint->GetFPIDAsString(), e.What() );
            throw;
        }

        {
            std::unique_lock lock( PreloadedFootprintsMutex );
            auto             it = PreloadedFootprints.Get().find( aNickname );

            if( it != PreloadedFootprints.Get().end() )
            {
                wxString fpName = aFootprint->GetFPID().GetLibItemName();

                if( aOverwrite )
                {
                    auto& footprints = it->second;
                    footprints.erase( std::remove_if( footprints.begin(), footprints.end(),
                                                      [&fpName]( const std::unique_ptr<FOOTPRINT>& fp )
                                                      {
                                                          return fp->GetFPID().GetLibItemName().wx_str() == fpName;
                                                      } ),
                                      footprints.end() );
                }

                // Must match what FootprintSave() just wrote, UUIDs included
                FOOTPRINT* clone = static_cast<FOOTPRINT*>( aFootprint->Clone() );
                clone->SetParent( nullptr );
                clone->SetParentGroup( nullptr );

                LIB_ID id = clone->GetFPID();
                id.SetLibNickname( aNickname );
                clone->SetFPID( id );

                it->second.emplace_back( clone );
            }
        }

        return SAVE_OK;
    }
    else
    {
        wxLogTrace( traceLibraries, "SaveFootprint: requested library %s not loaded", aNickname );
        return SAVE_SKIPPED;
    }
}


void FOOTPRINT_LIBRARY_ADAPTER::DeleteFootprint( const wxString& aNickname, const wxString& aFootprintName )
{
    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aNickname ) )
    {
        std::lock_guard pluginGuard( pluginMutex( aNickname ) );

        try
        {
            pcbplugin( *lib )->FootprintDelete( getUri( ( *lib )->row ), aFootprintName );
        }
        catch( IO_ERROR& e )
        {
            wxLogTrace( traceLibraries, "DeleteFootprint: error deleting %s:%s: %s", aNickname,
                        aFootprintName, e.What() );
            throw;
        }

        {
            std::unique_lock lock( PreloadedFootprintsMutex );
            auto             it = PreloadedFootprints.Get().find( aNickname );

            if( it != PreloadedFootprints.Get().end() )
            {
                auto& footprints = it->second;
                footprints.erase( std::remove_if( footprints.begin(), footprints.end(),
                                                  [&aFootprintName]( const std::unique_ptr<FOOTPRINT>& fp )
                                                  {
                                                      return fp->GetFPID().GetLibItemName().wx_str() == aFootprintName;
                                                  } ),
                                  footprints.end() );
            }
        }
    }
    else
    {
        wxLogTrace( traceLibraries, "DeleteFootprint: requested library %s not loaded", aNickname );
    }
}


bool FOOTPRINT_LIBRARY_ADAPTER::IsFootprintLibWritable( const wxString& aLib )
{
    // Route through fetchIfLoaded() so LOAD_ERROR sentinel entries, which carry a null
    // plugin, are filtered out instead of dereferenced.
    if( std::optional<const LIB_DATA*> lib = fetchIfLoaded( aLib ) )
    {
        std::lock_guard pluginGuard( pluginMutex( aLib ) );

        return ( *lib )->plugin->IsLibraryWritable( getUri( ( *lib )->row ) );
    }

    return false;
}


LIBRARY_RESULT<IO_BASE*> FOOTPRINT_LIBRARY_ADAPTER::createPlugin( const LIBRARY_TABLE_ROW* row )
{
    PCB_IO_MGR::PCB_FILE_T type = PCB_IO_MGR::EnumFromStr( row->Type() );

    if( type == PCB_IO_MGR::NESTED_TABLE )
    {
        wxString   msg;
        wxFileName fileName( m_manager.GetFullURI( row, true ) );

        if( fileName.FileExists() )
            return tl::unexpected( LIBRARY_TABLE_OK() );
        else
            msg = wxString::Format( _( "Nested table '%s' not found." ), row->URI() );

        return tl::unexpected( LIBRARY_ERROR( msg ) );
    }
    else if( type == PCB_IO_MGR::PCB_FILE_UNKNOWN )
    {
        wxLogTrace( traceLibraries, "FP: Plugin type %s is unknown!", row->Type() );
        wxString msg = wxString::Format( _( "Unknown library type %s " ), row->Type() );
        return tl::unexpected( LIBRARY_ERROR( msg ) );
    }

    PCB_IO* plugin = PCB_IO_MGR::FindPlugin( type );
    wxCHECK( plugin, tl::unexpected( LIBRARY_ERROR( _( "Internal error" ) ) ) );

    wxLogTrace( traceLibraries, "FP: Library %s (%s) plugin created", row->Nickname(),
                magic_enum::enum_name( row->Scope() ) );

    return plugin;
}


PCB_IO* FOOTPRINT_LIBRARY_ADAPTER::pcbplugin( const LIB_DATA* aRow )
{
    // Note: can't use dynamic_cast across compile units on Mac
    wxCHECK( aRow->plugin->IsPCB_IO(), nullptr );
    PCB_IO* ret = static_cast<PCB_IO*>( aRow->plugin.get() );
    return ret;
}
