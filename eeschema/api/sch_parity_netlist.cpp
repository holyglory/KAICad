/* Non-interactive native schematic comparison input. GPL-3.0-or-later. */
#include "sch_parity_netlist.h"
#include <connection_graph.h>
#include <erc/erc.h>
#include <netlist_exporters/netlist_exporter_kicad.h>
#include <sch_item.h>
#include <sch_reference_list.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <sch_symbol.h>
#include <schematic.h>
#include <set>

std::vector<std::string> FormatSchematicParityNetlist(
        SCHEMATIC& aSchematic, OUTPUTFORMATTER& aOutput,
        bool aAllowDuplicateSheetNames, KIWAY* aKiway )
{
    using STATUS = SCH_PARITY_INPUT_STATUS;
    if( !aSchematic.GetProject() || !aSchematic.RootScreen() || !aSchematic.ConnectionGraph() )
        throw SCH_PARITY_INPUT_ERROR( STATUS::NOT_INITIALIZED,
                                      "Schematic comparison requires an initialized project and connectivity graph" );

    SCH_SHEET_LIST hierarchy = aSchematic.Hierarchy();
    std::set<SCH_SCREEN*> screens;
    std::set<SCH_ITEM*> seen;
    std::vector<SCH_ITEM*> items;
    for( const SCH_SHEET_PATH& path : hierarchy )
    {
        if( !path.LastScreen() )
            throw SCH_PARITY_INPUT_ERROR( STATUS::NOT_INITIALIZED, "A schematic sheet is not loaded" );
        if( screens.insert( path.LastScreen() ).second )
            for( SCH_ITEM* item : path.LastScreen()->Items() ) items.push_back( item );
    }
    bool connectivityPending = false;
    while( !items.empty() )
    {
        SCH_ITEM* item = items.back();
        items.pop_back();
        if( !item || !seen.insert( item ).second ) continue;
        if( item->GetFlags() & ( IN_EDIT | IS_MOVING | IS_NEW ) )
            throw SCH_PARITY_INPUT_ERROR( STATUS::EDIT_IN_PROGRESS,
                                          "Finish or cancel the current schematic edit before comparison" );
        if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( item );
            symbol && ( !symbol->GetLibSymbolRef() || symbol->IsMissingLibSymbol() ) )
            throw SCH_PARITY_INPUT_ERROR( STATUS::MISSING_SYMBOL_DEFINITION,
                                          "Schematic comparison requires every symbol definition: "
                                          + symbol->m_Uuid.AsStdString() );
        connectivityPending |= item->IsConnectable() && item->IsConnectivityDirty();
        item->RunOnChildren( [&]( SCH_ITEM* child ) { items.push_back( child ); }, RECURSE_MODE::NO_RECURSE );
    }

    // Native board netlists exclude power/virtual references; unlike ReadyToNetlist,
    // this path must not automatically assign power-symbol reference numbers.
    SCH_REFERENCE_LIST references;
    hierarchy.GetSymbols( references, SYMBOL_FILTER_NON_POWER );
    std::string annotationIssue;
    const int annotationErrors = references.CheckAnnotation(
            [&]( ERCE_T, const wxString& message, SCH_REFERENCE*, SCH_REFERENCE* )
            { if( annotationIssue.empty() ) annotationIssue = message.ToStdString( wxConvUTF8 ); } );
    if( annotationErrors )
        throw SCH_PARITY_INPUT_ERROR( STATUS::ANNOTATION_REQUIRED,
                                      "Schematic comparison requires valid annotation: " + annotationIssue );

    std::vector<std::string> warnings;
    ERC_TESTER erc( &aSchematic );
    if( erc.TestDuplicateSheetNames( false ) > 0 )
    {
        const std::string warning = "The schematic contains duplicate sheet names";
        if( !aAllowDuplicateSheetNames )
            throw SCH_PARITY_INPUT_ERROR( STATUS::DUPLICATE_SHEET_NAMES, warning );
        warnings.push_back( warning );
    }
    if( connectivityPending )
        throw SCH_PARITY_INPUT_ERROR( STATUS::CONNECTIVITY_PENDING,
                                      "Schematic connectivity must settle before comparison" );

    // The native exporter temporarily switches sheet context while expanding fields.
    // Its normal return restores that context, but an output exception must do so too.
    struct RESTORE_SHEET
    {
        SCHEMATIC& schematic;
        SCH_SHEET_PATH path;
        ~RESTORE_SHEET() { schematic.SetCurrentSheet( path ); }
    } restore{ aSchematic, aSchematic.CurrentSheet() };
    NETLIST_EXPORTER_KICAD exporter( &aSchematic );
    exporter.SetKiway( aKiway );
    exporter.Format( &aOutput, GNL_ALL | GNL_OPT_KICAD );
    return warnings;
}
