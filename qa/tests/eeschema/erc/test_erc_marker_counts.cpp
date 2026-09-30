/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.TXT for contributors.
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 3
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#include <qa_utils/wx_utils/unit_test_utils.h>
#include <schematic_utils/schematic_file_util.h>

#include <connection_graph.h>
#include <schematic.h>
#include <sch_marker.h>
#include <erc/erc_settings.h>
#include <erc/erc.h>
#include <settings/settings_manager.h>
#include <locale_io.h>
#include <api/api_sch_erc_settings.h>
#include <algorithm>


struct ERC_MARKER_COUNT_FIXTURE
{
    SETTINGS_MANAGER           m_settingsManager;
    std::unique_ptr<SCHEMATIC> m_schematic;
};


/**
 * SHEETLIST_ERC_ITEMS_PROVIDER caches per-severity counts. Excluding a marker moves it between
 * its error/warning bucket and the exclusion bucket, so the cached counts must follow the
 * transition and stay identical to a fresh full recompute.
 */
BOOST_FIXTURE_TEST_CASE( ERCMarkerCountsExclusion, ERC_MARKER_COUNT_FIXTURE )
{
    LOCALE_IO dummy;

    KI_TEST::LoadSchematic( m_settingsManager, "issue10430", m_schematic );

    ERC_SETTINGS& settings = m_schematic->ErcSettings();

    settings.m_ERCSeverities[ERCE_LIB_SYMBOL_ISSUES] = RPT_SEVERITY_IGNORE;
    settings.m_ERCSeverities[ERCE_LIB_SYMBOL_MISMATCH] = RPT_SEVERITY_IGNORE;

    m_schematic->ConnectionGraph()->RunERC();

    ERC_TESTER tester( m_schematic.get() );
    tester.TestMultUnitPinConflicts();
    tester.TestMultiunitFootprints();
    tester.TestNoConnectPins();
    tester.TestPinToPin();
    tester.TestSimilarLabels();
    tester.TestTextVars( nullptr );

    const int allSeverities = RPT_SEVERITY_ERROR | RPT_SEVERITY_WARNING | RPT_SEVERITY_EXCLUSION;

    SHEETLIST_ERC_ITEMS_PROVIDER provider( m_schematic.get() );
    provider.SetSeverities( allSeverities );

    BOOST_REQUIRE( provider.GetCount() > 0 );

    const int errorsBefore = provider.GetCount( RPT_SEVERITY_ERROR );
    const int warningsBefore = provider.GetCount( RPT_SEVERITY_WARNING );
    const int exclusionsBefore = provider.GetCount( RPT_SEVERITY_EXCLUSION );

    // Pick a displayed, non-excluded marker to exclude.
    SCH_MARKER* marker = nullptr;
    SEVERITY    markerSeverity = RPT_SEVERITY_UNDEFINED;

    for( int i = 0; i < provider.GetCount(); ++i )
    {
        SCH_MARKER* candidate = static_cast<SCH_MARKER*>( provider.GetItem( i )->GetParent() );

        if( !candidate->IsExcluded() )
        {
            marker = candidate;
            markerSeverity = settings.GetSeverity( candidate->GetRCItem()->GetErrorCode() );
            break;
        }
    }

    BOOST_REQUIRE( marker != nullptr );
    BOOST_REQUIRE( markerSeverity == RPT_SEVERITY_ERROR || markerSeverity == RPT_SEVERITY_WARNING );

    const auto policyBefore = SCH_ERC_SETTINGS::Capture( *m_schematic );
    const auto storedBefore = settings.CaptureCurrentState();
    const auto revisionBefore = m_schematic->ChangeJournal().Sequence();
    BOOST_CHECK_EQUAL( policyBefore.pin_map_size(), ELECTRICAL_PINTYPES_TOTAL * ELECTRICAL_PINTYPES_TOTAL );
    BOOST_CHECK( policyBefore.rule_severities_size() > 0 );

    BOOST_CHECK( provider.SetMarkerExcluded( marker, true ) );
    BOOST_CHECK( !provider.SetMarkerExcluded( marker, true ) );

    // A comment-only change is persisted but must not move severity counts.
    BOOST_CHECK( provider.SetMarkerExcluded( marker, true, wxS( "Intentional fixture exclusion" ) ) );
    BOOST_CHECK( !provider.SetMarkerExcluded( marker, true, wxS( "Intentional fixture exclusion" ) ) );
    BOOST_CHECK_EQUAL( marker->GetComment(), wxS( "Intentional fixture exclusion" ) );

    const auto markerBeforeClone = ERC_EXCLUSION::FromMarker( *marker ).ToProto().SerializeAsString();
    {
        std::unique_ptr<SCH_MARKER> copy( static_cast<SCH_MARKER*>( marker->Clone() ) );
        BOOST_CHECK( copy->GetRCItem() != marker->GetRCItem() );
        BOOST_CHECK( marker->GetRCItem()->GetParent() == marker );
        BOOST_CHECK( copy->GetRCItem()->GetParent() == copy.get() );
        BOOST_CHECK_EQUAL( ERC_EXCLUSION::FromMarker( *copy ).ToProto().SerializeAsString(), markerBeforeClone );
    }
    BOOST_CHECK( marker->GetRCItem()->GetParent() == marker );
    BOOST_CHECK_EQUAL( ERC_EXCLUSION::FromMarker( *marker ).ToProto().SerializeAsString(), markerBeforeClone );

    const auto captured = SCH_ERC_SETTINGS::Capture( *m_schematic );
    const auto key = ERC_EXCLUSION::FromMarker( *marker ).GetSortKey();
    int matches = 0;
    for( const auto& exclusion : captured.exclusions() )
    {
        if( exclusion.marker().SerializeAsString() == key )
        {
            ++matches;
            BOOST_CHECK_EQUAL( exclusion.comment(), "Intentional fixture exclusion" );
        }
    }
    BOOST_CHECK_EQUAL( matches, 1 );
    SCH_ERC_SETTINGS::PREPARED prepared;
    std::string failure;
    BOOST_REQUIRE_MESSAGE( SCH_ERC_SETTINGS::Prepare( captured, *m_schematic, prepared, failure ), failure );
    BOOST_CHECK_EQUAL( prepared.canonical.SerializeAsString(), captured.SerializeAsString() );
    BOOST_REQUIRE_EQUAL( prepared.exclusions.size(), captured.exclusions_size() );
    auto reordered = captured;
    std::reverse( reordered.mutable_rule_severities()->begin(), reordered.mutable_rule_severities()->end() );
    std::reverse( reordered.mutable_pin_map()->begin(), reordered.mutable_pin_map()->end() );
    BOOST_REQUIRE_MESSAGE( SCH_ERC_SETTINGS::Prepare( reordered, *m_schematic, prepared, failure ), failure );
    BOOST_CHECK_EQUAL( prepared.canonical.SerializeAsString(), captured.SerializeAsString() );
    BOOST_CHECK( settings.CaptureCurrentState() == storedBefore );
    BOOST_CHECK_EQUAL( m_schematic->ChangeJournal().Sequence(), revisionBefore );

    // The exclusion bucket gains one; the marker's original bucket loses one. Total is unchanged.
    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_EXCLUSION ), exclusionsBefore + 1 );

    if( markerSeverity == RPT_SEVERITY_ERROR )
    {
        BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_ERROR ), errorsBefore - 1 );
        BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_WARNING ), warningsBefore );
    }
    else
    {
        BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_WARNING ), warningsBefore - 1 );
        BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_ERROR ), errorsBefore );
    }

    // The incrementally maintained counts must match a fresh authoritative recompute.
    SHEETLIST_ERC_ITEMS_PROVIDER fresh( m_schematic.get() );
    fresh.SetSeverities( allSeverities );

    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_ERROR ), fresh.GetCount( RPT_SEVERITY_ERROR ) );
    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_WARNING ),
                       fresh.GetCount( RPT_SEVERITY_WARNING ) );
    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_EXCLUSION ),
                       fresh.GetCount( RPT_SEVERITY_EXCLUSION ) );

    // Restoring the marker must move it back and keep counts consistent with a recompute.
    BOOST_CHECK( provider.SetMarkerExcluded( marker, false ) );
    BOOST_CHECK( !provider.SetMarkerExcluded( marker, false ) );
    BOOST_CHECK_EQUAL( SCH_ERC_SETTINGS::Capture( *m_schematic ).SerializeAsString(),
                       policyBefore.SerializeAsString() );

    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_ERROR ), errorsBefore );
    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_WARNING ), warningsBefore );
    BOOST_CHECK_EQUAL( provider.GetCount( RPT_SEVERITY_EXCLUSION ), exclusionsBefore );
}


BOOST_FIXTURE_TEST_CASE( ERCSeverityReportsStoredChangesOnly, ERC_MARKER_COUNT_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, "issue10430", m_schematic );
    ERC_SETTINGS& settings = m_schematic->ErcSettings();
    const auto original = settings.m_ERCSeverities;
    const auto previous = settings.GetSeverity( ERCE_PIN_NOT_CONNECTED );
    const auto changed = previous == RPT_SEVERITY_WARNING ? RPT_SEVERITY_ERROR : RPT_SEVERITY_WARNING;

    BOOST_CHECK( !settings.SetSeverity( ERCE_PIN_NOT_CONNECTED, previous ) );
    BOOST_CHECK( settings.m_ERCSeverities == original );
    BOOST_CHECK( settings.SetSeverity( ERCE_PIN_NOT_CONNECTED, changed ) );
    BOOST_CHECK( !settings.SetSeverity( ERCE_PIN_NOT_CONNECTED, changed ) );
    BOOST_CHECK_EQUAL( settings.GetSeverity( ERCE_PIN_NOT_CONNECTED ), changed );
    BOOST_CHECK( settings.SetSeverity( ERCE_PIN_NOT_CONNECTED, previous ) );
    BOOST_CHECK( settings.m_ERCSeverities == original );
}


BOOST_FIXTURE_TEST_CASE( ERCReplacementRejectsIncompletePolicyWithoutChangingLiveState, ERC_MARKER_COUNT_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, "issue10430", m_schematic );
    const auto before = SCH_ERC_SETTINGS::Capture( *m_schematic );
    const auto stored = m_schematic->ErcSettings().CaptureCurrentState();
    const auto revision = m_schematic->ChangeJournal().Sequence();
    for( int problem = 0; problem < 5; ++problem )
    {
        auto invalid = before;
        if( problem == 0 ) invalid.mutable_rule_severities()->RemoveLast();
        if( problem == 1 ) *invalid.add_rule_severities() = invalid.rule_severities( 0 );
        if( problem == 2 ) invalid.mutable_pin_map()->RemoveLast();
        if( problem == 3 ) *invalid.mutable_pin_map( 0 ) = invalid.pin_map( 1 );
        if( problem == 4 ) invalid.mutable_rule_severities( 0 )->set_severity( kiapi::common::types::RS_EXCLUSION );
        SCH_ERC_SETTINGS::PREPARED prepared;
        std::string failure;
        BOOST_CHECK( !SCH_ERC_SETTINGS::Prepare( invalid, *m_schematic, prepared, failure ) );
        BOOST_CHECK( !failure.empty() );
        BOOST_CHECK_EQUAL( SCH_ERC_SETTINGS::Capture( *m_schematic ).SerializeAsString(), before.SerializeAsString() );
        BOOST_CHECK( m_schematic->ErcSettings().CaptureCurrentState() == stored );
        BOOST_CHECK_EQUAL( m_schematic->ChangeJournal().Sequence(), revision );
    }
}
