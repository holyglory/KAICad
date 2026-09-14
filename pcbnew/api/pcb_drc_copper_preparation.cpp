/* Full native copper preparation on a private DRC input bundle. GPL-3.0-or-later. */
#include "pcb_drc_run_inputs.h"
#include <board.h>
#include <board_commit.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <pcb_generator.h>
#include <progress_reporter.h>
#include <router/pns_routing_settings.h>
#include <teardrop/teardrop.h>
#include <tool/tool_manager.h>
#include <tools/generator_tool.h>
#include <zone.h>
#include <zone_filler.h>
#include <set>
#include <stdexcept>

const PCB_DRC_COPPER_PREPARATION& PCB_DRC_RUN_INPUTS::PrepareCopper( PROGRESS_REPORTER* aReporter )
{
    using STATUS = PCB_DRC_COPPER_PREPARATION::STATUS;
    auto& result = m_copperPreparation;
    if( result.status != STATUS::NOT_RUN ) return result;
    result.status = STATUS::FAILED;

    auto cancelled = [&] { return aReporter && aReporter->IsCancelled(); };
    if( cancelled() )
    {
        result.status = STATUS::CANCELLED;
        return result;
    }
    BOARD& board = GetBoard();
    if( !m_routingSettings )
    {
        result.error = "Copper preparation requires captured native routing settings";
        return result;
    }
    if( !board.GetDesignSettings().m_DRCEngine || !board.GetDesignSettings().m_DRCEngine->RulesValid() )
    {
        result.error = "Copper preparation requires valid captured design rules";
        return result;
    }

    TOOL_MANAGER manager;
    manager.SetEnvironment( &board, nullptr, nullptr, nullptr, nullptr );
    BOARD_COMMIT commit( &manager, true, false );

    try
    {
        auto* generatorTool = new GENERATOR_TOOL( false );
        manager.RegisterTool( generatorTool );
        generatorTool->InitializeSnapshot( std::move( m_routingSettings ) );
        board.BuildConnectivity();
        if( aReporter ) aReporter->Report( "Rebuilding teardrops" );
        TEARDROP_MANAGER teardrops( &board, &manager );
        teardrops.UpdateTeardrops( commit, nullptr, nullptr, true );
        board.IncrementTimeStamp();

        auto fill = [&]( const std::vector<ZONE*>& zones )
        {
            if( cancelled() ) return STATUS::CANCELLED;
            // Empty boards have no fill operation, but may still contain dirty generators.
            if( zones.empty() ) return STATUS::COMPLETED;
            ZONE_FILLER filler( &board, &commit );
            filler.SetProgressReporter( aReporter );
            filler.Fill( zones, false, nullptr );
            switch( filler.LastOutcome() )
            {
            case ZONE_FILLER::OUTCOME::COMPLETED: return STATUS::COMPLETED;
            case ZONE_FILLER::OUTCOME::CANCELLED: return STATUS::CANCELLED;
            case ZONE_FILLER::OUTCOME::NOT_CONVERGED: return STATUS::NOT_CONVERGED;
            default: return STATUS::FAILED;
            }
        };

        std::vector<ZONE*> zones( board.Zones().begin(), board.Zones().end() );
        result.status = fill( zones );
        if( result.status != STATUS::COMPLETED )
        {
            commit.Revert();
            return result;
        }
        board.OnZonesFilled( zones );
        std::vector<PCB_GENERATOR*> regenerated;
        for( PCB_GENERATOR* generator : board.Generators() )
        {
            if( cancelled() )
            {
                result.status = STATUS::CANCELLED;
                commit.Revert();
                return result;
            }
            if( !generator->IsDirty() ) continue;
            if( aReporter ) aReporter->Report( "Regenerating board features" );
            generator->EditStart( generatorTool, &board, &commit );
            if( !generator->Update( generatorTool, &board, &commit ) )
            {
                generator->EditCancel( generatorTool, &board, &commit );
                throw std::runtime_error( "Native generator could not regenerate: "
                                          + generator->m_Uuid.AsStdString() );
            }
            generator->EditFinish( generatorTool, &board, &commit );
            regenerated.push_back( generator );
        }
        // Generated tracks can invalidate their teardrops. Let the native commit
        // rebuild those dependencies before collecting the final refill input.
        commit.Push( wxEmptyString, SKIP_UNDO | SKIP_SET_DIRTY | SKIP_CONNECTIVITY | ZONE_FILL_OP );

        std::set<ZONE*> affected;
        for( PCB_GENERATOR* generator : regenerated )
        {
            auto refill = generator->GetZonesNeedingRefillAfterUpdate();
            affected.insert( refill.begin(), refill.end() );
        }
        for( ZONE* zone : board.Zones() )
        {
            // Include newly rebuilt teardrops and all copper affected by generated
            // geometry, not just the generator's own declared zone neighbourhood.
            if( !regenerated.empty() || zone->NeedRefill() ) affected.insert( zone );
        }
        board.IncrementTimeStamp();
        result.status = fill( std::vector<ZONE*>( affected.begin(), affected.end() ) );
        if( result.status != STATUS::COMPLETED )
        {
            commit.Revert();
            return result;
        }
        commit.Push( wxEmptyString, SKIP_UNDO | SKIP_SET_DIRTY | SKIP_TEARDROPS
                                    | SKIP_CONNECTIVITY | ZONE_FILL_OP );
        board.BuildConnectivity();
        if( cancelled() )
        {
            result.status = STATUS::CANCELLED;
            return result;
        }
        for( PCB_GENERATOR* generator : regenerated ) result.regenerated.push_back( generator->m_Uuid );
        result.status = STATUS::COMPLETED;
    }
    catch( const std::exception& error )
    {
        result.status = cancelled() ? STATUS::CANCELLED : STATUS::FAILED;
        result.error = error.what();
        if( !commit.Empty() ) commit.Revert();
    }
    // A failed bundle is never reused as fresh verification input. The caller
    // discards it; any earlier completed stage belongs only to this private board.
    return result;
}
