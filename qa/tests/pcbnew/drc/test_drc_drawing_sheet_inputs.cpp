/* Drawing-sheet inputs must not fall back to changing global editor data. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <board.h>
#include <board_design_settings.h>
#include <cli_progress_reporter.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <drc/drc_run_scope.h>
#include <drawing_sheet/ds_data_item.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_proxy_view_item.h>

BOOST_AUTO_TEST_SUITE( DrcDrawingSheetInputs )

BOOST_AUTO_TEST_CASE( CapturedLayoutProducesItsOwnFindingsAndLeavesGlobalLayoutUntouched )
{
    DS_DATA_MODEL source;
    source.AllowVoidList( true );
    auto* text = new DS_DATA_ITEM_TEXT( "${DRC_ERROR captured-page-only}" );
    text->SetStart( 10, 10, LT_CORNER );
    source.Append( text );
    auto captured = source.CloneForRendering();
    text->m_TextBase = "${DRC_ERROR later-page-only}";
    auto& global = DS_DATA_MODEL::GetTheInstance();
    wxString globalBefore;
    global.SaveInString( &globalBefore );
    const double globalUnits = global.m_WSunits2Iu;

    BOARD board;
    auto& settings = board.GetDesignSettings();
    settings.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &settings );
    auto& engine = *settings.m_DRCEngine;
    engine.InitEngine( wxFileName() );
    for( int code = DRCE_FIRST; code <= DRCE_LAST; ++code )
        settings.m_DRCSeverities[code] = SEVERITY::RPT_SEVERITY_IGNORE;
    settings.m_DRCSeverities[DRCE_UNRESOLVED_VARIABLE] = SEVERITY::RPT_SEVERITY_ERROR;
    settings.m_DRCSeverities[DRCE_GENERIC_ERROR] = SEVERITY::RPT_SEVERITY_ERROR;
    DS_PROXY_VIEW_ITEM proxy( pcbIUScale, &board.GetPageSettings(), board.GetProject(),
                              &board.GetTitleBlock(), &board.GetProperties() );
    bool running = false;
    for( bool old : { true, false } )
    {
        std::vector<wxString> messages;
        {
            DRC_RUN_SCOPE scope( engine, running );
            engine.SetDrawingSheet( &proxy );
            engine.SetDrawingSheetModel( old ? std::move( captured ) : source.CloneForRendering() );
            auto* owned = engine.GetDrawingSheetModel();
            BOOST_REQUIRE( owned );
            BOOST_CHECK_THROW( DRC_RUN_SCOPE( engine, running ), std::logic_error );
            BOOST_CHECK( engine.GetDrawingSheetModel() == owned );
            engine.SetViolationHandler( [&]( const auto& item, const auto&, int, const auto& )
            {
                BOOST_CHECK_EQUAL( item->GetErrorCode(), DRCE_GENERIC_ERROR );
                messages.push_back( item->GetErrorMessage( false ) );
            } );
            BOOST_REQUIRE( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::COMPLETED );
        }
        BOOST_REQUIRE_EQUAL( messages.size(), 1 );
        BOOST_CHECK( messages.front().Contains( old ? "captured-page-only" : "later-page-only" ) );
        BOOST_CHECK( !messages.front().Contains( old ? "later-page-only" : "captured-page-only" ) );
        BOOST_CHECK( engine.GetDrawingSheetModel() == nullptr );
        BOOST_CHECK( engine.GetDrawingSheet() == nullptr );
        BOOST_CHECK( !running );
    }
    wxString globalAfter;
    global.SaveInString( &globalAfter );
    BOOST_CHECK( globalBefore == globalAfter );
    BOOST_CHECK_EQUAL( global.m_WSunits2Iu, globalUnits );
}

BOOST_AUTO_TEST_CASE( CancellationReleasesDrawingModelAndBorrowedProxy )
{
    class CANCELLED : public CLI_PROGRESS_REPORTER
    {
    public:
        bool IsCancelled() const override { return true; }
    } reporter;
    BOARD board;
    DRC_ENGINE engine( &board, &board.GetDesignSettings() );
    DS_PROXY_VIEW_ITEM proxy( pcbIUScale, &board.GetPageSettings(), nullptr,
                              &board.GetTitleBlock(), &board.GetProperties() );
    bool running = false;
    {
        DRC_RUN_SCOPE scope( engine, running );
        engine.SetDrawingSheet( &proxy );
        engine.SetDrawingSheetModel( std::make_unique<DS_DATA_MODEL>() );
        engine.SetProgressReporter( &reporter );
        BOOST_CHECK( engine.RunTests( EDA_UNITS::MM, false, false ) == DRC_RUN_RESULT::CANCELLED );
    }
    BOOST_CHECK( engine.GetDrawingSheetModel() == nullptr );
    BOOST_CHECK( engine.GetDrawingSheet() == nullptr );
    BOOST_CHECK( engine.GetProgressReporter() == nullptr );
    BOOST_CHECK( !running );
}

BOOST_AUTO_TEST_SUITE_END()
