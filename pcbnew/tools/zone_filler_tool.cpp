/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2014-2017 CERN
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * @author Maciej Suminski <maciej.suminski@cern.ch>
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
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
#include <cstdint>
#include <exception>
#include <thread>
#include <zone.h>
#include <connectivity/connectivity_data.h>
#include <board_commit.h>
#include <footprint.h>
#include <pcb_track.h>
#include <pad.h>
#include <pcb_group.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <progress_reporter.h>
#include <widgets/wx_infobar.h>
#include <widgets/wx_progress_reporters.h>
#include <wx/event.h>
#include <wx/hyperlink.h>
#include <tool/tool_manager.h>
#include <tool/actions.h>
#include <tools/pcb_selection_tool.h>
#include "pcb_actions.h"
#include "zone_filler_tool.h"
#include "zone_filler.h"
#include "teardrop/teardrop.h"
#include <core/profile.h>
#include <pcb_generator.h>
#include <tools/generator_tool.h>
#include <scoped_set_reset.h>

namespace
{
// BOARD_COMMIT's destructor discards copies without restoring the live board.
// Keep each phase's transaction owned here and explicitly revert pending edits.
class FILL_COMMIT_GUARD
{
public:
    FILL_COMMIT_GUARD( BOARD_COMMIT& aCommit, bool& aApplied, bool& aKnown, bool& aRolledBack ) :
            m_commit( aCommit ), m_applied( aApplied ), m_known( aKnown ), m_rolledBack( aRolledBack )
    {}

    ~FILL_COMMIT_GUARD() { Revert(); }

    void Push( const wxString& aMessage, int aFlags )
    {
        const bool hasChanges = !m_commit.Empty();
        m_pushStarted = true;
        m_known = false;
        m_commit.Push( aMessage, aFlags );
        m_applied = hasChanges;
        m_known = true;
        m_pending = false;
        m_pushStarted = false;
    }

    void Revert() noexcept
    {
        if( !m_pending )
            return;

        // A partially executed Push can already have transferred undo copies.
        // It cannot safely be treated as an unpushed transaction to restore.
        if( m_pushStarted )
        {
            m_known = false;
            return;
        }

        try
        {
            m_commit.Revert();
            m_pending = false;
            m_rolledBack = true;
        }
        catch( ... )
        {
            m_known = false;
        }
    }

private:
    BOARD_COMMIT& m_commit;
    bool& m_applied;
    bool& m_known;
    bool& m_rolledBack;
    bool m_pending = true;
    bool m_pushStarted = false;
};

std::string fillOutcomeError( ZONE_FILLER::OUTCOME aOutcome )
{
    switch( aOutcome )
    {
    case ZONE_FILLER::OUTCOME::CANCELLED: return "zone_fill_cancelled";
    case ZONE_FILLER::OUTCOME::NOT_CONVERGED: return "zone_fill_not_converged";
    case ZONE_FILLER::OUTCOME::UNCHANGED_OR_DECLINED: return "zone_fill_unchanged_or_declined";
    default: return "zone_fill_failed";
    }
}
}

ZONE_FILLER_TOOL::ZONE_FILLER_TOOL() :
    PCB_TOOL_BASE( ZONE_FILLER_TOOL_NAME ),
    m_fillInProgress( false )
{
}


ZONE_FILLER_TOOL::~ZONE_FILLER_TOOL()
{
}


void ZONE_FILLER_TOOL::Reset( RESET_REASON aReason )
{
}


std::vector<PCB_GENERATOR*> ZONE_FILLER_TOOL::regenerateDirtyGenerators( BOARD_COMMIT& aCommit )
{
    std::vector<PCB_GENERATOR*> regenerated;

    BOARD*          brd = board();
    GENERATOR_TOOL* genTool = m_toolMgr ? m_toolMgr->GetTool<GENERATOR_TOOL>() : nullptr;

    if( !brd || !genTool )
        return regenerated;

    for( PCB_GENERATOR* gen : brd->Generators() )
    {
        if( !gen->IsDirty() )
            continue;

        gen->EditStart( genTool, brd, &aCommit );
        gen->Update( genTool, brd, &aCommit );
        gen->EditFinish( genTool, brd, &aCommit );
        regenerated.push_back( gen );
    }

    return regenerated;
}


ZONE_FILL_RESULT ZONE_FILLER_TOOL::refillAroundGenerators(
        const std::vector<PCB_GENERATOR*>& aRegenerated, PROGRESS_REPORTER* aReporter )
{
    ZONE_FILL_RESULT result;
    result.cleanupFinished = true;
    if( aRegenerated.empty() )
        return result;

    try
    {
        BOARD* brd = board();
        if( !brd )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.failureStage = "generator-refill-target";
            result.errorCode = "zone_fill_target_unavailable";
            result.errorMessage = "The board disappeared before the generator refill.";
            return result;
        }

        std::set<ZONE*> uniqueZones;
        for( PCB_GENERATOR* gen : aRegenerated )
        {
            for( ZONE* zone : gen->GetZonesNeedingRefillAfterUpdate() )
                uniqueZones.insert( zone );
        }
        if( uniqueZones.empty() )
            return result;

        result.generatorRefillRequired = true;
        result.rollbackCompleted = false;
        result.failureStage = "generator-refill";
        brd->IncrementTimeStamp();

        BOARD_COMMIT refillCommit( this );
        FILL_COMMIT_GUARD transaction( refillCommit, result.generatorRefillCommitApplied,
                                      result.commitStateKnown, result.rollbackCompleted );
        ZONE_FILLER refiller( brd, &refillCommit );
        refiller.SetProgressReporter( aReporter );
        bool accepted;
        try
        {
            accepted = refiller.Fill( std::vector<ZONE*>( uniqueZones.begin(), uniqueZones.end() ) );
        }
        catch( ... )
        {
            result.generatorRefillOutcome = refiller.LastOutcome();
            throw;
        }
        result.generatorRefillOutcome = refiller.LastOutcome();
        result.outcome = result.generatorRefillOutcome;
        if( accepted )
        {
            result.failureStage = "generator-refill-commit";
            transaction.Push( wxEmptyString, APPEND_UNDO | SKIP_CONNECTIVITY | ZONE_FILL_OP );
        }
        else
        {
            result.failureStage = "generator-refill-rollback";
            transaction.Revert();
        }

        if( result.outcome == ZONE_FILLER::OUTCOME::COMPLETED && result.commitStateKnown )
            result.failureStage.clear();
        else
        {
            result.errorCode = fillOutcomeError( result.outcome );
            result.errorMessage = "The fill around regenerated items did not complete successfully.";
        }
    }
    catch( const std::exception& error )
    {
        result.outcome = ZONE_FILLER::OUTCOME::FAILED;
        result.errorCode = "zone_fill_failed";
        result.errorMessage = error.what();
    }
    catch( ... )
    {
        result.outcome = ZONE_FILLER::OUTCOME::FAILED;
        result.errorCode = "zone_fill_failed";
        result.errorMessage = "The fill around regenerated items raised an unknown exception.";
    }
    if( !result.commitStateKnown )
    {
        result.rollbackCompleted = false;
        result.errorCode = "zone_fill_commit_state_unknown";
    }
    return result;
}


void ZONE_FILLER_TOOL::CheckAllZones( wxWindow* aCaller, PROGRESS_REPORTER* aReporter )
{
    if( !getEditFrame<PCB_EDIT_FRAME>()->m_ZoneFillsDirty || m_fillInProgress )
        return;

    m_fillInProgress = true;

    std::vector<ZONE*> toFill;

    for( ZONE* zone : board()->Zones() )
        toFill.push_back( zone );

    BOARD_COMMIT                          commit( this );
    std::unique_ptr<WX_PROGRESS_REPORTER> reporter;

    m_filler = std::make_unique<ZONE_FILLER>( frame()->GetBoard(), &commit );

    if( aReporter )
    {
        m_filler->SetProgressReporter( aReporter );
    }
    else
    {
        reporter = std::make_unique<WX_PROGRESS_REPORTER>( aCaller, _( "Check Zones" ), 4, PR_CAN_ABORT );
        m_filler->SetProgressReporter( reporter.get() );
    }

    if( m_filler->Fill( toFill, true, aCaller ) )
    {
        commit.Push( _( "Fill Zone(s)" ), SKIP_CONNECTIVITY | ZONE_FILL_OP );
        getEditFrame<PCB_EDIT_FRAME>()->m_ZoneFillsDirty = false;
    }
    else
    {
        commit.Revert();
    }

    PostFillRefresh();

    m_fillInProgress = false;
    m_filler.reset( nullptr );
}


void ZONE_FILLER_TOOL::singleShotRefocus( wxIdleEvent& )
{
    canvas()->SetFocus();
    canvas()->Unbind( wxEVT_IDLE, &ZONE_FILLER_TOOL::singleShotRefocus, this );
}


void ZONE_FILLER_TOOL::FillAllZones( wxWindow* aCaller, PROGRESS_REPORTER* aReporter, bool aHeadless )
{
    FillAllZonesWithResult( aCaller, aReporter, aHeadless );
}


ZONE_FILL_RESULT ZONE_FILLER_TOOL::FillAllZonesWithResult(
        wxWindow* aCaller, PROGRESS_REPORTER* aReporter, bool aHeadless )
{
    ZONE_FILL_RESULT result;
    if( m_fillInProgress )
    {
        result.cleanupFinished = true;
        result.failureStage = "admission";
        result.errorCode = "zone_fill_busy";
        result.errorMessage = "A zone fill is already running.";
        return result;
    }

    PCB_EDIT_FRAME* editFrame = aHeadless ? nullptr : getEditFrame<PCB_EDIT_FRAME>();
    if( !board() || ( !aHeadless && !editFrame ) )
    {
        result.cleanupFinished = true;
        result.failureStage = "target";
        result.errorCode = "zone_fill_target_unavailable";
        result.errorMessage = "The zone fill target is unavailable.";
        return result;
    }

    {
        SCOPED_SET_RESET<bool> busy( m_fillInProgress, true );
        std::unique_ptr<WX_PROGRESS_REPORTER> reporter;
        struct FILLER_CLEANUP
        {
            std::unique_ptr<ZONE_FILLER>& filler;
            ~FILLER_CLEANUP() { filler.reset(); }
        } fillerCleanup{ m_filler };

        result.rollbackCompleted = false;
        bool primaryReverted = false;
        bool refillReverted = true;
        try
        {
            BOARD_COMMIT commit( this );
            FILL_COMMIT_GUARD transaction( commit, result.primaryCommitApplied,
                                          result.commitStateKnown, primaryReverted );
            result.failureStage = "teardrops";
            TEARDROP_MANAGER teardropMgr( board(), m_toolMgr );
            teardropMgr.UpdateTeardrops( commit, nullptr, nullptr, true );

            board()->IncrementTimeStamp();
            std::vector<ZONE*> toFill( board()->Zones().begin(), board()->Zones().end() );
            m_filler = std::make_unique<ZONE_FILLER>( board(), &commit );

            if( !aHeadless && !board()->GetDesignSettings().m_DRCEngine->RulesValid() )
            {
                WX_INFOBAR* infobar = editFrame->GetInfoBar();
                infobar->RemoveAllButtons();
                infobar->AddLink( _( "Show DRC rules" ),
                        [editFrame]( wxHyperlinkEvent& )
                        {
                            editFrame->ShowBoardSetupDialog( _( "Rules" ) );
                        } );
                infobar->ShowMessageFor( _( "Zone fills may be inaccurate.  DRC rules contain errors." ),
                                        10000, wxICON_WARNING );
            }

            if( aReporter )
                m_filler->SetProgressReporter( aReporter );
            else if( !aHeadless )
            {
                reporter = std::make_unique<WX_PROGRESS_REPORTER>(
                        aCaller, _( "Fill All Zones" ), 5, PR_CAN_ABORT );
                m_filler->SetProgressReporter( reporter.get() );
            }

            result.failureStage = "primary-fill";
            bool accepted;
            try
            {
                accepted = m_filler->Fill( toFill );
            }
            catch( ... )
            {
                result.primaryOutcome = m_filler->LastOutcome();
                throw;
            }
            result.primaryOutcome = m_filler->LastOutcome();
            result.outcome = result.primaryOutcome;
            if( accepted )
            {
                if( m_filler->GetProgressReporter() )
                    m_filler->GetProgressReporter()->AdvancePhase();

                result.failureStage = "generators";
                board()->OnZonesFilled( toFill );
                std::vector<PCB_GENERATOR*> regenerated = regenerateDirtyGenerators( commit );
                result.failureStage = "primary-commit";
                transaction.Push( _( "Fill Zone(s)" ), SKIP_CONNECTIVITY | ZONE_FILL_OP );
                if( !aHeadless )
                    editFrame->m_ZoneFillsDirty = false;

                // Keep the generator's changes in the owning first commit. The
                // second commit remains an independent append-undo transaction.
                ZONE_FILL_RESULT refill = refillAroundGenerators(
                        regenerated, m_filler->GetProgressReporter() );
                result.generatorRefillRequired = refill.generatorRefillRequired;
                result.generatorRefillOutcome = refill.generatorRefillOutcome;
                result.generatorRefillCommitApplied = refill.generatorRefillCommitApplied;
                result.commitStateKnown = result.commitStateKnown && refill.commitStateKnown;
                refillReverted = refill.rollbackCompleted;
                if( !refill.errorCode.empty() )
                {
                    result.outcome = refill.outcome;
                    result.failureStage = refill.failureStage;
                    result.errorCode = refill.errorCode;
                    result.errorMessage = refill.errorMessage;
                }
            }
            else
            {
                result.failureStage = "primary-rollback";
                transaction.Revert();
            }

            if( result.outcome != ZONE_FILLER::OUTCOME::COMPLETED && result.errorCode.empty() )
            {
                result.errorCode = fillOutcomeError( result.outcome );
                result.errorMessage = "The primary zone fill did not complete successfully.";
            }
        }
        catch( const std::exception& error )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_failed";
            result.errorMessage = error.what();
        }
        catch( ... )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_failed";
            result.errorMessage = "The zone fill raised an unknown exception.";
        }

        result.rollbackCompleted = result.commitStateKnown && primaryReverted && refillReverted
                                   && !result.primaryCommitApplied && !result.generatorRefillCommitApplied;
        if( !result.commitStateKnown )
        {
            result.rollbackCompleted = false;
            result.errorCode = "zone_fill_commit_state_unknown";
        }

        // Refresh after either commit or rollback, while the result still owns
        // the exact pass outcomes and before clearing the busy/filler state.
        const std::string earlierStage = result.failureStage;
        try
        {
            result.failureStage = "cleanup";
            PostFillRefresh( aHeadless );
            if( !aHeadless && m_filler && m_filler->IsDebug() )
                editFrame->UpdateUserInterface();
            if( !aHeadless )
                canvas()->Bind( wxEVT_IDLE, &ZONE_FILLER_TOOL::singleShotRefocus, this );
            result.failureStage = result.errorCode.empty() ? "" : earlierStage;
        }
        catch( const std::exception& error )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_cleanup_failed";
            result.errorMessage = error.what();
        }
        catch( ... )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_cleanup_failed";
            result.errorMessage = "The zone fill cleanup raised an unknown exception.";
        }
    }
    result.cleanupFinished = true;
    return result;
}


int ZONE_FILLER_TOOL::ZoneFillDirty( const TOOL_EVENT& aEvent )
{
    PCB_EDIT_FRAME*    frame = getEditFrame<PCB_EDIT_FRAME>();
    std::vector<ZONE*> toFill;

    for( ZONE* zone : board()->Zones() )
    {
        if( !zone->IsFilled() || m_dirtyZoneIDs.count( zone->m_Uuid ) )
            toFill.push_back( zone );
    }

    if( toFill.empty() )
        return 0;

    if( m_fillInProgress )
        return 0;

    int64_t startTime = GetRunningMicroSecs();
    m_fillInProgress = true;

    m_dirtyZoneIDs.clear();

    board()->IncrementTimeStamp();    // Clear caches

    BOARD_COMMIT                          commit( this );
    std::unique_ptr<WX_PROGRESS_REPORTER> reporter;
    int                                   pts = 0;

    m_filler = std::make_unique<ZONE_FILLER>( board(), &commit );

    if( !board()->GetDesignSettings().m_DRCEngine->RulesValid() )
    {
        WX_INFOBAR* infobar = frame->GetInfoBar();

        infobar->RemoveAllButtons();

        infobar->AddLink( _( "Show DRC rules" ),
                      [frame]( wxHyperlinkEvent& aLocEvent )
                      {
                          frame->ShowBoardSetupDialog( _( "Rules" ) );
                      } );

        infobar->ShowMessageFor( _( "Zone fills may be inaccurate.  DRC rules contain errors." ), 10000,
                                 wxICON_WARNING );
    }

    for( ZONE* zone : toFill )
    {
        zone->GetLayerSet().RunOnLayers(
                [&]( PCB_LAYER_ID layer )
                {
                    pts += zone->GetFilledPolysList( layer )->FullPointCount();
                } );

        if( pts > 1000 )
        {
            wxString title = wxString::Format( _( "Refill %d Zones" ), (int) toFill.size() );

            reporter = std::make_unique<WX_PROGRESS_REPORTER>( frame, title, 5, PR_CAN_ABORT );
            m_filler->SetProgressReporter( reporter.get() );
            break;
        }
    }

    bool                        filled = m_filler->Fill( toFill );
    std::vector<PCB_GENERATOR*> regenerated;

    if( filled )
    {
        board()->OnZonesFilled( toFill );
        regenerated = regenerateDirtyGenerators( commit );

        commit.Push( _( "Auto-fill Zone(s)" ), APPEND_UNDO | SKIP_CONNECTIVITY | ZONE_FILL_OP );
    }
    else
    {
        commit.Revert();
    }

    if( filled )
        refillAroundGenerators( regenerated );

    rebuildConnectivity();
    PostFillRefresh();

    if( GetRunningMicroSecs() - startTime > 3000000 )   // 3 seconds
    {
        WX_INFOBAR* infobar = frame->GetInfoBar();

        infobar->RemoveAllButtons();
        infobar->AddLink( _( "Open Preferences" ),
                [this]( wxHyperlinkEvent& )
                {
                    getEditFrame<PCB_EDIT_FRAME>()->ShowPreferences( _( "Editing Options" ), _( "PCB Editor" ) );
                } );

        infobar->ShowMessageFor( _( "Automatic refill of zones can be turned off in Preferences if it becomes "
                                    "too slow." ),
                                 10000, wxICON_INFORMATION, WX_INFOBAR::MESSAGE_TYPE::GENERIC );
    }

    if( m_filler->IsDebug() )
        frame->UpdateUserInterface();

    m_fillInProgress = false;
    m_filler.reset( nullptr );

    // wxWidgets has keyboard focus issues after the progress reporter.  Re-setting the focus
    // here doesn't work, so we delay it to an idle event.
    canvas()->Bind( wxEVT_IDLE, &ZONE_FILLER_TOOL::singleShotRefocus, this );

    return 0;
}


int ZONE_FILLER_TOOL::ZoneFill( const TOOL_EVENT& aEvent )
{
    if( m_fillInProgress )
    {
        wxBell();
        return -1;
    }

    std::vector<ZONE*> toFill;

    if( ZONE* passedZone = aEvent.Parameter<ZONE*>() )
    {
        toFill.push_back( passedZone );
    }
    else
    {
        const PCB_SELECTION& sel = m_toolMgr->GetTool<PCB_SELECTION_TOOL>()->RequestSelection(
                []( const VECTOR2I& aPt, GENERAL_COLLECTOR& aCollector, PCB_SELECTION_TOOL* sTool )
                {
                } );

        for( EDA_ITEM* item : sel )
        {
            if( ZONE* zone = dynamic_cast<ZONE*>( item ) )
                toFill.push_back( zone );
        }
    }

    // Bail out of the filler if there is nothing to fill
    if( toFill.empty() )
    {
        wxBell();
        return -1;
    }

    m_fillInProgress = true;

    BOARD_COMMIT                          commit( this );
    std::unique_ptr<WX_PROGRESS_REPORTER> reporter;

    m_filler = std::make_unique<ZONE_FILLER>( board(), &commit );

    reporter = std::make_unique<WX_PROGRESS_REPORTER>( frame(), _( "Fill Zone" ), 5, PR_CAN_ABORT );
    m_filler->SetProgressReporter( reporter.get() );

    if( m_filler->Fill( toFill ) )
    {
        reporter->AdvancePhase();
        commit.Push( _( "Fill Zone(s)" ), SKIP_CONNECTIVITY | ZONE_FILL_OP );
    }
    else
    {
        commit.Revert();
    }

    PostFillRefresh();

    m_fillInProgress = false;
    m_filler.reset( nullptr );
    return 0;
}


int ZONE_FILLER_TOOL::ZoneFillAll( const TOOL_EVENT& aEvent )
{
    if( !aEvent.HasParameter() )
    {
        FillAllZones( frame() );
        return 0;
    }

    ZONE_FILL_REQUEST request = aEvent.Parameter<ZONE_FILL_REQUEST>();
    ZONE_FILL_RESULT result = FillAllZonesWithResult( frame() );
    if( request.completion )
        request.completion( result );
    return 0;
}


int ZONE_FILLER_TOOL::ZoneUnfill( const TOOL_EVENT& aEvent )
{
    const PCB_SELECTION& sel = m_toolMgr->GetTool<PCB_SELECTION_TOOL>()->RequestSelection(
            []( const VECTOR2I& aPt, GENERAL_COLLECTOR& aCollector, PCB_SELECTION_TOOL* sTool )
            {
            } );

    std::vector<ZONE*> toUnfill;

    for( EDA_ITEM* item : sel )
    {
        if( ZONE* zone = dynamic_cast<ZONE*>( item ) )
            toUnfill.push_back( zone );
    }

    // Bail out if there are no zones
    if( toUnfill.empty() )
    {
        wxBell();
        return -1;
    }

    BOARD_COMMIT commit( this );

    for( ZONE* zone : toUnfill )
    {
        commit.Modify( zone );

        zone->UnFill();
    }

    commit.Push( _( "Unfill Zone" ), ZONE_FILL_OP );

    refresh();

    return 0;
}


int ZONE_FILLER_TOOL::ZoneUnfillAll( const TOOL_EVENT& aEvent )
{
    BOARD_COMMIT commit( this );

    for( ZONE* zone : board()->Zones() )
    {
        commit.Modify( zone );

        zone->UnFill();
    }

    commit.Push( _( "Unfill All Zones" ), ZONE_FILL_OP );

    refresh();

    return 0;
}


PROGRESS_REPORTER* ZONE_FILLER_TOOL::GetProgressReporter()
{
    if( m_fillInProgress && m_filler )
        return m_filler->GetProgressReporter();
    else
        return nullptr;
}


void ZONE_FILLER_TOOL::PostFillRefresh( bool aHeadless )
{
    rebuildConnectivity( aHeadless );

    if( !aHeadless )
        refresh();
}


void ZONE_FILLER_TOOL::rebuildConnectivity( bool aHeadless )
{
    board()->BuildConnectivity();
    m_toolMgr->PostEvent( EVENTS::ConnectivityChangedEvent );
    if( !aHeadless )
        canvas()->RedrawRatsnest();
}


void ZONE_FILLER_TOOL::refresh()
{
    // Note: KIGFX::REPAINT isn't enough for things that go from invisible to visible as
    // they won't be found in the view layer's itemset for re-painting.
    canvas()->GetView()->UpdateAllItemsConditionally( KIGFX::ALL,
            [&]( KIGFX::VIEW_ITEM* aItem ) -> bool
            {
                if( PCB_VIA* via = dynamic_cast<PCB_VIA*>( aItem ) )
                {
                    return via->GetRemoveUnconnected();
                }
                else if( PAD* pad = dynamic_cast<PAD*>( aItem ) )
                {
                    return pad->GetRemoveUnconnected();
                }

                return false;
            } );

    canvas()->Refresh();
}


bool ZONE_FILLER_TOOL::IsZoneFillAction( const TOOL_EVENT* aEvent )
{
    return aEvent->IsAction( &PCB_ACTIONS::zoneFill )
           || aEvent->IsAction( &PCB_ACTIONS::zoneFillAll )
           || aEvent->IsAction( &PCB_ACTIONS::zoneUnfill )
           || aEvent->IsAction( &PCB_ACTIONS::zoneUnfillAll );

    // Don't include zoneFillDirty; that's a system action not a user action
}


void ZONE_FILLER_TOOL::setTransitions()
{
    // Zone actions
    Go( &ZONE_FILLER_TOOL::ZoneFill,      PCB_ACTIONS::zoneFill.MakeEvent() );
    Go( &ZONE_FILLER_TOOL::ZoneFillAll,   PCB_ACTIONS::zoneFillAll.MakeEvent() );
    Go( &ZONE_FILLER_TOOL::ZoneFillDirty, PCB_ACTIONS::zoneFillDirty.MakeEvent() );
    Go( &ZONE_FILLER_TOOL::ZoneUnfill,    PCB_ACTIONS::zoneUnfill.MakeEvent() );
    Go( &ZONE_FILLER_TOOL::ZoneUnfillAll, PCB_ACTIONS::zoneUnfillAll.MakeEvent() );
}
