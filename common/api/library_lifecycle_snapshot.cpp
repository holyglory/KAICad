/* Preliminary read-only library lifecycle binding. GPL-3.0-or-later. */
#include <api/library_lifecycle_snapshot.h>

#include <wx/filename.h>

#include <algorithm>
#include <utility>


namespace
{
wxString NormalizeDirectory( const wxString& aPath )
{
    if( aPath.empty() || aPath.Find( '\0' ) != wxNOT_FOUND )
        return {};

    wxFileName path = wxFileName::DirName( aPath );

    if( !path.IsAbsolute() || !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) )
        return {};

    const wxString normalized = path.GetPath();
    return normalized.empty() ? wxString{} : normalized;
}


bool IsAbsoluteFile( const wxString& aPath )
{
    if( aPath.empty() || aPath.Find( '\0' ) != wxNOT_FOUND )
        return false;

    wxFileName path( aPath );
    return path.IsAbsolute() && path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
}


bool IsSymbolFile( const wxString& aPath )
{
    if( !IsAbsoluteFile( aPath ) || aPath.Last() == '/' || aPath.Last() == '\\' )
        return false;

    wxFileName path( aPath );

    if( !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) )
        return false;

    if( path.GetExt().CmpNoCase( wxS( "kicad_sym" ) ) != 0 )
        return false;

    // A directory ending in .kicad_sym would be a folder/nested provider, not the direct
    // symbol-file provider covered by this slice.  The producer still owns filesystem IO; this
    // check only rejects an explicitly unsupported path shape.
    for( const wxString& directory : path.GetDirs() )
    {
        if( directory.Lower().EndsWith( wxS( ".kicad_sym" ) ) )
            return false;
    }

    return true;
}


bool IsFootprintFile( const wxString& aPath )
{
    if( !IsAbsoluteFile( aPath ) || aPath.Last() == '/' || aPath.Last() == '\\' )
        return false;

    wxFileName path( aPath );

    if( !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE )
            || path.GetExt().CmpNoCase( wxS( "kicad_mod" ) ) != 0 )
    {
        return false;
    }

    // A .kicad_mod parent denotes a nested/derived provider path, not a direct
    // entry in the .pretty directory covered by this slice.
    for( const wxString& directory : path.GetDirs() )
    {
        if( directory.Lower().EndsWith( wxS( ".kicad_mod" ) ) )
            return false;
    }

    return true;
}


bool IsFootprintDirectoryUri( const wxString& aUri )
{
    if( aUri.empty() || aUri.Find( '\0' ) != wxNOT_FOUND )
        return false;

    wxString normalized = aUri;
    while( normalized.Last() == '/' || normalized.Last() == '\\' )
        normalized.RemoveLast();

    return normalized.Lower().EndsWith( wxS( ".pretty" ) );
}


bool IsSha256( const std::string& aDigest )
{
    return aDigest.size() == 64
           && std::all_of( aDigest.begin(), aDigest.end(), []( char aCharacter )
                           {
                               return ( aCharacter >= '0' && aCharacter <= '9' )
                                      || ( aCharacter >= 'a' && aCharacter <= 'f' );
                           } );
}


LIBRARY_LIFECYCLE_RESULT Error( LIBRARY_LIFECYCLE_ERROR_CODE aCode, std::string aMessage )
{
    return tl::unexpected( LIBRARY_LIFECYCLE_ERROR { .code = aCode, .message = std::move( aMessage ) } );
}
}


LIBRARY_LIFECYCLE_RESULT ValidateLibraryLifecycleSnapshot(
        const LIBRARY_LIFECYCLE_SNAPSHOT& aSnapshot, const wxString& aExpectedProjectName,
        const wxString& aExpectedProjectPath )
{
    using namespace kiapi::common::types;

    const DocumentSpecifier& document = aSnapshot.document;

    const bool isSymbol = document.type() == DOCTYPE_SYMBOL;
    const bool isFootprint = document.type() == DOCTYPE_FOOTPRINT;

    if( ( !isSymbol && !isFootprint ) || !document.has_project() || !document.has_lib_id()
            || document.project().name().empty() || document.project().path().empty()
            || document.lib_id().library_nickname().empty() || document.lib_id().entry_name().empty()
            || document.project().name().find( '\0' ) != std::string::npos
            || document.project().path().find( '\0' ) != std::string::npos
            || document.lib_id().library_nickname().find( '\0' ) != std::string::npos
            || document.lib_id().entry_name().find( '\0' ) != std::string::npos )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_DOCUMENT,
                      "a library snapshot requires a project-bound symbol or footprint DocumentSpecifier" );
    }

    const wxString normalizedProjectPath =
            NormalizeDirectory( wxString::FromUTF8( document.project().path() ) );
    const wxString normalizedExpectedProjectPath = NormalizeDirectory( aExpectedProjectPath );

    if( aExpectedProjectName.empty() || aExpectedProjectName.Find( '\0' ) != wxNOT_FOUND
            || normalizedProjectPath.empty() || normalizedExpectedProjectPath.empty()
            || document.project().name() != aExpectedProjectName.ToStdString( wxConvUTF8 )
            || normalizedProjectPath != normalizedExpectedProjectPath )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::PROJECT_MISMATCH,
                      "the requested library project is not the project served by this native owner" );
    }

    if( aSnapshot.nickname.empty() || aSnapshot.nickname.Find( '\0' ) != wxNOT_FOUND
            || aSnapshot.nickname.ToStdString( wxConvUTF8 ) != document.lib_id().library_nickname()
            || ( aSnapshot.tableScope != LIBRARY_TABLE_SCOPE::GLOBAL
                 && aSnapshot.tableScope != LIBRARY_TABLE_SCOPE::PROJECT )
            || !IsAbsoluteFile( aSnapshot.tablePath ) || aSnapshot.rawUri.empty()
            || aSnapshot.rawUri.Find( '\0' ) != wxNOT_FOUND
            || ( isSymbol && !IsSymbolFile( aSnapshot.expandedUri ) )
            || ( isFootprint && ( !IsFootprintDirectoryUri( aSnapshot.rawUri )
                                  || !IsFootprintFile( aSnapshot.expandedUri ) ) ) )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                      "the library table row is missing its exact scope, path or URI identity" );
    }

    if( aSnapshot.provider.empty() )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::UNKNOWN_PROVIDER,
                      "the library provider was not identified" );
    }

    // This foundation slice can observe only direct KiCad symbol files and direct footprint
    // entries in .pretty directories. Folder, database, HTTP and nested providers are deliberately
    // refused until their complete file sets and mutation owners share this lifecycle contract.
    const bool providerSupported = aSnapshot.provider == "KiCad"
                                   || ( isSymbol && aSnapshot.provider == "kicad-symbol-file" )
                                   || ( isFootprint && aSnapshot.provider == "kicad-footprint-file" );

    if( !providerSupported )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER,
                      "the library provider is not covered by the read-only lifecycle slice" );
    }

    if( !aSnapshot.forcedRefresh || !aSnapshot.providerObservable || !aSnapshot.contentSnapshotComplete
            || !aSnapshot.diskBaselineChecked
            || ( aSnapshot.detachedDefinition.empty()
                 && !( isSymbol && aSnapshot.mcpDirectSymbolFile && !aSnapshot.entryExists
                        && !aSnapshot.rawLibraryContents.empty() )
                 && !( isFootprint && aSnapshot.mcpDirectFootprintFile && !aSnapshot.entryExists ) )
            || aSnapshot.dependencyBaselines.empty() )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                      "a safe library snapshot requires a forced refresh, detached definition, "
                      "observable provider and complete content/dependency evidence" );
    }

    std::vector<wxString> paths;
    bool hasTableBaseline = false;
    bool hasLibraryBaseline = false;

    for( const FILE_CONTENT_BASELINE& baseline : aSnapshot.dependencyBaselines )
    {
        if( !baseline.Known() || !IsAbsoluteFile( baseline.Path() ) )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "every library dependency needs one complete, unique absolute file baseline" );
        }

        for( const wxString& path : paths )
        {
            if( FILE_CONTENT_BASELINE::SamePath( path, baseline.Path() ) )
            {
                return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "every library dependency needs one complete, unique absolute file baseline" );
            }
        }

        paths.push_back( baseline.Path() );

        const bool isTable = FILE_CONTENT_BASELINE::SamePath( baseline.Path(), aSnapshot.tablePath );
        const bool isLibrary = FILE_CONTENT_BASELINE::SamePath( baseline.Path(), aSnapshot.expandedUri );

        if( isTable && !baseline.Exists() )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "the table and expanded library URI each need an existing content baseline" );
        }

        if( isLibrary && !baseline.Exists()
                && !( isFootprint && aSnapshot.mcpDirectFootprintFile && !aSnapshot.entryExists ) )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "the expanded library URI needs an existing content baseline unless this is an admitted new footprint entry" );
        }

        hasTableBaseline = hasTableBaseline || ( isTable && baseline.Exists() );
        hasLibraryBaseline = hasLibraryBaseline || ( isLibrary && ( baseline.Exists()
                || ( isFootprint && aSnapshot.mcpDirectFootprintFile && !aSnapshot.entryExists ) ) );

        if( baseline.Exists() && !IsSha256( baseline.Sha256() ) )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "a partial hash or cursor cannot be used as a symbol-library file baseline" );
        }

        if( !baseline.Exists() && ( !baseline.Sha256().empty() || baseline.Bytes() != 0 ) )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "an absent dependency must have an empty digest and zero bytes" );
        }
    }

    if( !hasTableBaseline || !hasLibraryBaseline )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                      "the table and expanded library URI each need an existing content baseline" );
    }

    // The current owner coverage deliberately remains incomplete.  A true value here would be a
    // false claim because symbol-editor, import/rescue and remote writers can still change files.
    if( aSnapshot.completeChangeTracking || aSnapshot.postverify )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                      "the read-only foundation cannot claim complete mutation tracking or postverify" );
    }

    return {};
}


LIBRARY_LIFECYCLE_RESULT AdmitLibraryLifecycleMutation(
        const LIBRARY_LIFECYCLE_SNAPSHOT& aSnapshot, const wxString& aExpectedProjectName,
        const wxString& aExpectedProjectPath )
{
    LIBRARY_LIFECYCLE_RESULT valid = ValidateLibraryLifecycleSnapshot(
            aSnapshot, aExpectedProjectName, aExpectedProjectPath );

    if( !valid )
        return valid;

    // The full native writer inventory remains incomplete, so this foundation must never turn
    // into a general library mutation permission.  The checked MCP lane is a deliberately narrow
    // exception: it is admitted only for a direct KiCad symbol file whose complete dependency
    // baselines and rollback bytes were captured by the same native owner.
    if( ( aSnapshot.document.type() != kiapi::common::types::DOCTYPE_SYMBOL
          && aSnapshot.document.type() != kiapi::common::types::DOCTYPE_FOOTPRINT )
            || ( aSnapshot.document.type() == kiapi::common::types::DOCTYPE_SYMBOL
                 && ( aSnapshot.provider != "KiCad" || !aSnapshot.mcpDirectSymbolFile
                      || aSnapshot.rawLibraryContents.empty() ) )
            || ( aSnapshot.document.type() == kiapi::common::types::DOCTYPE_FOOTPRINT
                 && ( aSnapshot.provider != "kicad-footprint-file"
                      || !aSnapshot.mcpDirectFootprintFile ) ) )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::MUTATION_NOT_AVAILABLE,
                      "checked library mutation is unavailable outside the direct MCP KiCad symbol-file scope" );
    }

    return {};
}
