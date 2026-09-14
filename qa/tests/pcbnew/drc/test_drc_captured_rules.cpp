/* Captured custom rules must not silently become defaults or changing files. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <drc/drc_rule_parser.h>
#include <file_content_baseline.h>
#include <pcbnew_utils/board_test_utils.h>
#include <richio.h>
#include <reporter.h>
#include <fstream>

namespace
{
const std::string halfMillimeter = R"((version 1)
(rule "captured clearance" (constraint clearance (min 0.5mm))))";
const std::string eightTenths = R"((version 1)
(rule "captured clearance" (constraint clearance (min 0.8mm))))";

int Clearance( DRC_ENGINE& engine )
{
    DRC_CONSTRAINT value;
    BOOST_REQUIRE( engine.QueryWorstConstraint( CLEARANCE_CONSTRAINT, value ) );
    return value.GetValue().Min();
}
}

BOOST_AUTO_TEST_SUITE( DrcCapturedRules )

BOOST_AUTO_TEST_CASE( FileAndCapturedRulesAgreeButOnlyNewFileReadsFollowAnEdit )
{
    KI_TEST::TEMPORARY_DIRECTORY scratch( "drc_rules_" + KIID().AsStdString(), "" );
    const auto path = scratch.GetPath() / "fixture.kicad_dru";
    auto write = [&]( const std::string& value ) { std::ofstream stream( path ); stream << value; };
    write( halfMillimeter );
    const wxString filename = wxString::FromUTF8( path.string() );
    std::string captured;
    auto baseline = FILE_CONTENT_BASELINE::Read( filename, &captured );
    BOOST_REQUIRE( baseline.Known() && baseline.Exists() );
    const auto timestamp = std::filesystem::last_write_time( path );
    BOARD first, second;
    DRC_ENGINE fromFile( &first, &first.GetDesignSettings() );
    DRC_ENGINE fromText( &second, &second.GetDesignSettings() );
    fromFile.InitEngine( wxFileName( filename ) );
    fromText.InitEngineFromText( captured, "captured custom rules" );
    BOOST_CHECK_EQUAL( Clearance( fromFile ), 500000 );
    BOOST_CHECK_EQUAL( Clearance( fromText ), Clearance( fromFile ) );

    write( eightTenths );
    std::filesystem::last_write_time( path, timestamp );
    BOOST_CHECK( baseline.Check( filename ) == FILE_BASELINE_CHECK::CHANGED );
    fromFile.InitEngine( wxFileName( filename ) );
    fromText.InitEngineFromText( captured, "captured custom rules" );
    BOOST_CHECK_EQUAL( Clearance( fromFile ), 800000 );
    BOOST_CHECK_EQUAL( Clearance( fromText ), 500000 );
}

BOOST_AUTO_TEST_CASE( FailedInitializationCannotRunAnImplicitOnlyFallbackAsComplete )
{
    BOARD board;
    DRC_ENGINE engine( &board, &board.GetDesignSettings() );
    engine.InitEngineFromText( halfMillimeter, "valid captured rules" );
    BOOST_CHECK( engine.RulesValid() );
    REPORTER diagnostics;
    engine.SetLogReporter( &diagnostics );
    BOOST_CHECK_THROW( engine.InitEngineFromText(
            "(version 1) (rule \"broken\" (constraint clearance (min )))", "invalid captured rules" ), PARSE_ERROR );
    BOOST_CHECK( !engine.RulesValid() );
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::INCOMPLETE );
    engine.InitEngineFromText( eightTenths, "recovered captured rules" );
    BOOST_CHECK( engine.RulesValid() );
    BOOST_CHECK_EQUAL( Clearance( engine ), 800000 );
    const std::string future = "(version " + std::to_string( DRC_RULE_FILE_VERSION + 1 ) + ")\n"
                               "(rule \"future\" (constraint clearance (min 0.8mm)))";
    BOOST_CHECK_THROW( engine.InitEngineFromText( future, "unsupported captured rules" ), std::runtime_error );
    BOOST_CHECK( !engine.RulesValid() );
    BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::INCOMPLETE );
    engine.SetLogReporter( nullptr );
}

BOOST_AUTO_TEST_SUITE_END()
