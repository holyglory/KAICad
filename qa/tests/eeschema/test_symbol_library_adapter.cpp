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

#include <qa_utils/wx_utils/unit_test_utils.h>

#include <file_content_baseline.h>
#include <file_write_observer.h>
#include <ki_exception.h>
#include <lib_symbol.h>
#include <libraries/symbol_library_adapter.h>
#include <libraries/library_manager.h>
#include <sch_io/sch_io.h>
#include <sch_io/sch_io_mgr.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>

#include <wx/filename.h>
#include <wx/textfile.h>

#include <chrono>
#include <filesystem>


namespace
{

/**
 * Exposes a way to seed a LOAD_ERROR sentinel entry into the project-scope map,
 * exactly as the async loader leaves it when a library fails to load (status set
 * to LOAD_ERROR, plugin and row left null).
 */
class TEST_SYMBOL_LIBRARY_ADAPTER : public SYMBOL_LIBRARY_ADAPTER
{
public:
    using SYMBOL_LIBRARY_ADAPTER::SYMBOL_LIBRARY_ADAPTER;

    void SeedLoadError( const wxString& aNickname )
    {
        m_libraries[aNickname].status.load_status = LOAD_STATUS::LOAD_ERROR;
    }

    /// Seed a LOAD_ERROR sentinel carrying a real error message, as the async loader
    /// leaves it when a library plugin fails to enumerate.
    void SeedLoadError( const wxString& aNickname, const wxString& aMessage )
    {
        m_libraries[aNickname].status.load_status = LOAD_STATUS::LOAD_ERROR;
        m_libraries[aNickname].status.error = LIBRARY_ERROR( aMessage );
    }

    /// Mark a table row as successfully loaded so GetLibraryNames() reports it.
    void SeedLoaded( const wxString& aNickname )
    {
        m_libraries[aNickname].status.load_status = LOAD_STATUS::LOADED;
    }
};


/// Writes a project sym-lib-table listing the given nicknames in the order supplied.
class SCOPED_SYM_LIB_TABLE
{
public:
    SCOPED_SYM_LIB_TABLE( const std::vector<wxString>& aNicknamesInTableOrder,
                         SCH_IO_MGR::SCH_FILE_T aProvider = SCH_IO_MGR::SCH_KICAD,
                         bool aBuffering = false ) :
            m_extension( aProvider == SCH_IO_MGR::SCH_LEGACY ? wxS( ".lib" ) : wxS( ".kicad_sym" ) )
    {
        std::error_code ec;
        m_dir = std::filesystem::temp_directory_path( ec )
                / std::filesystem::path( wxString::Format( wxS( "kicad_qa_symlibtbl_%p" ), (void*) this )
                                                 .ToStdString() );
        std::filesystem::create_directories( m_dir, ec );

        wxTextFile file( wxString( ( m_dir / "sym-lib-table" ).string() ) );
        file.Create();
        file.AddLine( wxS( "(sym_lib_table" ) );
        file.AddLine( wxS( "  (version 7)" ) );

        for( const wxString& nickname : aNicknamesInTableOrder )
        {
            file.AddLine( wxString::Format(
                    wxS( "  (lib (name \"%s\")(type \"%s\")(uri \"%s\")(options \"%s\")(descr \"\"))" ),
                    nickname, SCH_IO_MGR::ShowType( aProvider ), LibraryPath( nickname ),
                    aBuffering ? wxS( "buffering" ) : wxS( "" ) ) );
        }

        file.AddLine( wxS( ")" ) );
        file.Write();
        file.Close();
    }

    ~SCOPED_SYM_LIB_TABLE()
    {
        std::error_code ec;
        std::filesystem::remove_all( m_dir, ec );
    }

    wxString Path() const { return wxString( m_dir.string() ); }

    wxString LibraryPath( const wxString& aNickname ) const
    {
        return wxFileName( Path(), aNickname + m_extension ).GetFullPath();
    }

private:
    std::filesystem::path m_dir;
    wxString m_extension;
};

} // namespace


BOOST_AUTO_TEST_SUITE( SymbolLibraryAdapter )


/**
 * Regression test for a null-plugin dereference crash.
 *
 * A symbol library that fails to load leaves a LOAD_ERROR sentinel with a null
 * plugin in the adapter's map. IsSymbolLibWritable() is called for every table row
 * by the symbol editor and chooser, so it must report such a library as not writable
 * instead of dereferencing the null plugin.
 */
BOOST_AUTO_TEST_CASE( IsSymbolLibWritableHandlesFailedLoad )
{
    LIBRARY_MANAGER             manager;
    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );

    adapter.SeedLoadError( wxS( "BadLib" ) );

    BOOST_CHECK_EQUAL( adapter.IsSymbolLibWritable( wxS( "BadLib" ) ), false );

    // A library that was never even attempted must also be safe.
    BOOST_CHECK_EQUAL( adapter.IsSymbolLibWritable( wxS( "NeverSeen" ) ), false );
}


/**
 * Regression test for https://gitlab.com/kicad/code/kicad/-/issues/23066.
 *
 * The library browsers list nicknames straight from GetLibraryNames(). When personal
 * libraries were appended to the table after the default ones, the raw table order left
 * them grouped at the bottom instead of interleaved alphabetically. GetLibraryNames() must
 * return the available nicknames in case-insensitive natural order regardless of table order.
 */
BOOST_AUTO_TEST_CASE( GetLibraryNamesSortedRegardlessOfTableOrder )
{
    const std::vector<wxString> tableOrder = { wxS( "Zebra" ), wxS( "amplifier" ), wxS( "Device" ),
                                               wxS( "Board_2" ), wxS( "Board_10" ) };

    SCOPED_SYM_LIB_TABLE tableFile( tableOrder );

    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );

    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );

    for( const wxString& nickname : tableOrder )
        adapter.SeedLoaded( nickname );

    const std::vector<wxString> names = adapter.GetLibraryNames();

    const std::vector<wxString> expected = { wxS( "amplifier" ), wxS( "Board_2" ), wxS( "Board_10" ),
                                             wxS( "Device" ), wxS( "Zebra" ) };

    BOOST_CHECK_EQUAL_COLLECTIONS( names.begin(), names.end(), expected.begin(), expected.end() );
}


/**
 * Regression test for https://gitlab.com/kicad/code/kicad/-/issues/22764.
 *
 * With "remember open files" enabled the schematic auto-opens and loads symbols on demand
 * while the background preload is still enumerating the library table. At any instant some
 * enabled table rows have no loaded entry yet. GetLibraryLoadErrors() collects the status of
 * every enabled row, and a row present in the table but not yet loaded must not be reported
 * as a load error, otherwise a spurious error indicator appears and its count varies with the
 * load timing from one launch to the next. Genuine failures still carry a real error message
 * and must still be reported.
 */
BOOST_AUTO_TEST_CASE( PartiallyLoadedTableReportsNoSpuriousErrors )
{
    const std::vector<wxString> tableOrder = { wxS( "Device" ), wxS( "Connector" ), wxS( "Amplifier" ),
                                               wxS( "Regulator" ), wxS( "BadLib" ) };

    SCOPED_SYM_LIB_TABLE tableFile( tableOrder );

    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );

    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );

    // Simulate a preload caught mid-flight: two libraries resolved, one genuinely failed,
    // and the remaining table rows have no entry yet because loading has not reached them.
    adapter.SeedLoaded( wxS( "Device" ) );
    adapter.SeedLoaded( wxS( "Connector" ) );
    adapter.SeedLoadError( wxS( "BadLib" ), wxS( "File is corrupt" ) );

    wxString errors;

    for( const KI_ERROR& error : adapter.GetLibraryLoadErrors() )
        errors.Append( error.AsString() + "\n" );

    // The unloaded-but-present rows must not masquerade as missing-from-table errors.
    BOOST_CHECK( !errors.Contains( wxS( "not found in library table" ) ) );
    BOOST_CHECK( !errors.Contains( wxS( "Amplifier" ) ) );
    BOOST_CHECK( !errors.Contains( wxS( "Regulator" ) ) );

    // The genuine failure must still be reported.
    BOOST_CHECK( errors.Contains( wxS( "BadLib" ) ) );
    BOOST_CHECK( errors.Contains( wxS( "File is corrupt" ) ) );
}


// Exercise the adapter through the actual writable providers and disposable project tables.
// These tests include file readback and cache destruction, rather than substituting fake writers.
BOOST_AUTO_TEST_CASE( SaveSymbolCollisionPreservesCacheAndCreatePersists )
{
    for( SCH_IO_MGR::SCH_FILE_T provider : { SCH_IO_MGR::SCH_KICAD, SCH_IO_MGR::SCH_LEGACY } )
    {
        BOOST_TEST_CONTEXT( SCH_IO_MGR::ShowType( provider ).ToStdString() )
        {
            const wxString nickname = wxS( "Writable" );
            SCOPED_SYM_LIB_TABLE tableFile( { nickname }, provider );
            const wxString path = tableFile.LibraryPath( nickname );
            {
                IO_RELEASER<SCH_IO> plugin( SCH_IO_MGR::FindPlugin( provider ) );
                plugin->CreateLibrary( path );
            }

            {
                LIBRARY_MANAGER manager;
                manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
                TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
                const auto loaded = adapter.LoadOne( nickname );
                BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );

                LIB_SYMBOL original( wxS( "Existing" ) );
                original.GetValueField().SetText( original.GetName() );
                original.GetReferenceField().SetText( wxS( "U" ) );
                BOOST_REQUIRE( adapter.SaveSymbol( nickname, &original ) == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
                LIB_SYMBOL* cached = adapter.LoadSymbol( nickname, original.GetName() );
                BOOST_REQUIRE( cached );
                BOOST_CHECK( cached != &original );
                const auto baseline = FILE_CONTENT_BASELINE::Read( path );
                BOOST_REQUIRE( baseline.Known() );

                LIB_SYMBOL replacement( original );
                replacement.GetReferenceField().SetText( wxS( "R" ) );
                BOOST_CHECK( adapter.SaveSymbol( nickname, &replacement, false )
                             == SYMBOL_LIBRARY_ADAPTER::SAVE_SKIPPED );
                LIB_SYMBOL* afterSkip = adapter.LoadSymbol( nickname, original.GetName() );
                BOOST_REQUIRE( afterSkip );
                BOOST_CHECK( afterSkip == cached );
                BOOST_CHECK_EQUAL( afterSkip->GetReferenceField().GetText().ToStdString(), "U" );
                BOOST_CHECK( baseline.Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );

                // Borrowed input is cloned before refreshing or replacing the provider cache.
                const std::filesystem::path libraryFile( path.ToStdString() );
                std::filesystem::last_write_time(
                        libraryFile, std::filesystem::last_write_time( libraryFile ) + std::chrono::seconds( 2 ) );
                BOOST_REQUIRE( adapter.SaveSymbol( nickname, afterSkip ) == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
                LIB_SYMBOL* afterCopy = adapter.LoadSymbol( nickname, original.GetName() );
                BOOST_REQUIRE( afterCopy );
                BOOST_CHECK_EQUAL( afterCopy->GetReferenceField().GetText().ToStdString(), "U" );

                LIB_SYMBOL created( wxS( "Created" ) );
                created.GetValueField().SetText( created.GetName() );
                created.GetReferenceField().SetText( wxS( "Q" ) );
                BOOST_REQUIRE( adapter.SaveSymbol( nickname, &created, false )
                               == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
                BOOST_CHECK_EQUAL( created.GetReferenceField().GetText().ToStdString(), "Q" );
            } // Destroys the provider cache: skipped and saved entries must remain provider-owned.

            IO_RELEASER<SCH_IO> reopened( SCH_IO_MGR::FindPlugin( provider ) );
            LIB_SYMBOL* existing = reopened->LoadSymbol( path, wxS( "Existing" ) );
            LIB_SYMBOL* created = reopened->LoadSymbol( path, wxS( "Created" ) );
            BOOST_REQUIRE( existing && created );
            BOOST_CHECK_EQUAL( existing->GetReferenceField().GetText().ToStdString(), "U" );
            BOOST_CHECK_EQUAL( created->GetReferenceField().GetText().ToStdString(), "Q" );
        }
    }
}


BOOST_AUTO_TEST_CASE( SaveSymbolPreservesExplicitBuffering )
{
    for( SCH_IO_MGR::SCH_FILE_T provider : { SCH_IO_MGR::SCH_KICAD, SCH_IO_MGR::SCH_LEGACY } )
    {
        BOOST_TEST_CONTEXT( SCH_IO_MGR::ShowType( provider ).ToStdString() )
        {
            const wxString nickname = wxS( "Buffered" );
            SCOPED_SYM_LIB_TABLE tableFile( { nickname }, provider, true );
            const wxString path = tableFile.LibraryPath( nickname );
            {
                IO_RELEASER<SCH_IO> plugin( SCH_IO_MGR::FindPlugin( provider ) );
                plugin->CreateLibrary( path );
            }
            const auto baseline = FILE_CONTENT_BASELINE::Read( path );
            BOOST_REQUIRE( baseline.Known() );
            {
                LIBRARY_MANAGER manager;
                manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
                TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
                const auto loaded = adapter.LoadOne( nickname );
                BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
                LIB_SYMBOL created( wxS( "Pending" ) );
                created.GetValueField().SetText( created.GetName() );
                BOOST_REQUIRE( adapter.SaveSymbol( nickname, &created, false )
                               == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
                LIB_SYMBOL* cached = adapter.LoadSymbol( nickname, created.GetName() );
                BOOST_REQUIRE( cached );
                BOOST_CHECK( cached != &created );
                BOOST_CHECK( baseline.Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );
            }
            IO_RELEASER<SCH_IO> reopened( SCH_IO_MGR::FindPlugin( provider ) );
            BOOST_CHECK( reopened->LoadSymbol( path, wxS( "Pending" ) ) == nullptr );
        }
    }
}


BOOST_AUTO_TEST_CASE( SaveSymbolWriteRefusalPropagatesAndReloadDiscardsFailedCache )
{
    const wxString nickname = wxS( "Writable" );
    SCOPED_SYM_LIB_TABLE tableFile( { nickname } );
    const wxString path = tableFile.LibraryPath( nickname );
    {
        IO_RELEASER<SCH_IO> plugin( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
        plugin->CreateLibrary( path );
    }
    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
    const auto loaded = adapter.LoadOne( nickname );
    BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );
    LIB_SYMBOL original( wxS( "Existing" ) );
    original.GetValueField().SetText( original.GetName() );
    original.GetReferenceField().SetText( wxS( "U" ) );
    BOOST_REQUIRE( adapter.SaveSymbol( nickname, &original ) == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
    const auto baseline = FILE_CONTENT_BASELINE::Read( path );
    BOOST_REQUIRE( baseline.Known() );

    auto replacement = std::make_unique<LIB_SYMBOL>( original );
    replacement->GetReferenceField().SetText( wxS( "R" ) );
    unsigned refused = 0, committed = 0;
    {
        FILE_WRITE_OBSERVER observer(
                [&]( const wxString& aPath )
                {
                    BOOST_CHECK( FILE_CONTENT_BASELINE::SamePath( path, aPath ) );
                    ++refused;
                    THROW_IO_ERROR( "Controlled symbol-library write refusal" );
                },
                [&]( const FILE_CONTENT_BASELINE& ) { ++committed; } );
        BOOST_CHECK_EXCEPTION( adapter.SaveSymbol( nickname, replacement.get() ), IO_ERROR,
                               []( const IO_ERROR& aError )
                               {
                                   return aError.What().Contains( wxS( "Controlled symbol-library write refusal" ) );
                               } );
    }
    BOOST_CHECK_EQUAL( refused, 1 );
    BOOST_CHECK_EQUAL( committed, 0 );
    BOOST_CHECK( baseline.Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );
    LIB_SYMBOL* failedCache = adapter.LoadSymbol( nickname, original.GetName() );
    BOOST_REQUIRE( failedCache );
    BOOST_CHECK( failedCache != replacement.get() );
    BOOST_CHECK_EQUAL( failedCache->GetReferenceField().GetText().ToStdString(), "U" );
    BOOST_CHECK_EQUAL( replacement->GetReferenceField().GetText().ToStdString(), "R" );
    replacement.reset(); // Failure did not transfer caller ownership into the changed cache.
    BOOST_CHECK_EQUAL( failedCache->GetReferenceField().GetText().ToStdString(), "U" );

    // The failed write is not a rollback. Explicit reload destroys the clone and restores disk state.
    adapter.ReloadLibraryEntry( nickname, LIBRARY_TABLE_SCOPE::PROJECT );
    const auto reloaded = adapter.LoadOne( nickname );
    BOOST_REQUIRE( reloaded && reloaded->load_status == LOAD_STATUS::LOADED );
    LIB_SYMBOL* restored = adapter.LoadSymbol( nickname, original.GetName() );
    BOOST_REQUIRE( restored );
    BOOST_CHECK_EQUAL( restored->GetReferenceField().GetText().ToStdString(), "U" );
    BOOST_CHECK( baseline.Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );
    BOOST_CHECK( adapter.SaveSymbol( nickname, &original, false ) == SYMBOL_LIBRARY_ADAPTER::SAVE_SKIPPED );
}


BOOST_AUTO_TEST_CASE( SaveSymbolCheckedVerifiesEntriesAndReportsWriteBoundaries )
{
    const wxString nickname = wxS( "Checked" );
    SCOPED_SYM_LIB_TABLE tableFile( { nickname } );
    const wxString path = tableFile.LibraryPath( nickname );
    {
        IO_RELEASER<SCH_IO> plugin( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
        plugin->CreateLibrary( path );
    }

    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
    const auto loaded = adapter.LoadOne( nickname );
    BOOST_REQUIRE( loaded && loaded->load_status == LOAD_STATUS::LOADED );

    LIB_SYMBOL original( wxS( "Existing" ) );
    original.GetValueField().SetText( original.GetName() );
    original.GetReferenceField().SetText( wxS( "U" ) );
    BOOST_REQUIRE( adapter.SaveSymbol( nickname, &original ) == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );
    LIB_SYMBOL unrelated( wxS( "Unrelated" ) );
    unrelated.GetValueField().SetText( unrelated.GetName() );
    unrelated.GetReferenceField().SetText( wxS( "X" ) );
    BOOST_REQUIRE( adapter.SaveSymbol( nickname, &unrelated ) == SYMBOL_LIBRARY_ADAPTER::SAVE_OK );

    IO_RELEASER<SCH_IO> plugin( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
    auto* direct = dynamic_cast<SCH_IO_KICAD_SEXPR*>( plugin.get() );
    BOOST_REQUIRE( direct );

    std::string tableContents;
    FILE_CONTENT_BASELINE tableBefore = FILE_CONTENT_BASELINE::Read( tableFile.Path(), &tableContents );
    std::string beforeContents;
    FILE_CONTENT_BASELINE before = FILE_CONTENT_BASELINE::Read( path, &beforeContents );
    BOOST_REQUIRE( tableBefore.Known() && before.Known() );
    // The native checked lane admits the table row and the direct library bytes together.  Keep
    // this order stable: the write path compares every observed dependency by its retained path,
    // while the library file itself is the one intentional replacement.
    const std::vector<FILE_CONTENT_BASELINE> completeBefore { tableBefore, before };
    LIB_SYMBOL created( wxS( "Created" ) );
    created.GetValueField().SetText( created.GetName() );
    created.GetReferenceField().SetText( wxS( "C" ) );
    SCH_KICAD_SEXPR_CHECKED_WRITE_RESULT createResult = direct->SaveSymbolChecked(
            path, &created, false, completeBefore, beforeContents, created.GetName() );
    BOOST_CHECK( createResult.writeStarted );
    BOOST_CHECK( createResult.committed );
    BOOST_CHECK( createResult.postverified );
    BOOST_CHECK( createResult.targetVerified );
    BOOST_CHECK( createResult.unrelatedEntriesVerified );
    BOOST_CHECK( createResult.beforeDefinition.empty() );
    BOOST_CHECK( tableBefore.Check( tableFile.Path() ) == FILE_BASELINE_CHECK::UNCHANGED );

    IO_RELEASER<SCH_IO> createReadback( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
    LIB_SYMBOL* persistedCreated = createReadback->LoadSymbol( path, created.GetName() );
    LIB_SYMBOL* persistedUnrelated = createReadback->LoadSymbol( path, unrelated.GetName() );
    BOOST_REQUIRE( persistedCreated && persistedUnrelated );
    BOOST_CHECK_EQUAL( persistedCreated->GetReferenceField().GetText().ToStdString(), "C" );
    BOOST_CHECK_EQUAL( persistedUnrelated->GetReferenceField().GetText().ToStdString(), "X" );

    // A refusal before replacement leaves the original bytes and proves rollback to the
    // pre-write provider state.
    LIB_SYMBOL replacement( original );
    replacement.GetReferenceField().SetText( wxS( "R" ) );
    std::string refusalContents;
    FILE_CONTENT_BASELINE refusalBaseline = FILE_CONTENT_BASELINE::Read( path, &refusalContents );
    FILE_CONTENT_BASELINE refusalTableBaseline = FILE_CONTENT_BASELINE::Read( tableFile.Path() );
    const std::vector<FILE_CONTENT_BASELINE> completeRefusal { refusalTableBaseline, refusalBaseline };
    bool beforeCalled = false;
    SCH_KICAD_SEXPR_CHECKED_WRITE_RESULT refused;
    {
        FILE_WRITE_OBSERVER observer(
                [&]( const wxString& aPath )
                {
                    beforeCalled = true;
                    BOOST_CHECK( FILE_CONTENT_BASELINE::SamePath( path, aPath ) );
                    THROW_IO_ERROR( "Controlled checked write refusal before replacement" );
                },
                [&]( const FILE_CONTENT_BASELINE& ) {} );
        refused = direct->SaveSymbolChecked( path, &replacement, true, completeRefusal,
                                             refusalContents, replacement.GetName() );
    }
    BOOST_CHECK( beforeCalled );
    BOOST_CHECK( refused.writeStarted );
    BOOST_CHECK( refused.rollbackAttempted );
    BOOST_CHECK( refused.rollbackVerified );
    BOOST_CHECK( !refused.committed );
    BOOST_CHECK( refusalBaseline.Check( path ) == FILE_BASELINE_CHECK::UNCHANGED );
    IO_RELEASER<SCH_IO> refusalReadback( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
    LIB_SYMBOL* afterRefusal = refusalReadback->LoadSymbol( path, original.GetName() );
    BOOST_REQUIRE( afterRefusal );
    BOOST_CHECK_EQUAL( afterRefusal->GetReferenceField().GetText().ToStdString(), "U" );

    // A callback failure after the atomic replacement is reported as indeterminate: the provider
    // has no safe attribution for a second overwrite, and the persisted result remains observable.
    std::string afterContents;
    FILE_CONTENT_BASELINE afterBaseline = FILE_CONTENT_BASELINE::Read( path, &afterContents );
    FILE_CONTENT_BASELINE afterTableBaseline = FILE_CONTENT_BASELINE::Read( tableFile.Path() );
    const std::vector<FILE_CONTENT_BASELINE> completeAfter { afterTableBaseline, afterBaseline };
    bool afterCalled = false;
    SCH_KICAD_SEXPR_CHECKED_WRITE_RESULT afterFailure;
    {
        FILE_WRITE_OBSERVER observer(
                [&]( const wxString& aPath )
                { BOOST_CHECK( FILE_CONTENT_BASELINE::SamePath( path, aPath ) ); },
                [&]( const FILE_CONTENT_BASELINE& )
                {
                    if( !afterCalled )
                    {
                        afterCalled = true;
                        THROW_IO_ERROR( "Controlled checked write refusal after replacement" );
                    }
                } );
        afterFailure = direct->SaveSymbolChecked( path, &replacement, true, completeAfter,
                                                  afterContents, replacement.GetName() );
    }
    BOOST_CHECK( afterCalled );
    BOOST_CHECK( afterFailure.writeStarted );
    BOOST_CHECK( afterFailure.rollbackAttempted );
    BOOST_CHECK( afterFailure.rollbackIndeterminate );
    BOOST_CHECK( !afterFailure.committed );
    BOOST_CHECK( afterBaseline.Check( path ) == FILE_BASELINE_CHECK::CHANGED );
    IO_RELEASER<SCH_IO> afterReadback( SCH_IO_MGR::FindPlugin( SCH_IO_MGR::SCH_KICAD ) );
    LIB_SYMBOL* afterReplacement = afterReadback->LoadSymbol( path, replacement.GetName() );
    BOOST_REQUIRE( afterReplacement );
    BOOST_CHECK_EQUAL( afterReplacement->GetReferenceField().GetText().ToStdString(), "R" );
}


BOOST_AUTO_TEST_CASE( SaveSymbolRejectsInvalidInputAndReadFailures )
{
    const wxString nickname = wxS( "Broken" );
    SCOPED_SYM_LIB_TABLE tableFile( { nickname } );
    wxTextFile malformed( tableFile.LibraryPath( nickname ) );
    BOOST_REQUIRE( malformed.Create() );
    malformed.AddLine( wxS( "(kicad_symbol_lib (invalid" ) );
    BOOST_REQUIRE( malformed.Write() );
    malformed.Close();

    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
    LIB_SYMBOL source( wxS( "Source" ) );
    BOOST_CHECK_THROW( adapter.SaveSymbol( nickname, nullptr ), IO_ERROR );
    BOOST_CHECK_THROW( adapter.SaveSymbol( wxS( "Missing" ), &source, false ), IO_ERROR );
    BOOST_CHECK_THROW( adapter.SaveSymbol( nickname, &source, false ), IO_ERROR );
    BOOST_CHECK_EQUAL( source.GetName().ToStdString(), "Source" );
}


BOOST_AUTO_TEST_CASE( SaveSymbolRejectsUnsupportedProvider )
{
    const wxString nickname = wxS( "ReadOnly" );
    SCOPED_SYM_LIB_TABLE tableFile( { nickname }, SCH_IO_MGR::SCH_HTTP );
    LIBRARY_MANAGER manager;
    manager.LoadProjectTables( tableFile.Path(), { LIBRARY_TABLE_TYPE::SYMBOL } );
    TEST_SYMBOL_LIBRARY_ADAPTER adapter( manager );
    LIB_SYMBOL source( wxS( "Source" ) );
    BOOST_CHECK_EXCEPTION( adapter.SaveSymbol( nickname, &source ), IO_ERROR,
                           []( const IO_ERROR& aError )
                           {
                               return aError.What().Contains( wxS( "does not support saving symbols" ) );
                           } );
    BOOST_CHECK_EQUAL( source.GetName().ToStdString(), "Source" );
}


BOOST_AUTO_TEST_SUITE_END()
