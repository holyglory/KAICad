/* Preliminary read-only library lifecycle binding tests. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>

#include <api/library_lifecycle_snapshot.h>

#include <filesystem>


namespace
{
using namespace kiapi::common::types;


LIBRARY_LIFECYCLE_SNAPSHOT Snapshot()
{
    const std::filesystem::path root =
            std::filesystem::temp_directory_path() / "kicad-library-lifecycle-fixture";
    const std::filesystem::path library = root / "libs" / "fixture.kicad_sym";

    LIBRARY_LIFECYCLE_SNAPSHOT snapshot;
    snapshot.document.set_type( DOCTYPE_SYMBOL );
    snapshot.document.mutable_project()->set_name( "fixture" );
    snapshot.document.mutable_project()->set_path( root.string() );
    snapshot.document.mutable_lib_id()->set_library_nickname( "Fixture" );
    snapshot.document.mutable_lib_id()->set_entry_name( "Probe" );
    snapshot.tablePath = wxString::FromUTF8( ( root / "sym-lib-table" ).string() );
    snapshot.tableScope = LIBRARY_TABLE_SCOPE::PROJECT;
    snapshot.nickname = wxS( "Fixture" );
    snapshot.rawUri = wxS( "${KIPRJMOD}/libs/fixture.kicad_sym" );
    snapshot.expandedUri = wxString::FromUTF8( library.string() );
    snapshot.provider = "kicad-symbol-file";
    snapshot.detachedDefinition = "(symbol (lib_id Fixture:Probe))";
    snapshot.dependencyBaselines.push_back(
            FILE_CONTENT_BASELINE::FromBytes( snapshot.expandedUri, "fixture" ) );
    snapshot.forcedRefresh = true;
    snapshot.providerObservable = true;
    snapshot.contentSnapshotComplete = true;
    snapshot.diskBaselineChecked = true;
    // The unsupported editor/import/rescue owners keep this false until a shared event boundary
    // and rollback proof are implemented.
    snapshot.completeChangeTracking = false;
    snapshot.postverify = false;
    return snapshot;
}


void RequireError( const LIBRARY_LIFECYCLE_RESULT& aResult,
                   LIBRARY_LIFECYCLE_ERROR_CODE aCode )
{
    BOOST_REQUIRE( !aResult.has_value() );
    BOOST_CHECK( aResult.error().code == aCode );
}
}


BOOST_AUTO_TEST_SUITE( LibraryLifecycleSnapshot )


BOOST_AUTO_TEST_CASE( AcceptsExactProjectRowAndCompleteReadEvidence )
{
    const auto snapshot = Snapshot();
    const auto result = ValidateLibraryLifecycleSnapshot(
            snapshot, wxS( "fixture" ), snapshot.document.project().path() );
    BOOST_REQUIRE( result.has_value() );
}


BOOST_AUTO_TEST_CASE( RejectsForeignProjectBeforeProviderRead )
{
    auto snapshot = Snapshot();
    snapshot.document.mutable_project()->set_name( "other" );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          snapshot, wxS( "fixture" ), wxString::FromUTF8( snapshot.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::PROJECT_MISMATCH );
}


BOOST_AUTO_TEST_CASE( RejectsUnknownAndUnsupportedProviders )
{
    auto unknown = Snapshot();
    unknown.provider.clear();
    RequireError( ValidateLibraryLifecycleSnapshot(
                          unknown, wxS( "fixture" ), wxString::FromUTF8( unknown.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::UNKNOWN_PROVIDER );

    auto unsupported = Snapshot();
    unsupported.provider = "http";
    RequireError( ValidateLibraryLifecycleSnapshot(
                          unsupported, wxS( "fixture" ), wxString::FromUTF8( unsupported.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER );
}


BOOST_AUTO_TEST_CASE( RejectsIncompleteRefreshAndDependencyEvidence )
{
    auto noRefresh = Snapshot();
    noRefresh.forcedRefresh = false;
    RequireError( ValidateLibraryLifecycleSnapshot(
                          noRefresh, wxS( "fixture" ), wxString::FromUTF8( noRefresh.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT );

    auto partialBaseline = Snapshot();
    partialBaseline.dependencyBaselines.clear();
    // The public baseline API cannot manufacture a partial digest; the unknown default models
    // the same unsafe cursor/partial-hash condition at this native boundary.
    partialBaseline.dependencyBaselines.push_back( FILE_CONTENT_BASELINE{} );
    RequireError( ValidateLibraryLifecycleSnapshot( partialBaseline, wxS( "fixture" ),
                                                     wxString::FromUTF8( partialBaseline.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );
}


BOOST_AUTO_TEST_CASE( ReadSnapshotNeverAdmitsCheckedMutation )
{
    const auto snapshot = Snapshot();
    const auto result = AdmitLibraryLifecycleMutation(
            snapshot, wxS( "fixture" ), wxString::FromUTF8( snapshot.document.project().path() ) );
    RequireError( result, LIBRARY_LIFECYCLE_ERROR_CODE::MUTATION_NOT_AVAILABLE );

    auto falseTracking = snapshot;
    falseTracking.completeChangeTracking = true;
    RequireError( ValidateLibraryLifecycleSnapshot(
                          falseTracking, wxS( "fixture" ),
                          wxString::FromUTF8( falseTracking.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_SNAPSHOT );
}


BOOST_AUTO_TEST_SUITE_END()
