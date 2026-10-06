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
            FILE_CONTENT_BASELINE::FromBytes( snapshot.tablePath, "(sym_lib_table)" ) );
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


LIBRARY_LIFECYCLE_SNAPSHOT FootprintSnapshot()
{
    const std::filesystem::path root =
            std::filesystem::temp_directory_path() / "kicad-library-lifecycle-footprint-fixture";
    const std::filesystem::path entry = root / "libs" / "fixture.pretty" / "Probe.kicad_mod";

    LIBRARY_LIFECYCLE_SNAPSHOT snapshot;
    snapshot.document.set_type( DOCTYPE_FOOTPRINT );
    snapshot.document.mutable_project()->set_name( "fixture" );
    snapshot.document.mutable_project()->set_path( root.string() );
    snapshot.document.mutable_lib_id()->set_library_nickname( "Fixture" );
    snapshot.document.mutable_lib_id()->set_entry_name( "Probe" );
    snapshot.tablePath = wxString::FromUTF8( ( root / "fp-lib-table" ).string() );
    snapshot.tableScope = LIBRARY_TABLE_SCOPE::PROJECT;
    snapshot.nickname = wxS( "Fixture" );
    snapshot.rawUri = wxS( "${KIPRJMOD}/libs/fixture.pretty" );
    snapshot.expandedUri = wxString::FromUTF8( entry.string() );
    snapshot.provider = "kicad-footprint-file";
    snapshot.detachedDefinition = "(footprint Probe)";
    snapshot.dependencyBaselines.push_back(
            FILE_CONTENT_BASELINE::FromBytes( snapshot.tablePath, "(fp_lib_table)" ) );
    snapshot.dependencyBaselines.push_back(
            FILE_CONTENT_BASELINE::FromBytes( snapshot.expandedUri, "(footprint Probe)" ) );
    snapshot.forcedRefresh = true;
    snapshot.providerObservable = true;
    snapshot.contentSnapshotComplete = true;
    snapshot.diskBaselineChecked = true;
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


BOOST_AUTO_TEST_CASE( RejectsRelativeProjectPathsEvenWhenExpectedPathMatches )
{
    auto snapshot = Snapshot();
    snapshot.document.mutable_project()->set_path( "relative/project" );
    RequireError( ValidateLibraryLifecycleSnapshot( snapshot, wxS( "fixture" ),
                                                     wxS( "relative/project" ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::PROJECT_MISMATCH );
}


BOOST_AUTO_TEST_CASE( AcceptsAbsoluteDotAliasesForProjectAndFiles )
{
    auto snapshot = Snapshot();
    const std::filesystem::path root = std::filesystem::path( snapshot.document.project().path() );
    const std::filesystem::path tableAlias = root / "tables" / ".." / "sym-lib-table";
    const std::filesystem::path libraryAlias = root / "libs" / ".." / "libs" / "fixture.kicad_sym";
    snapshot.tablePath = wxString::FromUTF8( tableAlias.string() );
    snapshot.expandedUri = wxString::FromUTF8( libraryAlias.string() );
    snapshot.dependencyBaselines.clear();
    snapshot.dependencyBaselines.push_back(
            FILE_CONTENT_BASELINE::FromBytes( snapshot.tablePath, "(sym_lib_table)" ) );
    snapshot.dependencyBaselines.push_back(
            FILE_CONTENT_BASELINE::FromBytes( snapshot.expandedUri, "fixture" ) );

    const auto result = ValidateLibraryLifecycleSnapshot(
            snapshot, wxS( "fixture" ), wxString::FromUTF8( ( root / "tables" / ".." ).string() ) );
    BOOST_REQUIRE( result.has_value() );
}


BOOST_AUTO_TEST_CASE( RejectsRelativeTableAndExpandedUri )
{
    auto relativeTable = Snapshot();
    relativeTable.tablePath = wxS( "relative/sym-lib-table" );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          relativeTable, wxS( "fixture" ),
                          wxString::FromUTF8( relativeTable.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );

    auto relativeLibrary = Snapshot();
    relativeLibrary.expandedUri = wxS( "relative/fixture.kicad_sym" );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          relativeLibrary, wxS( "fixture" ),
                          wxString::FromUTF8( relativeLibrary.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );
}


BOOST_AUTO_TEST_CASE( RejectsNulTableAndExpandedUri )
{
    auto nulTable = Snapshot();
    nulTable.tablePath += wxUniChar( '\0' );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          nulTable, wxS( "fixture" ),
                          wxString::FromUTF8( nulTable.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );

    auto nulLibrary = Snapshot();
    nulLibrary.expandedUri += wxUniChar( '\0' );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          nulLibrary, wxS( "fixture" ),
                          wxString::FromUTF8( nulLibrary.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );
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


BOOST_AUTO_TEST_CASE( RequiresExistingTableAndLibraryBaselines )
{
    auto missingTable = Snapshot();
    missingTable.dependencyBaselines.erase( missingTable.dependencyBaselines.begin() );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          missingTable, wxS( "fixture" ),
                          wxString::FromUTF8( missingTable.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );

    auto missingLibrary = Snapshot();
    missingLibrary.dependencyBaselines.pop_back();
    RequireError( ValidateLibraryLifecycleSnapshot(
                          missingLibrary, wxS( "fixture" ),
                          wxString::FromUTF8( missingLibrary.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );

    auto absentTable = Snapshot();
    const std::filesystem::path root = std::filesystem::path( absentTable.document.project().path() );
    absentTable.tablePath = wxString::FromUTF8( ( root / "missing-sym-lib-table" ).string() );
    absentTable.dependencyBaselines[0] = FILE_CONTENT_BASELINE::Read( absentTable.tablePath );
    // The producer owns filesystem IO.  The validator only requires an existing baseline for
    // the table and expanded URI identities; it does not probe the path itself.
    RequireError( ValidateLibraryLifecycleSnapshot(
                          absentTable, wxS( "fixture" ),
                          wxString::FromUTF8( absentTable.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );

    auto unknownLibrary = Snapshot();
    unknownLibrary.dependencyBaselines[1] = FILE_CONTENT_BASELINE{};
    RequireError( ValidateLibraryLifecycleSnapshot(
                          unknownLibrary, wxS( "fixture" ),
                          wxString::FromUTF8( unknownLibrary.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );
}


BOOST_AUTO_TEST_CASE( RejectsDuplicateNormalizedDependencyAliases )
{
    auto snapshot = Snapshot();
    const std::filesystem::path root = std::filesystem::path( snapshot.document.project().path() );
    snapshot.dependencyBaselines.push_back( FILE_CONTENT_BASELINE::FromBytes(
            wxString::FromUTF8( ( root / "libs" / ".." / "libs" / "fixture.kicad_sym" ).string() ),
            "duplicate" ) );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          snapshot, wxS( "fixture" ),
                          wxString::FromUTF8( snapshot.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INCOMPLETE_BASELINE );
}


BOOST_AUTO_TEST_CASE( RejectsDirectoryAndNestedSymbolFileShapes )
{
    auto directory = Snapshot();
    directory.expandedUri = wxString::FromUTF8(
            ( std::filesystem::path( directory.document.project().path() ) / "libs" ).string() );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          directory, wxS( "fixture" ),
                          wxString::FromUTF8( directory.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );

    auto nested = Snapshot();
    nested.expandedUri = wxString::FromUTF8( ( std::filesystem::path(
                                                       nested.document.project().path() )
                                               / "folder.kicad_sym" / "fixture.kicad_sym" )
                                                      .string() );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          nested, wxS( "fixture" ),
                          wxString::FromUTF8( nested.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );
}


BOOST_AUTO_TEST_CASE( AcceptsDirectFootprintEntryAndRejectsNestedEntry )
{
    const auto snapshot = FootprintSnapshot();
    BOOST_REQUIRE( ValidateLibraryLifecycleSnapshot(
                           snapshot, wxS( "fixture" ),
                           wxString::FromUTF8( snapshot.document.project().path() ) )
                           .has_value() );

    auto nested = snapshot;
    nested.expandedUri = wxString::FromUTF8( ( std::filesystem::path(
                                                        snapshot.document.project().path() )
                                                / "libs" / "fixture.pretty.kicad_mod" / "Probe.kicad_mod" )
                                                       .string() );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          nested, wxS( "fixture" ),
                          wxString::FromUTF8( nested.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );
}


BOOST_AUTO_TEST_CASE( RejectsFootprintRowsWithoutPrettyProviderOrWithExternalProvider )
{
    auto badRow = FootprintSnapshot();
    badRow.rawUri = wxS( "${KIPRJMOD}/libs/fixture.kicad_mod" );
    RequireError( ValidateLibraryLifecycleSnapshot(
                          badRow, wxS( "fixture" ),
                          wxString::FromUTF8( badRow.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::INVALID_TABLE_ROW );

    auto external = FootprintSnapshot();
    external.provider = "http";
    RequireError( ValidateLibraryLifecycleSnapshot(
                          external, wxS( "fixture" ),
                          wxString::FromUTF8( external.document.project().path() ) ),
                  LIBRARY_LIFECYCLE_ERROR_CODE::UNSUPPORTED_PROVIDER );
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
