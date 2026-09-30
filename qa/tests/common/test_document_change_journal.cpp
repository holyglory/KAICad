/* KiCad automation change journal tests. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/document_change_journal.h>

BOOST_AUTO_TEST_SUITE( DocumentChangeJournal )

BOOST_AUTO_TEST_CASE( OrderedHistoryAndNoChangeRead )
{
    DOCUMENT_CHANGE_JOURNAL journal;
    journal.Reset( "document-a" );
    BOOST_CHECK( !journal.ReadAfter( "document-a", 0 ).resetRequired );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT, "Move" );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::UNDO, "Undo" );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::REDO, "Redo" );
    auto history = journal.ReadAfter( "document-a", 1 );
    BOOST_REQUIRE_EQUAL( history.entries.size(), 2 );
    BOOST_CHECK_EQUAL( history.entries[0].sequence, 2 );
    BOOST_CHECK( history.entries[0].kind == DOCUMENT_CHANGE_JOURNAL::KIND::UNDO );
    BOOST_CHECK( history.entries[1].kind == DOCUMENT_CHANGE_JOURNAL::KIND::REDO );
    BOOST_CHECK( journal.ReadAfter( "document-a", 3 ).entries.empty() );
    BOOST_CHECK_EQUAL( journal.Sequence(), 3 );
}

BOOST_AUTO_TEST_CASE( MissedHistoryWrongEpochAndFutureCursorRequireSnapshot )
{
    DOCUMENT_CHANGE_JOURNAL journal( 2 );
    journal.Reset( "document-a" );
    for( int i = 0; i < 3; ++i )
        journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT, "Edit" );
    BOOST_CHECK( journal.ReadAfter( "document-a", 0 ).resetRequired );
    BOOST_CHECK( !journal.ReadAfter( "document-a", 1 ).resetRequired );
    BOOST_CHECK( journal.ReadAfter( "other-document", 2 ).resetRequired );
    BOOST_CHECK( journal.ReadAfter( "document-a", 4 ).resetRequired );
    journal.Reset( "document-b" );
    BOOST_CHECK_EQUAL( journal.Sequence(), 0 );
    BOOST_CHECK( journal.ReadAfter( "document-a", 0 ).resetRequired );
    BOOST_CHECK( journal.ReadAfter( "document-b", 0 ).entries.empty() );
    BOOST_CHECK_THROW( journal.Reset( "document-b" ), std::invalid_argument );
    BOOST_CHECK_THROW( journal.Reset( "" ), std::invalid_argument );
}

BOOST_AUTO_TEST_CASE( AutomationAttributionDoesNotLeakIntoUndoOrRedo )
{
    DOCUMENT_CHANGE_JOURNAL journal;
    journal.Reset( "document-a" );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT, "Synchronize", "sync-a", "operation-a" );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::UNDO, "Undo" );
    journal.Append( DOCUMENT_CHANGE_JOURNAL::KIND::REDO, "Redo" );
    auto history = journal.ReadAfter( "document-a", 0 );
    BOOST_REQUIRE_EQUAL( history.entries.size(), 3 );
    BOOST_CHECK_EQUAL( history.entries[0].originId, "sync-a" );
    BOOST_CHECK_EQUAL( history.entries[0].operationId, "operation-a" );
    for( size_t i = 1; i < history.entries.size(); ++i )
    {
        BOOST_CHECK( history.entries[i].originId.empty() );
        BOOST_CHECK( history.entries[i].operationId.empty() );
    }
}

BOOST_AUTO_TEST_SUITE_END()

// The schematic change-tracking coverage oracle reads only the source tree and needs no editor,
// so the focused journal check compiles and runs it as well.
#include "../eeschema/test_sch_change_tracking.cpp"
