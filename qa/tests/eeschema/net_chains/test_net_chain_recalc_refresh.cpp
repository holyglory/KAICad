/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.TXT for contributors.
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

#include <boost/test/unit_test.hpp>
#include <locale>
#include <sstream>

#include <qa_utils/wx_utils/unit_test_utils.h>
#include <schematic_utils/schematic_file_util.h>

#include <connection_graph.h>
#include <schematic.h>
#include <sch_netchain.h>
#include <sch_sheet.h>
#include <sch_screen.h>
#include <sch_symbol.h>
#include <sch_pin.h>
#include <sch_label.h>
#include <sch_line.h>
#include <settings/settings_manager.h>
#include <locale_io.h>
#include <richio.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <project/net_settings.h>
#include <settings/json_settings_internals.h>


// Regression for [H-1]. CONNECTION_GRAPH::Reset() clears every committed chain's
// non-owning symbol pointer set to drop stale SCH_SYMBOL references before the rest
// of the graph is rebuilt.  RebuildNetChains() then iterates the persisted override
// maps and used to skip any name that was already in m_committedNetChains, leaving
// the chain with an empty m_symbols and stale derived state.  Downstream consumers
// (netlist export, the setup panel, the tuner cache) trusted those caches.
//
// The fix refreshes the committed chain in place during the rebuild restore pass
// rather than skipping it.  This test exercises the full Recalculate(unconditional)
// cycle and asserts the committed chain still has populated m_symbols and m_nets
// afterwards.
struct NETCHAIN_RECALC_REFRESH_FIXTURE
{
    NETCHAIN_RECALC_REFRESH_FIXTURE() : m_settingsManager() {}

    SETTINGS_MANAGER           m_settingsManager;
    std::unique_ptr<SCHEMATIC> m_schematic;
};


BOOST_AUTO_TEST_CASE( NetChain_UnassignedClassesPersistAndCompare )
{
    NET_SETTINGS source( nullptr, "" );
    NET_SETTINGS target( nullptr, "" );
    source.SetNetChainClass( "CHAIN", "Used" );
    source.SetNetChainClassDefinitions( { "Used", "Unused" } );
    BOOST_CHECK( source != target );
    BOOST_REQUIRE( source.Store() );
    auto json = nlohmann::json::parse( source.FormatAsString() );
    BOOST_REQUIRE( json["net_chain_class_definitions"].is_array() );
    target.SetNetChainClassDefinitions( { "Stale" } );
    JSON_SETTINGS_INTERNALS parsed;
    static_cast<nlohmann::json&>( parsed ) = json;
    target.Internals()->CloneFrom( parsed ); target.Load();
    BOOST_CHECK( target.GetNetChainClassDefinitions() == source.GetNetChainClassDefinitions() );
    BOOST_CHECK( target.GetNetChainClasses() == source.GetNetChainClasses() );
    BOOST_CHECK( target == source );
    target.SetNetChainClassDefinitions( { "Used" } );
    BOOST_CHECK( target != source );
    target.CopyFrom( source ); BOOST_CHECK( target == source );

    // Legacy assignment-only projects still expose Used, without retaining a
    // previously loaded explicit class from another project.
    json.erase( "net_chain_class_definitions" );
    static_cast<nlohmann::json&>( parsed ) = json;
    target.Internals()->CloneFrom( parsed ); target.Load();
    BOOST_CHECK( target.GetNetChainClassDefinitions() == std::set<wxString>{ "Used" } );
    json["net_chain_classes"] = nlohmann::json::object();
    json["net_chain_class_definitions"] = nlohmann::json::array();
    static_cast<nlohmann::json&>( parsed ) = json;
    target.Internals()->CloneFrom( parsed ); target.Load();
    BOOST_CHECK( target.GetNetChainClassDefinitions().empty() );
    BOOST_CHECK( target.GetNetChainClasses().empty() );
}


BOOST_FIXTURE_TEST_CASE( NetChain_RenamePreservesUnresolvedDeclaration,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );
    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, true );
    BOOST_REQUIRE( !graph->GetPotentialNetChains().empty() );
    SCH_NETCHAIN* chain = graph->CreateNetChainFromPotential(
            graph->GetPotentialNetChains().front().get(), "OWNED" );
    BOOST_REQUIRE( chain );
    auto overrides = graph->GetNetChainNetClassOverrides();
    overrides["UNRESOLVED"] = "PreserveRequirement";
    graph->SetNetChainNetClassOverrides( overrides );
    const auto before = graph->GetNetChainDefinitions();
    BOOST_CHECK( !graph->GetNetChainByName( "UNRESOLVED" ) );
    BOOST_CHECK( !graph->RenameCommittedNetChain( "OWNED", "UNRESOLVED" ) );
    BOOST_CHECK( graph->GetNetChainDefinitions() == before );
    for( SCH_SYMBOL* symbol : chain->GetSymbols() )
        BOOST_CHECK_EQUAL( symbol->GetNetChainName(), "OWNED" );
    BOOST_REQUIRE( graph->RenameCommittedNetChain( "OWNED", "RENAMED" ) );
    BOOST_CHECK( graph->GetNetChainDefinitions().at( "UNRESOLVED" ) == before.at( "UNRESOLVED" ) );
}


BOOST_FIXTURE_TEST_CASE( NetChain_RestoreKeepsDeclaredTerminalOrder,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );
    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, true );
    BOOST_REQUIRE( !graph->GetPotentialNetChains().empty() );
    SCH_NETCHAIN* chain = graph->CreateNetChainFromPotential(
            graph->GetPotentialNetChains().front().get(), "ORDERED" );
    BOOST_REQUIRE( chain );
    const KIID pinA = chain->GetTerminalPinB();
    const KIID pinB = chain->GetTerminalPinA();
    auto definitions = graph->GetNetChainDefinitions();
    auto& expected = definitions.at( "ORDERED" );
    std::swap( expected.terminals.first, expected.terminals.second );
    graph->SetNetChainDefinitions( definitions );
    for( int pass = 0; pass < 2; ++pass )
    {
        SCH_NETCHAIN* restored = graph->GetNetChainByName( "ORDERED" );
        BOOST_REQUIRE( restored );
        BOOST_CHECK( graph->GetNetChainDefinitions().at( "ORDERED" ) == expected );
        BOOST_CHECK( restored->GetTerminalPinA() == pinA );
        BOOST_CHECK( restored->GetTerminalPinB() == pinB );
        graph->Recalculate( sheets, true );
    }
}


BOOST_FIXTURE_TEST_CASE( NetChain_ExcludedMembersCannotReturnThroughRestore,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO locale;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );
    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    auto sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, true );
    BOOST_REQUIRE( !graph->GetPotentialNetChains().empty() );
    BOOST_REQUIRE( graph->CreateNetChainFromPotential( graph->GetPotentialNetChains().front().get(), "RESTRICTED" ) );
    const auto baseline = graph->GetNetChainDefinitions();
    const auto original = baseline.at( "RESTRICTED" );
    BOOST_REQUIRE_EQUAL( original.memberNets.size(), 4u );
    for( const wxString& net : original.memberNets )
    {
        auto desired = baseline;
        desired["RESTRICTED"].memberNets.erase( net );
        desired["RESTRICTED"].excludedNets.insert( net );
        graph->SetNetChainDefinitions( desired );
        for( int pass = 0; pass < 2; ++pass )
        {
            BOOST_CHECK( !graph->GetNetChainForNet( net ) );
            const auto observed = graph->GetNetChainDefinitions().at( "RESTRICTED" );
            BOOST_CHECK( !observed.committed );
            BOOST_CHECK( observed.terminals == original.terminals );
            BOOST_CHECK( observed.memberNets == desired.at( "RESTRICTED" ).memberNets );
            BOOST_CHECK( observed.excludedNets == desired.at( "RESTRICTED" ).excludedNets );
            graph->Recalculate( sheets, true );
        }
        graph->SetNetChainDefinitions( baseline );
        BOOST_CHECK( graph->GetNetChainDefinitions() == baseline );
    }

    // Exact pin ownership survives a real label change. Name-only matching
    // would allow this renamed removed net back into the inferred path.
    SCH_PIN* selected = nullptr;
    SCH_SHEET_PATH selectedPath;
    const KIID selectedId = graph->GetNetChainByName( "RESTRICTED" )->GetTerminalPinA();
    for( const auto& path : sheets )
        for( SCH_ITEM* item : path.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
            for( SCH_PIN* pin : static_cast<SCH_SYMBOL*>( item )->GetPins( &path ) )
                if( pin->m_Uuid == selectedId ) { selected = pin; selectedPath = path; }
    BOOST_REQUIRE( selected );
    const wxString oldName = selected->Connection( &selectedPath )->Name();
    auto anchored = baseline;
    anchored["RESTRICTED"].memberNets.erase( oldName );
    anchored["RESTRICTED"].excludedNets.insert( oldName );
    anchored["RESTRICTED"].excludedPins.emplace( selectedPath.Path(), selectedId );
    graph->SetNetChainDefinitions( anchored );
    BOOST_CHECK( !graph->GetNetChainByName( "RESTRICTED" ) );
    selectedPath.LastScreen()->Append( new SCH_LABEL( selected->GetPosition(), "RENAMED_REMOVED_NET" ) );
    graph->Recalculate( sheets, true );
    BOOST_CHECK( selected->Connection( &selectedPath )->Name() != oldName );
    BOOST_CHECK( !graph->GetNetChainByName( "RESTRICTED" ) );
    BOOST_CHECK( graph->GetNetChainDefinitions().at( "RESTRICTED" ).excludedPins == anchored.at( "RESTRICTED" ).excludedPins );

    // Round-trip the real native writer/parser, including the anchor path.
    // The stream helper must carry the same root-owned restriction maps as
    // the editor loader, not silently drop a newly supported field.
    STRING_FORMATTER serialized;
    SCH_IO_KICAD_SEXPR writer;
    writer.FormatSchematicToFormatter( &serialized, m_schematic->GetTopLevelSheet( 0 ), m_schematic.get() );
    std::istringstream input( serialized.GetString() );
    auto reloaded = KI_TEST::ReadSchematicFromStream( input, &m_schematic->Project() );
    BOOST_REQUIRE( reloaded );
    auto* reloadedGraph = reloaded->ConnectionGraph();
    reloadedGraph->Recalculate( reloaded->BuildSheetListSortedByPageNumbers(), true );
    BOOST_CHECK( !reloadedGraph->GetNetChainByName( "RESTRICTED" ) );
    BOOST_CHECK( reloadedGraph->GetNetChainDefinitions().at( "RESTRICTED" ).excludedPins
                 == anchored.at( "RESTRICTED" ).excludedPins );

    // Split the removed electrical owner, then merge its anchored side into
    // the opposite endpoint. Neither operation may revive removed membership.
    SCH_LINE* attachedWire = nullptr;
    for( SCH_ITEM* item : selectedPath.LastScreen()->Items().OfType( SCH_LINE_T ) )
    {
        auto* line = static_cast<SCH_LINE*>( item );
        if( line->GetLayer() == LAYER_WIRE && ( line->GetStartPoint() == selected->GetPosition()
                || line->GetEndPoint() == selected->GetPosition() ) )
        { attachedWire = line; break; }
    }
    BOOST_REQUIRE( attachedWire );
    const VECTOR2I gap( 0, schIUScale.MilsToIU( 100 ) );
    if( attachedWire->GetStartPoint() == selected->GetPosition() )
        attachedWire->SetStartPoint( attachedWire->GetStartPoint() + gap );
    else
        attachedWire->SetEndPoint( attachedWire->GetEndPoint() + gap );
    selectedPath.LastScreen()->Update( attachedWire, false );
    graph->Recalculate( sheets, true );
    BOOST_CHECK( !graph->GetNetChainByName( "RESTRICTED" ) );
    SCH_PIN* other = nullptr;
    for( SCH_ITEM* item : selectedPath.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
    {
        auto* symbol = static_cast<SCH_SYMBOL*>( item );
        for( SCH_PIN* pin : symbol->GetPins( &selectedPath ) )
            if( symbol->GetRef( &selectedPath ) == original.terminals.second.ref
                && pin->GetNumber() == original.terminals.second.pin ) other = pin;
    }
    BOOST_REQUIRE( other && other != selected );
    auto* merged = new SCH_LINE( selected->GetPosition(), LAYER_WIRE );
    merged->SetEndPoint( other->GetPosition() ); selectedPath.LastScreen()->Append( merged );
    graph->Recalculate( sheets, true );
    BOOST_CHECK( selected->Connection( &selectedPath )->Name() == other->Connection( &selectedPath )->Name() );
    BOOST_CHECK( !graph->GetNetChainByName( "RESTRICTED" ) );
    BOOST_CHECK( graph->GetNetChainDefinitions().at( "RESTRICTED" ).excludedPins == anchored.at( "RESTRICTED" ).excludedPins );

    // An unmatched path is unresolved, not an invitation to find a similarly
    // named pin elsewhere. Intent remains present through subsequent rebuilds.
    auto missing = anchored;
    KIID_PATH missingPath; missingPath.push_back( KIID() );
    missing["RESTRICTED"].excludedPins.clear();
    missing["RESTRICTED"].excludedPins.emplace( missingPath, selectedId );
    graph->SetNetChainDefinitions( missing );
    BOOST_CHECK( !graph->GetNetChainByName( "RESTRICTED" ) );
    BOOST_CHECK( graph->GetNetChainDefinitions().at( "RESTRICTED" ).terminals == original.terminals );
}


BOOST_FIXTURE_TEST_CASE( NetChain_ExcludedPinPathIsolatesRepeatedScreens,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO locale;
    KI_TEST::LoadSchematic( m_settingsManager, "net_chains_four_nets", m_schematic );
    SCH_SHEET* original = m_schematic->GetTopLevelSheet( 0 );
    SCH_SCREEN* shared = original->GetScreen();
    auto* parent = new SCH_SHEET( m_schematic.get() );
    parent->SetScreen( new SCH_SCREEN( m_schematic.get() ) );
    parent->SetName( "Parent" ); parent->SetFileName( "parent.kicad_sch" );
    m_schematic->AddTopLevelSheet( parent );
    BOOST_REQUIRE( m_schematic->RemoveTopLevelSheet( original ) );
    original->SetName( "First" ); parent->GetScreen()->Append( original );
    auto* repeat = new SCH_SHEET( m_schematic.get() );
    repeat->SetName( "Second" ); repeat->SetFileName( original->GetFileName() );
    repeat->SetScreen( shared ); parent->GetScreen()->Append( repeat );
    m_schematic->RefreshHierarchy();
    std::vector<SCH_SHEET_PATH> instances;
    for( const auto& path : m_schematic->Hierarchy() )
        if( path.LastScreen() == shared ) instances.push_back( path );
    BOOST_REQUIRE_EQUAL( instances.size(), 2u );
    std::set<SCH_SYMBOL*> symbols;
    for( SCH_ITEM* item : shared->Items().OfType( SCH_SYMBOL_T ) )
        symbols.insert( static_cast<SCH_SYMBOL*>( item ) );
    SCH_SYMBOL* firstEndpoint = nullptr;
    SCH_SYMBOL* secondEndpoint = nullptr;
    int number = 1;
    for( SCH_SYMBOL* symbol : symbols )
    {
        for( int instance = 0; instance < 2; ++instance )
        {
            wxString ref = wxString::Format( "U%d", number + instance * 100 );
            symbol->SetRef( &instances[instance], ref );
        }
        if( symbol->GetPins( &instances[0] ).size() == 1 )
        {
            if( !firstEndpoint ) firstEndpoint = symbol;
            else secondEndpoint = symbol;
        }
        ++number;
    }
    BOOST_REQUIRE( firstEndpoint && secondEndpoint );
    auto* graph = m_schematic->ConnectionGraph();
    graph->SetNetChainDefinitions( {} );
    graph->Recalculate( m_schematic->Hierarchy(), true );
    std::vector<std::set<wxString>> nets( 2 );
    for( int index = 0; index < 2; ++index )
    {
        for( SCH_SYMBOL* symbol : symbols )
            for( SCH_PIN* pin : symbol->GetPins( &instances[index] ) )
                nets[index].insert( pin->Connection( &instances[index] )->Name() );
        BOOST_REQUIRE_EQUAL( nets[index].size(), 4u );
        auto* from = firstEndpoint->GetPins( &instances[index] ).front();
        auto* to = secondEndpoint->GetPins( &instances[index] ).front();
        BOOST_REQUIRE( graph->CreateManualNetChain( index == 0 ? "FirstChain" : "SecondChain", symbols, nets[index],
            from->m_Uuid, to->m_Uuid, firstEndpoint->GetRef( &instances[index] ), from->GetNumber(),
            secondEndpoint->GetRef( &instances[index] ), to->GetNumber() ) );
    }
    BOOST_CHECK( nets[0] != nets[1] );
    const auto baseline = graph->GetNetChainDefinitions();
    auto desired = baseline;
    SCH_PIN* samePhysicalPin = firstEndpoint->GetPins( &instances[0] ).front();
    wxString removed = samePhysicalPin->Connection( &instances[0] )->Name();
    desired["FirstChain"].memberNets.erase( removed );
    desired["FirstChain"].excludedNets.insert( removed );
    desired["FirstChain"].excludedPins.emplace( instances[0].Path(), samePhysicalPin->m_Uuid );
    graph->SetNetChainDefinitions( desired );
    for( int pass = 0; pass < 2; ++pass )
    {
        BOOST_CHECK( !graph->GetNetChainByName( "FirstChain" ) );
        BOOST_REQUIRE( graph->GetNetChainByName( "SecondChain" ) );
        BOOST_CHECK( graph->GetNetChainDefinitions().at( "SecondChain" ) == baseline.at( "SecondChain" ) );
        BOOST_CHECK( graph->GetNetChainDefinitions().at( "FirstChain" ).excludedPins == desired.at( "FirstChain" ).excludedPins );
        graph->Recalculate( m_schematic->Hierarchy(), true );
    }
}


BOOST_FIXTURE_TEST_CASE( NetChain_OpacityWriterPreservesDoublePrecision,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO locale;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );
    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    graph->Recalculate( m_schematic->BuildSheetListSortedByPageNumbers(), true );
    BOOST_REQUIRE( !graph->GetPotentialNetChains().empty() );
    SCH_NETCHAIN* chain = graph->CreateNetChainFromPotential(
            graph->GetPotentialNetChains().front().get(), "PRECISION" );
    BOOST_REQUIRE( chain );
    for( double alpha : { 0.0, 1.0, 0.5, 128.0 / 255.0, 0.12345678901234567, 1e-30, 1e-200 } )
    {
        chain->SetColor( KIGFX::COLOR4D( 51.0 / 255, 102.0 / 255, 153.0 / 255, alpha ) );
        STRING_FORMATTER formatter;
        SCH_IO_KICAD_SEXPR writer;
        writer.FormatSchematicToFormatter( &formatter, m_schematic->GetTopLevelSheet( 0 ), m_schematic.get() );
        const std::string& text = formatter.GetString();
        auto declaration = text.find( "(net_chain \"PRECISION\"" );
        BOOST_REQUIRE( declaration != std::string::npos );
        const std::string prefix = " (color 51 102 153 ";
        auto color = text.find( prefix, declaration );
        BOOST_REQUIRE( color != std::string::npos );
        auto start = color + prefix.size();
        auto end = text.find( ')', start );
        BOOST_REQUIRE( end != std::string::npos );
        double parsed = -1;
        // Apple libc++ in the supported toolchain does not provide floating
        // from_chars. Preserve locale-independent, full-token parsing and the
        // exact-double assertion without dropping the precision regression.
        std::istringstream value( text.substr( start, end - start ) );
        value.imbue( std::locale::classic() );
        value >> std::noskipws >> parsed;
        BOOST_REQUIRE( !value.fail() && value.eof() );
        BOOST_CHECK_EQUAL( parsed, alpha );
    }
}


BOOST_FIXTURE_TEST_CASE( NetChain_RefreshCommittedChainAcrossUnconditionalRecalc,
                         NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );

    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    BOOST_REQUIRE( graph );

    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    const auto& potentials = graph->GetPotentialNetChains();
    BOOST_REQUIRE( !potentials.empty() );

    SCH_NETCHAIN* potential = potentials.front().get();
    BOOST_REQUIRE( potential );

    const std::set<wxString> originalNets    = potential->GetNets();
    const std::size_t        originalNetCnt  = originalNets.size();
    const std::size_t        originalSymCnt  = potential->GetSymbols().size();

    BOOST_REQUIRE_GT( originalNetCnt, 0u );
    BOOST_REQUIRE_GT( originalSymCnt, 0u );

    SCH_NETCHAIN* committed = graph->CreateNetChainFromPotential( potential, wxT( "REFRESH_TEST" ) );
    BOOST_REQUIRE( committed );
    BOOST_REQUIRE_EQUAL( committed->GetNets().size(), originalNetCnt );
    BOOST_REQUIRE_EQUAL( committed->GetSymbols().size(), originalSymCnt );

    // The hazard.  Recalculate(true) -> Reset() clears m_symbols on every committed chain,
    // and the rebuild restore pass used to skip names already present in
    // m_committedNetChains, leaving the chain permanently empty.
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    SCH_NETCHAIN* refreshed = graph->GetNetChainByName( wxT( "REFRESH_TEST" ) );
    BOOST_REQUIRE_MESSAGE( refreshed,
                           "Committed chain disappeared across unconditional Recalculate" );

    BOOST_CHECK_MESSAGE( !refreshed->GetSymbols().empty(),
                         "Committed chain has empty m_symbols after unconditional Recalculate; "
                         "Reset() cleared the cache and RebuildNetChains() failed to refresh it" );

    BOOST_CHECK_MESSAGE( !refreshed->GetNets().empty(),
                         "Committed chain has empty m_nets after unconditional Recalculate" );

    BOOST_CHECK_EQUAL( refreshed->GetNets().size(), originalNetCnt );
    BOOST_CHECK_EQUAL( refreshed->GetSymbols().size(), originalSymCnt );

    // Terminal pin/ref data must also survive the round trip; the setup panel and the PCB
    // tuner walk these to find the bookend pads.
    BOOST_CHECK( !refreshed->GetTerminalRef( 0 ).IsEmpty() );
    BOOST_CHECK( !refreshed->GetTerminalRef( 1 ).IsEmpty() );

    // A second round trip must remain stable (no slow leak of derived state).
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    SCH_NETCHAIN* twice = graph->GetNetChainByName( wxT( "REFRESH_TEST" ) );
    BOOST_REQUIRE( twice );
    BOOST_CHECK( !twice->GetSymbols().empty() );
    BOOST_CHECK( !twice->GetNets().empty() );
    BOOST_CHECK_EQUAL( twice->GetNets().size(), originalNetCnt );
    BOOST_CHECK_EQUAL( twice->GetSymbols().size(), originalSymCnt );
}


// Companion check.  User-set netclass and color overrides live on the SCH_NETCHAIN itself
// (not in the override map) once the chain is committed.  The in-place refresh must NOT
// reset them.
BOOST_FIXTURE_TEST_CASE( NetChain_RefreshPreservesOverridesOnCommittedChain,
                         NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );

    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    BOOST_REQUIRE( graph );

    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    const auto& potentials = graph->GetPotentialNetChains();
    BOOST_REQUIRE( !potentials.empty() );

    SCH_NETCHAIN* committed = graph->CreateNetChainFromPotential( potentials.front().get(),
                                                                  wxT( "OVERRIDE_TEST" ) );
    BOOST_REQUIRE( committed );

    committed->SetNetClass( wxT( "DDR_DATA" ) );
    committed->SetColor( KIGFX::COLOR4D( 1.0, 0.5, 0.25, 1.0 ) );

    graph->Recalculate( sheets, /*aUnconditional=*/true );

    SCH_NETCHAIN* refreshed = graph->GetNetChainByName( wxT( "OVERRIDE_TEST" ) );
    BOOST_REQUIRE( refreshed );

    BOOST_CHECK_EQUAL( refreshed->GetNetClass(), wxT( "DDR_DATA" ) );
    BOOST_CHECK( refreshed->GetColor() != KIGFX::COLOR4D::UNSPECIFIED );
    BOOST_CHECK_CLOSE( refreshed->GetColor().r, 1.0, 1e-6 );
    BOOST_CHECK_CLOSE( refreshed->GetColor().g, 0.5, 1e-6 );
    BOOST_CHECK_CLOSE( refreshed->GetColor().b, 0.25, 1e-6 );
}


// Regression for [H-2].  ReplaceNetChainTerminalPin writes into m_netChainTerminalOverrides,
// and the legacy pass-4 restore loop reapplies that override to potential chains by name.
// The new in-place refresh helpers on the committed-chain path used to copy the source
// chain's terminal pins unconditionally, silently undoing the user's "Replace terminal pin"
// action across an unconditional Recalculate.  The fix consults the override map first.
BOOST_FIXTURE_TEST_CASE( NetChain_RefreshPreservesTerminalPinOverride,
                         NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO dummy;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );

    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    BOOST_REQUIRE( graph );

    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    const auto& potentials = graph->GetPotentialNetChains();
    BOOST_REQUIRE( !potentials.empty() );

    SCH_NETCHAIN* potential = potentials.front().get();
    BOOST_REQUIRE( potential );

    SCH_NETCHAIN* committed = graph->CreateNetChainFromPotential( potential, wxT( "TERM_OVERRIDE" ) );
    BOOST_REQUIRE( committed );

    const KIID originalPinA = committed->GetTerminalPinA();
    const KIID originalPinB = committed->GetTerminalPinB();
    BOOST_REQUIRE( originalPinA != niluuid );
    BOOST_REQUIRE( originalPinB != niluuid );

    // Synthesize a fresh KIID and retarget the A endpoint as if the user had selected
    // "Replace terminal pin" on a different schematic pin.  We don't need the new id to
    // resolve to a real pin in the schematic; the override map only stores the raw KIIDs.
    const KIID newPinA;
    BOOST_REQUIRE( newPinA != originalPinA );

    graph->ReplaceNetChainTerminalPin( wxT( "TERM_OVERRIDE" ), originalPinA, newPinA );

    BOOST_REQUIRE_EQUAL( committed->GetTerminalPinA().AsString(), newPinA.AsString() );
    BOOST_REQUIRE_EQUAL( committed->GetTerminalPinB().AsString(), originalPinB.AsString() );

    graph->Recalculate( sheets, /*aUnconditional=*/true );

    SCH_NETCHAIN* refreshed = graph->GetNetChainByName( wxT( "TERM_OVERRIDE" ) );
    BOOST_REQUIRE( refreshed );

    BOOST_CHECK_MESSAGE( refreshed->GetTerminalPinA() == newPinA,
                         "Refresh helper overwrote the user's terminal-pin override on pin A" );
    BOOST_CHECK_MESSAGE( refreshed->GetTerminalPinB() == originalPinB,
                         "Refresh helper changed the unaffected terminal pin B" );

    // Stability across a second round trip.
    graph->Recalculate( sheets, /*aUnconditional=*/true );

    SCH_NETCHAIN* twice = graph->GetNetChainByName( wxT( "TERM_OVERRIDE" ) );
    BOOST_REQUIRE( twice );
    BOOST_CHECK( twice->GetTerminalPinA() == newPinA );
    BOOST_CHECK( twice->GetTerminalPinB() == originalPinB );
}

BOOST_FIXTURE_TEST_CASE( NetChain_SetTerminalPersistsExactSelectedPin,
                        NETCHAIN_RECALC_REFRESH_FIXTURE )
{
    LOCALE_IO locale;
    KI_TEST::LoadSchematic( m_settingsManager, wxString( "net_chains_four_nets" ), m_schematic );
    CONNECTION_GRAPH* graph = m_schematic->ConnectionGraph();
    BOOST_REQUIRE( graph );
    SCH_SHEET_LIST sheets = m_schematic->BuildSheetListSortedByPageNumbers();
    graph->Recalculate( sheets, true );
    BOOST_REQUIRE( !graph->GetPotentialNetChains().empty() );
    SCH_NETCHAIN* chain = graph->CreateNetChainFromPotential( graph->GetPotentialNetChains().front().get(), "REAL_TERMINAL" );
    BOOST_REQUIRE( chain );
    const KIID originalA = chain->GetTerminalPinA(), originalB = chain->GetTerminalPinB();
    const wxString oldRef = chain->GetTerminalRef( 0 ), oldNumber = chain->GetTerminalPinNum( 0 );
    const wxString otherRef = chain->GetTerminalRef( 1 ), otherNumber = chain->GetTerminalPinNum( 1 );
    SCH_PIN* replacement = nullptr;
    SCH_SHEET_PATH replacementPath;
    for( const SCH_SHEET_PATH& path : sheets )
    {
        for( SCH_ITEM* item : path.LastScreen()->Items() )
        {
            auto* symbol = dynamic_cast<SCH_SYMBOL*>( item );
            if( !symbol ) continue;
            for( SCH_PIN* pin : symbol->GetPins() )
            {
                auto* connection = pin->Connection( &path );
                if( !replacement && pin->m_Uuid != originalA && pin->m_Uuid != originalB
                        && connection && graph->GetNetChainForNet( connection->Name() ) == chain )
                {
                    replacement = pin;
                    replacementPath = path;
                }
            }
        }
    }
    BOOST_REQUIRE( replacement );
    SCH_NETCHAIN_TERMINAL_CHANGE change;
    change.chain = "REAL_TERMINAL";
    change.terminal = 0;
    change.expectedPin = originalA;
    change.expectedReference = oldRef;
    change.expectedNumber = oldNumber;
    change.selectedPin = replacement->m_Uuid;
    change.selectedPath = replacementPath.Path();
    auto bad = change; bad.terminal = 2;
    BOOST_CHECK( !graph->SetNetChainTerminal( bad, *replacement, replacementPath ) );
    bad = change; bad.expectedPin = KIID();
    BOOST_CHECK( !graph->SetNetChainTerminal( bad, *replacement, replacementPath ) );
    bad = change; bad.expectedReference = "unrelated";
    BOOST_CHECK( !graph->SetNetChainTerminal( bad, *replacement, replacementPath ) );
    bad = change; bad.selectedPath.push_back( KIID() );
    BOOST_CHECK( !graph->SetNetChainTerminal( bad, *replacement, replacementPath ) );
    bad = change; bad.selectedPin = KIID();
    BOOST_CHECK( !graph->SetNetChainTerminal( bad, *replacement, replacementPath ) );
    BOOST_CHECK( chain->GetTerminalPinA() == originalA );
    BOOST_CHECK( chain->GetTerminalRef( 0 ) == oldRef );

    BOOST_REQUIRE( graph->SetNetChainTerminal( change, *replacement, replacementPath ) );
    const auto definition = graph->GetNetChainDefinitions().at( "REAL_TERMINAL" );
    const wxString newRef = replacement->GetParentSymbol()->GetRef( &replacementPath );
    BOOST_CHECK( definition.terminals.first.ref == newRef );
    BOOST_CHECK( definition.terminals.first.pin == replacement->GetNumber() );
    BOOST_CHECK( definition.terminals.second.ref == otherRef );
    BOOST_CHECK( definition.terminals.second.pin == otherNumber );
    BOOST_CHECK( !graph->SetNetChainTerminal( change, *replacement, replacementPath ) ); // stale
    change.expectedPin = replacement->m_Uuid; change.expectedReference = newRef;
    change.expectedNumber = replacement->GetNumber();
    BOOST_CHECK( !graph->SetNetChainTerminal( change, *replacement, replacementPath ) ); // unchanged
    const KIID newId = replacement->m_Uuid;
    graph->Recalculate( sheets, true );
    chain = graph->GetNetChainByName( "REAL_TERMINAL" );
    BOOST_REQUIRE( chain );
    BOOST_CHECK( chain->GetTerminalPinA() == newId );
    BOOST_CHECK( chain->GetTerminalPinB() == originalB );
    BOOST_CHECK( chain->GetTerminalRef( 0 ) == newRef );
    BOOST_CHECK( graph->GetNetChainDefinitions().at( "REAL_TERMINAL" ).terminals.first.ref == newRef );
}
