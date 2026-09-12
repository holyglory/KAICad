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

#include <confirm.h>
#include <jobs/scratch_doc.h>
#include <sch_edit_frame.h>
#include <schematic.h>
#include <kiface_base.h>
#include <dialog_sch_import_settings.h>
#include <dialogs/panel_setup_netclasses.h>
#include <dialogs/panel_setup_severities.h>
#include <dialogs/panel_setup_buses.h>
#include "panel_setup_net_chains.h"
#include <panel_eeschema_annotation_options.h>
#include <panel_setup_formatting.h>
#include <panel_setup_pinmap.h>
#include <erc/erc_item.h>
#include <panel_text_variables.h>
#include <panel_bom_presets.h>
#include <panel_embedded_files.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <sch_io/sch_io.h>
#include <settings/settings_manager.h>
#include <widgets/wx_infobar.h>
#include <widgets/wx_progress_reporters.h>
#include "dialog_schematic_setup.h"

#include "panel_setup_symbol_parity.h"
#include "panel_template_fieldnames.h"
#include <sch_setup_draft.h>
#include <sch_commit.h>
#include <sch_screen.h>
#include <sch_symbol.h>
#include <sch_symbol_cache_state.h>
#include <richio.h>


DIALOG_SCHEMATIC_SETUP::DIALOG_SCHEMATIC_SETUP( SCH_EDIT_FRAME* aFrame ) :
        PAGED_DIALOG( aFrame, _( "Schematic Setup" ), true, false,
                      _( "Import Settings from Another Project..." ), wxSize( 920, 460 ) ),
        m_frame( aFrame ),
        m_draft( std::make_shared<SCH_SETUP_DRAFT>( aFrame->Prj().GetProjectFile() ) ),
        m_initialRevision( aFrame->Schematic().ChangeJournal().Sequence() )
{
    SetEvtHandlerEnabled( false );
    // wxWindow destroys child panels after this derived object's members. Keep
    // their referenced draft alive until the parent event handler is destroyed.
    Bind( wxEVT_DESTROY, [draft = m_draft]( wxWindowDestroyEvent& event ) { (void) draft; event.Skip(); } );

    m_pinToPinError = ERC_ITEM::Create( ERCE_PIN_TO_PIN_WARNING );

    /*
     * WARNING: If you change page names you MUST update calls to ShowSchematicSetupDialog().
     */

    m_treebook->AddPage( new wxPanel( GetTreebook() ), _( "General" ) );

    m_formattingPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_SETUP_FORMATTING( aParent, m_frame, &m_draft->SchematicSettings() );
            }, _( "Formatting" ) );

    m_annotationPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_EESCHEMA_ANNOTATION_OPTIONS( aParent, m_frame, &m_draft->SchematicSettings() );
            }, _( "Annotation" ) );

    m_fieldNameTemplatesPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                PROJECT_FILE& project = m_draft->ProjectSettings();
                return new PANEL_TEMPLATE_FIELDNAMES( aParent, &project.m_TemplateFieldNames );
            }, _( "Field Name Templates" ) );

    m_bomPresetsPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                SCHEMATIC_SETTINGS& settings = m_draft->SchematicSettings();
                return new PANEL_BOM_PRESETS( aParent, settings );
            }, _( "BOM Presets" ) );


    m_treebook->AddPage( new wxPanel( GetTreebook() ), _( "Electrical Rules" ) );

    m_severitiesPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                ERC_SETTINGS& ercSettings = m_draft->ErcSettings();
                return new PANEL_SETUP_SEVERITIES( aParent, ERC_ITEM::GetItemsWithSeverities(),
                                                   ercSettings.m_ERCSeverities, m_pinToPinError.get() );
            }, _( "Violation Severity" ) );

    m_symbolParityPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_SETUP_SYMBOL_PARITY( aParent, m_frame, &m_draft->SchematicSettings() );
            }, _( "Compare Symbol with Library" ) );

    m_pinMapPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_SETUP_PINMAP( aParent, m_frame, &m_draft->ErcSettings() );
            }, _( "Pin Conflicts Map" ) );

    m_treebook->AddPage( new wxPanel( GetTreebook() ), _( "Project" ) );

    m_netclassesPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                SCHEMATIC& schematic = m_frame->Schematic();
                return new PANEL_SETUP_NETCLASSES( aParent, m_frame, m_draft->ProjectSettings().NetSettings(),
                                                   schematic.GetNetClassAssignmentCandidates(), true );
            }, _( "Net Classes" ) );

    m_busesPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_SETUP_BUSES( aParent, m_frame, &m_draft->ProjectSettings().m_BusAliases );
            }, _( "Bus Alias Definitions" ) );

    m_netChainsPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                m_netChainsPanel = new PANEL_SETUP_NET_CHAINS( aParent, m_frame, m_draft->ProjectSettings().NetSettings() );
                return m_netChainsPanel;
            }, _( "Net Chains" ) );

    m_textVarsPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                return new PANEL_TEXT_VARIABLES( aParent, &Prj(), &m_draft->ProjectSettings().m_TextVars );
            }, _( "Text Variables" ) );


    m_treebook->AddPage( new wxPanel( GetTreebook() ), _( "Schematic Data" ) );

    m_embeddedFilesPage = m_treebook->GetPageCount();
    m_treebook->AddLazySubPage(
            [this]( wxWindow* aParent ) -> wxWindow*
            {
                m_embeddedFilesPanel = new PANEL_EMBEDDED_FILES( aParent, &m_frame->Schematic(),
                                                               NO_MARGINS | EMBEDDED_FILES_DEFER_COMMIT );
                return m_embeddedFilesPanel;
            }, _( "Embedded Files" ) );

    for( size_t i = 0; i < m_treebook->GetPageCount(); ++i )
        m_treebook->ExpandNode( i );

    SetEvtHandlerEnabled( true );

    finishDialogSettings();

    if( Prj().IsReadOnly() )
    {
        m_infoBar->ShowMessage( _( "Project is missing or read-only. Settings will not be editable." ),
                                wxICON_WARNING );
    }

    wxBookCtrlEvent evt( wxEVT_TREEBOOK_PAGE_CHANGED, wxID_ANY, 0 );

    wxQueueEvent( m_treebook, evt.Clone() );
}


bool DIALOG_SCHEMATIC_SETUP::TransferDataFromWindow()
{
    if( !PAGED_DIALOG::TransferDataFromWindow() ) return false;
    auto unchanged = [&]()
    {
        return m_frame->Schematic().ChangeJournal().Sequence() == m_initialRevision
                && m_draft->MatchesLive( m_frame->Prj().GetProjectFile() );
    };
    if( !unchanged() )
    {
        m_infoBar->ShowMessage( _( "The design changed while Schematic Setup was open. Close and reopen it before applying settings." ),
                               wxICON_WARNING );
        return false;
    }
    SCH_COMMIT commit( m_frame );
    try
    {
        std::unique_ptr<EMBEDDED_FILES> files;
        std::map<SCH_SCREEN*, SCH_SYMBOL_CACHE_STATE> caches;
        struct SYMBOL_FILES
        {
            SCH_SCREEN* screen;
            SCH_SYMBOL* symbol;
            std::unique_ptr<LIB_SYMBOL> definition;
        };
        std::vector<SYMBOL_FILES> symbolFiles;
        if( m_embeddedFilesPanel )
        {
            files = std::make_unique<EMBEDDED_FILES>( *m_embeddedFilesPanel->GetLocalFiles(), true );
            const auto removed = m_embeddedFilesPanel->ConfirmNestedRemovals();
            std::set<SCH_SCREEN*> seen;
            for( const auto& path : m_frame->Schematic().Hierarchy() )
            {
                SCH_SCREEN* screen = path.LastScreen();
                if( removed.empty() || !screen || !seen.insert( screen ).second ) continue;
                SCH_SYMBOL_CACHE_STATE candidate( screen->GetLibSymbols() );
                bool changed = false;
                for( const auto& [key, symbol] : candidate.Symbols() )
                    for( const wxString& name : removed )
                        if( symbol->GetEmbeddedFiles()->HasFile( name ) )
                        {
                            symbol->GetEmbeddedFiles()->RemoveFile( name, true );
                            changed = true;
                        }
                if( changed )
                {
                    // Placed symbols own private library definitions as well
                    // as the screen cache. Update both exact owners in the same
                    // transaction, never only the cache's copy of a file link.
                    for( SCH_ITEM* item : screen->Items() )
                    {
                        auto* symbol = dynamic_cast<SCH_SYMBOL*>( item );
                        if( !symbol || !symbol->GetLibSymbolRef() ) continue;
                        auto definition = std::make_unique<LIB_SYMBOL>( *symbol->GetLibSymbolRef() );
                        bool removedLink = false;
                        for( const wxString& name : removed )
                            if( definition->GetEmbeddedFiles()->HasFile( name ) )
                            {
                                definition->GetEmbeddedFiles()->RemoveFile( name, true );
                                removedLink = true;
                            }
                        if( !removedLink ) continue;
                        if( symbol->IsLocked() )
                            throw std::runtime_error( "A locked symbol still references an embedded file selected for removal" );
                        symbolFiles.push_back( { screen, symbol, std::move( definition ) } );
                    }
                    caches.emplace( screen, std::move( candidate ) );
                }
            }
            STRING_FORMATTER before, after;
            auto* live = m_frame->Schematic().GetEmbeddedFiles();
            live->WriteEmbeddedFiles( before, true );
            files->WriteEmbeddedFiles( after, true );
            if( before.GetString() == after.GetString()
                    && live->GetAreFontsEmbedded() == files->GetAreFontsEmbedded() ) files.reset();
            else files->UpdateFontFiles();
        }
        // A nested-file confirmation yields the event loop. Admit the complete
        // draft again before the first native mutation.
        if( !unchanged() ) throw std::runtime_error( "The design changed before settings were applied" );
        commit.SetSetupSettings( m_draft->Baseline(), m_draft->ProjectSettings().CaptureCurrentState() );
        if( m_netChainsPanel && !m_netChainsPanel->ApplyEdits( &commit ) )
            throw std::runtime_error( "Net-chain changes could not be applied" );
        for( const auto& [screen, candidate] : caches ) commit.CaptureLibraryCache( *screen );
        for( auto& item : symbolFiles )
        {
            commit.Modify( item.symbol, item.screen );
            item.symbol->SetLibSymbol( item.definition.release() );
            item.screen->Update( item.symbol );
        }
        for( auto& [screen, candidate] : caches ) commit.ReplaceLibraryCache( *screen, candidate );
        if( files ) commit.ReplaceEmbeddedFiles( *files );
        wxString failure;
        if( !commit.ValidateLibraryCaches( failure ) ) throw std::runtime_error( failure.ToStdString() );
        if( m_netChainsPanel ) m_netChainsPanel->ReleaseModelPointers();
        commit.Push( _( "Edit Schematic Setup" ) );
        return true;
    }
    catch( const std::exception& error )
    {
        if( m_netChainsPanel ) m_netChainsPanel->ReleaseModelPointers();
        commit.Revert();
        if( m_netChainsPanel ) m_netChainsPanel->RebindModelPointers();
        DisplayErrorMessage( this, _( "Schematic settings could not be applied." ), wxString::FromUTF8( error.what() ) );
        return false;
    }
}


void DIALOG_SCHEMATIC_SETUP::onPageChanged( wxBookCtrlEvent& aEvent )
{
    PAGED_DIALOG::onPageChanged( aEvent );

    int page = aEvent.GetSelection();

    if( m_netChainsPanel && m_treebook->GetCurrentPage() == m_netChainsPanel )
        m_netChainsPanel->RefreshNetClassChoices();

    if( Prj().IsReadOnly() )
        KIUI::Disable( m_treebook->GetPage( page ) );
}


void DIALOG_SCHEMATIC_SETUP::onAuxiliaryAction( wxCommandEvent& event )
{
    SETTINGS_MANAGER*          mgr = m_frame->GetSettingsManager();
    DIALOG_SCH_IMPORT_SETTINGS importDlg( this, m_frame );

    if( importDlg.ShowModal() == wxID_CANCEL )
        return;

    wxFileName projectFn( importDlg.GetFilePath() );

    SCRATCH_PROJECT scratch( *mgr, projectFn.GetFullPath(), /*aRequireProjectFile=*/true );

    if( !scratch.IsValid() )
    {
        DisplayErrorMessage( this, wxString::Format( _( "Error importing settings from project:\n"
                                                        "Project file %s could not be loaded." ),
                                                     projectFn.GetFullPath() ) );
        return;
    }

    PROJECT*      otherPrj = scratch.GetProject();
    SCHEMATIC     otherSch( otherPrj );
    PROJECT_FILE& file = otherPrj->GetProjectFile();

    wxASSERT( file.m_SchematicSettings );

    file.m_SchematicSettings->LoadFromFile();

    if( importDlg.m_FormattingOpt->GetValue() )
    {
        static_cast<PANEL_SETUP_FORMATTING*>( m_treebook->ResolvePage( m_formattingPage ) )
                ->ImportSettingsFrom( *file.m_SchematicSettings );
    }

    if( importDlg.m_FieldNameTemplatesOpt->GetValue() )
    {
        static_cast<PANEL_TEMPLATE_FIELDNAMES*>( m_treebook->ResolvePage( m_fieldNameTemplatesPage ) )
                ->ImportSettingsFrom( &file.m_TemplateFieldNames );
    }

    if( importDlg.m_SymbolParityOpt->GetValue() )
    {
        static_cast<PANEL_SETUP_SYMBOL_PARITY*>( m_treebook->ResolvePage( m_symbolParityPage ) )
                ->ImportSettingsFrom( file.m_SchematicSettings->m_SymbolParity );
    }

    if( importDlg.m_PinMapOpt->GetValue() )
    {
        static_cast<PANEL_SETUP_PINMAP*>( m_treebook->ResolvePage( m_pinMapPage ) )
                ->ImportSettingsFrom( file.m_ErcSettings->m_PinMap );
    }

    if( importDlg.m_SeveritiesOpt->GetValue() )
    {
        static_cast<PANEL_SETUP_SEVERITIES*>( m_treebook->ResolvePage( m_severitiesPage ) )
                ->ImportSettingsFrom( file.m_ErcSettings->m_ERCSeverities );
    }

    if( importDlg.m_NetClassesOpt->GetValue() )
    {
        auto* panel = static_cast<PANEL_SETUP_NETCLASSES*>( m_treebook->ResolvePage( m_netclassesPage ) );
        panel->ImportSettingsFrom( file.m_NetSettings );
        // Imports populate controls even when their page is not visible. Keep
        // dependent choices current without committing to the live project.
        if( panel->TransferDataFromWindow() && m_netChainsPanel )
            m_netChainsPanel->RefreshNetClassChoices();
    }

    if( importDlg.m_BomPresetsOpt->GetValue() )
    {
        static_cast<PANEL_BOM_PRESETS*>( m_treebook->ResolvePage( m_bomPresetsPage ) )
                ->ImportBomPresetsFrom( *file.m_SchematicSettings );
    }

    if( importDlg.m_BomFmtPresetsOpt->GetValue() )
    {
        static_cast<PANEL_BOM_PRESETS*>( m_treebook->ResolvePage( m_bomPresetsPage ) )
                ->ImportBomFmtPresetsFrom( *file.m_SchematicSettings );
    }

    if( importDlg.m_annotationOpt->GetValue() )
    {
        static_cast<PANEL_EESCHEMA_ANNOTATION_OPTIONS*>( m_treebook->ResolvePage( m_annotationPage ) )
                ->ImportSettingsFrom( *file.m_SchematicSettings );
    }

    if( importDlg.m_BusAliasesOpt->GetValue() )
    {
        static_cast<PANEL_SETUP_BUSES*>( m_treebook->ResolvePage( m_busesPage ) )
                ->ImportSettingsFrom( file.m_BusAliases );
    }

    if( importDlg.m_TextVarsOpt->GetValue() )
    {
        static_cast<PANEL_TEXT_VARIABLES*>( m_treebook->ResolvePage( m_textVarsPage ) )
                ->ImportSettingsFrom( otherPrj );
    }
}
