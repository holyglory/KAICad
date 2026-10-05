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

    if( document.type() != DOCTYPE_SYMBOL || !document.has_project() || !document.has_lib_id()
            || document.project().name().empty() || document.project().path().empty()
            || document.lib_id().library_nickname().empty() || document.lib_id().entry_name().empty()
            || document.project().name().find( '\0' ) != std::string::npos
            || document.project().path().find( '\0' ) != std::string::npos
            || document.lib_id().library_nickname().find( '\0' ) != std::string::npos
            || document.lib_id().entry_name().find( '\0' ) != std::string::npos )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_DOCUMENT,
                      "a symbol-library snapshot requires a project-bound symbol DocumentSpecifier" );
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
                      "the requested symbol-library project is not the project served by this native owner" );
    }

    if( aSnapshot.nickname.empty() || aSnapshot.nickname.Find( '\0' ) != wxNOT_FOUND
            || aSnapshot.nickname.ToStdString( wxConvUTF8 ) != document.lib_id().library_nickname()
            || ( aSnapshot.tableScope != LIBRARY_TABLE_SCOPE::GLOBAL
                 && aSnapshot.tableScope != LIBRARY_TABLE_SCOPE::PROJECT )
            || !IsAbsoluteFile( aSnapshot.tablePath ) || aSnapshot.rawUri.empty()
            || aSnapshot.rawUri.Find( '\0' ) != wxNOT_FOUND || !IsSymbolFile( aSnapshot.expandedUri ) )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW,
                      "the symbol-library table row is missing its exact scope, path or URI identity" );
    }

    if( aSnapshot.provider.empty() )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::UNKNOWN_PROVIDER,
                      "the symbol-library provider was not identified" );
    }

    // This foundation slice can observe a direct KiCad symbol file only.  Folder, database,
    // HTTP and nested providers are deliberately refused until their complete file sets and
    // mutation owners are covered by the same lifecycle contract.
    if( aSnapshot.provider != "kicad-symbol-file" && aSnapshot.provider != "KiCad" )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER,
                      "the symbol-library provider is not covered by the read-only lifecycle slice" );
    }

    if( !aSnapshot.forcedRefresh || !aSnapshot.providerObservable || !aSnapshot.contentSnapshotComplete
            || !aSnapshot.diskBaselineChecked || aSnapshot.detachedDefinition.empty()
            || aSnapshot.dependencyBaselines.empty() )
    {
        return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT,
                      "a safe symbol-library snapshot requires a forced refresh, detached definition, "
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
                          "every symbol-library dependency needs one complete, unique absolute file baseline" );
        }

        for( const wxString& path : paths )
        {
            if( FILE_CONTENT_BASELINE::SamePath( path, baseline.Path() ) )
            {
                return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                              "every symbol-library dependency needs one complete, unique absolute file baseline" );
            }
        }

        paths.push_back( baseline.Path() );

        const bool isTable = FILE_CONTENT_BASELINE::SamePath( baseline.Path(), aSnapshot.tablePath );
        const bool isLibrary = FILE_CONTENT_BASELINE::SamePath( baseline.Path(), aSnapshot.expandedUri );

        if( ( isTable || isLibrary ) && !baseline.Exists() )
        {
            return Error( LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE,
                          "the table and expanded symbol-library URI each need an existing content baseline" );
        }

        hasTableBaseline = hasTableBaseline || ( isTable && baseline.Exists() );
        hasLibraryBaseline = hasLibraryBaseline || ( isLibrary && baseline.Exists() );

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
                      "the table and expanded symbol-library URI each need an existing content baseline" );
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

    return Error( LIBRARY_LIFECYCLE_ERROR_CODE::MUTATION_NOT_AVAILABLE,
                  "checked symbol-library mutation is unavailable until every native writer has "
                  "shared tracking and rollback proof" );
}
