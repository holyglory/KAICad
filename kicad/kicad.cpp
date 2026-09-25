/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2004-2015 Jean-Pierre Charras, jp.charras at wanadoo.fr
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
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

/**
 * @file kicad.cpp
 * Main KiCad project manager file.
 */


#include <wx/filename.h>
#include <wx/log.h>
#include <wx/app.h>
#include <wx/stdpaths.h>
#include <wx/msgdlg.h>
#include <wx/cmdline.h>

#include <api/api_server.h>
#include <api/common/commands/automation_commands.pb.h>
#include <wx/dialog.h>
#include <api/common/commands/editor_commands.pb.h>
#include <common.h>
#include <env_vars.h>
#include <file_history.h>
#include <hotkeys_basic.h>
#include <kiway.h>
#include <macros.h>
#include <paths.h>
#include <richio.h>
#include <settings/settings_manager.h>
#include <settings/kicad_settings.h>
#include <settings/common_settings.h>
#include <../include/startwizard/startwizard.h>
#include <systemdirsappend.h>
#include <thread_pool.h>
#include <trace_helpers.h>
#include <wildcards_and_files_ext.h>
#include <confirm.h>

#include <git/git_backend.h>
#include <git/libgit_backend.h>
#include <cstdlib>

#include "pgm_kicad.h"
#include "kicad_manager_frame.h"
#include "mergetool_frame.h"

#include <wx/crt.h>
#include <wx/evtloop.h>
#include <wx/weakref.h>

#include <kiplatform/app.h>
#include <kiplatform/environment.h>

// a dummy to quiet linking with EDA_BASE_FRAME::config();
#include <kiface_base.h>

#include <libraries/library_manager.h>


KIFACE_BASE& Kiface()
{
    // This function should never be called. It is only referenced from
    // EDA_BASE_FRAME::config() and this is only provided to satisfy the
    // linker, not to be actually called. Print the diagnostic to stderr
    // and abort — throwing here lets wxApp's top-level exception handler
    // pop a second modal dialog, which defeats the point of moving the
    // diagnostic to the terminal in the first place.
    wxFprintf( stderr,
               wxT( "Unexpected call to Kiface() in kicad/kicad.cpp — a "
                    "project-manager-process code path is reaching into a "
                    "kiface stub. Re-run with KICAD_TRACE=KICAD for a "
                    "backtrace.\n" ) );
    std::abort();
}


static PGM_KICAD program;

PGM_KICAD& PgmTop()
{
    return program;
}


bool PGM_KICAD::OnPgmInit()
{
    App().SetAppDisplayName( wxT( "KiCad" ) );

#if defined(DEBUG)
    wxString absoluteArgv0 = wxStandardPaths::Get().GetExecutablePath();

    if( !wxIsAbsolutePath( absoluteArgv0 ) )
    {
        wxLogError( wxT( "No meaningful argv[0]" ) );
        return false;
    }
#endif

    // Initialize the git backend before trying to initialize individual programs
    SetGitBackend( new LIBGIT_BACKEND() );
    GetGitBackend()->Init();

    static const wxCmdLineEntryDesc desc[] = {
        { wxCMD_LINE_OPTION, "f", "frame", "Frame to load", wxCMD_LINE_VAL_STRING, 0 },
        { wxCMD_LINE_SWITCH, "n", "new", "New instance of KiCad, does not attempt to load previously open files",
          wxCMD_LINE_VAL_NONE, 0 },
        { wxCMD_LINE_OPTION, nullptr, "automation", "Automation instance UUID (requires an explicit project and API socket)",
          wxCMD_LINE_VAL_STRING, 0 },
        { wxCMD_LINE_OPTION, nullptr, "update-manager", "Empty update manager UUID (requires --new and an explicit API socket)",
          wxCMD_LINE_VAL_STRING, 0 },
        { wxCMD_LINE_OPTION, nullptr, "api-socket", "Explicit native automation IPC socket path",
          wxCMD_LINE_VAL_STRING, 0 },
        { wxCMD_LINE_OPTION, nullptr, "automation-log", "Native automation diagnostic log",
          wxCMD_LINE_VAL_STRING, 0 },
        { wxCMD_LINE_SWITCH, nullptr, "mergetool",
          "Launch the 3-way merge tool. Expects four positional args: "
          "ANCESTOR OURS THEIRS MERGED. Intended as a `git mergetool` driver.",
          wxCMD_LINE_VAL_NONE, 0 },
#ifndef __WXOSX__
        { wxCMD_LINE_SWITCH, nullptr, "software-rendering", "Use software rendering instead of OpenGL",
          wxCMD_LINE_VAL_NONE, 0 },
#endif
        { wxCMD_LINE_PARAM, nullptr, nullptr, "File to load", wxCMD_LINE_VAL_STRING,
          wxCMD_LINE_PARAM_MULTIPLE | wxCMD_LINE_PARAM_OPTIONAL },
        { wxCMD_LINE_NONE, nullptr, nullptr, nullptr, wxCMD_LINE_VAL_NONE, 0 }
    };

    wxCmdLineParser parser( App().argc, App().argv );
    parser.SetDesc( desc );
    parser.Parse( false );

    wxString automationId;
    wxString automationSocket;
    wxString automationLog;
    wxString automationProject;
    const bool projectAutomation = parser.Found( "automation", &automationId );
    wxString updateManagerId;
    const bool updateManagerMode = parser.Found( "update-manager", &updateManagerId );
    const bool automationMode = projectAutomation || updateManagerMode;

    if( updateManagerMode )
    {
        if( projectAutomation || parser.FoundSwitch( "new" ) != wxCMD_SWITCH_ON )
        {
            wxFprintf( stderr, "--update-manager requires --new and cannot combine with --automation\n" );
            return false;
        }

        automationId = updateManagerId;
    }

    if( automationMode )
    {
        if( automationId.IsEmpty() || !parser.Found( "api-socket", &automationSocket )
                || automationSocket.IsEmpty() || parser.GetParamCount() != ( updateManagerMode ? 0U : 1U )
                || parser.FoundSwitch( "mergetool" ) || parser.Found( "frame" ) )
        {
            wxFprintf( stderr, "Project automation requires one project; update-manager requires none. Both require an instance ID and API socket.\n" );
            return false;
        }

        if( !wxFileName( automationSocket ).IsAbsolute() )
        {
            wxFprintf( stderr, "Automation requires an absolute socket path\n" );
            return false;
        }

        if( !updateManagerMode )
        {
            wxFileName project( parser.GetParam( 0 ) );
            project.MakeAbsolute();

            if( !project.FileExists() || project.GetExt() != FILEEXT::ProjectFileExtension )
            {
                wxFprintf( stderr, "Project automation requires an existing .kicad_pro\n" );
                return false;
            }

            automationProject = project.GetFullPath();
        }

        if( parser.Found( "automation-log", &automationLog ) )
        {
            // Keep diagnostic descriptors owned by KiCad rather than the MCP
            // process, so disconnecting MCP never breaks a dirty editor's IO.
            if( !wxFreopen( automationLog, "a", stdout ) || !wxFreopen( automationLog, "a", stderr ) )
                return false;
        }

        delete wxLog::SetActiveTarget( new wxLogStderr() );
    }
    else if( parser.Found( "api-socket" ) || parser.Found( "automation-log" ) )
    {
        wxFprintf( stderr, "--api-socket and --automation-log require --automation or --update-manager\n" );
        return false;
    }

    bool mergetoolMode = parser.FoundSwitch( "mergetool" ) == wxCMD_SWITCH_ON;

    if( mergetoolMode && parser.GetParamCount() != 4 )
    {
        wxFprintf( stderr,
                   wxT( "kicad --mergetool expects four positional arguments: "
                        "ANCESTOR OURS THEIRS MERGED\n" ) );
        return false;
    }

    FRAME_T appType = mergetoolMode ? FRAME_MERGETOOL : KICAD_MAIN_FRAME_T;

    const struct
    {
        wxString name;
        FRAME_T  type;
    } frameTypes[] = { { wxT( "pcb" ), FRAME_PCB_EDITOR },
                       { wxT( "fpedit" ), FRAME_FOOTPRINT_EDITOR },
                       { wxT( "sch" ), FRAME_SCH },
                       { wxT( "calc" ), FRAME_CALC },
                       { wxT( "bm2cmp" ), FRAME_BM2CMP },
                       { wxT( "ds" ), FRAME_PL_EDITOR },
                       { wxT( "gerb" ), FRAME_GERBER },
                       { wxT( "" ), FRAME_T_COUNT } };

    wxString frameName;

    if( parser.Found( "frame", &frameName ) )
    {
        appType = FRAME_T_COUNT;

        for( const auto& it : frameTypes )
        {
            if( it.name == frameName )
                appType = it.type;
        }

        if( appType == FRAME_T_COUNT )
        {
            wxLogError( wxT( "Unknown frame: %s" ), frameName );
            // Clean up
            OnPgmExit();
            return false;
        }
    }

    if( appType == KICAD_MAIN_FRAME_T )
    {
        Kiway.SetCtlBits( KFCTL_CPP_PROJECT_SUITE );
    }
    else
    {
        Kiway.SetCtlBits( KFCTL_STANDALONE );
    }

#ifndef __WXMAC__
    if( parser.Found( "software-rendering" ) )
    {
        wxSetEnv( "KICAD_SOFTWARE_RENDERING", "1" );
    }
#endif

    if( !InitPgm( false ) )
        return false;


    m_bm.InitSettings( new KICAD_SETTINGS );
    GetSettingsManager().RegisterSettings( PgmSettings() );
    GetSettingsManager().SetKiway( &Kiway );
    m_bm.Init();

    if( const COMMON_SETTINGS* cfg = Pgm().GetCommonSettings() )
    {
        if( cfg->m_Appearance.app_theme == APP_THEME::DARK )
            KIPLATFORM::APP::EnableDarkMode( true );
        else if( cfg->m_Appearance.app_theme == APP_THEME::AUTO )
            KIPLATFORM::APP::EnableDarkMode( false );
    }

    // Add search paths to feed the PGM_KICAD::SysSearch() function,
    // currently limited in support to only look for project templates
    {
        SEARCH_STACK bases;

        SystemDirsAppend( &bases );

        for( unsigned i = 0; i < bases.GetCount(); ++i )
        {
            wxFileName fn( bases[i], wxEmptyString );

            // Add KiCad template file path to search path list.
            fn.AppendDir( wxT( "template" ) );

            // Only add path if exists and can be read by the user.
            if( fn.DirExists() && fn.IsDirReadable() )
                m_bm.m_search.AddPaths( fn.GetPath() );
        }

        auto insertExpanded = [&]( const wxString& aValue )
        {
            wxString resolved = ExpandEnvVarSubstitutions( aValue, nullptr );

            // Skip values that still contain unresolved variable references so we don't
            // pollute the search stack with paths like "${MISSING}/templates".
            if( resolved.Contains( wxT( "${" ) ) || resolved.Contains( wxT( "$(" ) ) )
                return;

            m_bm.m_search.Insert( resolved, 0 );
        };

        // The versioned TEMPLATE_DIR takes precedence over the search stack template path.
        if( std::optional<wxString> v = ENV_VAR::GetVersionedEnvVarValue( GetLocalEnvVariables(),
                                                                          wxT( "TEMPLATE_DIR" ) ) )
        {
            if( !v->IsEmpty() )
                insertExpanded( *v );
        }

        // We've been adding system (installed default) search paths so far, now for user paths
        // The default user search path is inside KIPLATFORM::ENV::GetDocumentsPath()
        m_bm.m_search.Insert( PATHS::GetUserTemplatesPath(), 0 );

        // ...but the user can override that default with the KICAD_USER_TEMPLATE_DIR env var.
        // The value may itself reference other KiCad path variables, so expand them here.
        ENV_VAR_MAP_CITER it = GetLocalEnvVariables().find( "KICAD_USER_TEMPLATE_DIR" );

        if( it != GetLocalEnvVariables().end() && it->second.GetValue() != wxEmptyString )
            insertExpanded( it->second.GetValue() );
    }

    wxFrame*              frame         = nullptr;
    KIWAY_PLAYER*         playerFrame   = nullptr;
    KICAD_MANAGER_FRAME*  managerFrame  = nullptr;
    MERGETOOL_FRAME*      mergetoolFrame = nullptr;

    // Editor frames register their API handlers in their constructors, so the API server must
    // be live before any frame is built.  The short-lived --mergetool driver skips it to avoid
    // contending for the singleton IPC socket.
    if( appType != FRAME_MERGETOOL )
    {
        m_api_server = std::make_unique<KICAD_API_SERVER>( !automationMode );

        if( automationMode )
        {
            m_api_server->ConfigureAutomation( automationId.ToStdString(), automationProject.ToStdString() );
            m_api_server->SetSocketPath( automationSocket );
            m_api_server->Start();

            if( !m_api_server->Running() )
                return false;
        }

        m_api_common_handler = std::make_unique<API_HANDLER_COMMON>();
        m_api_server->RegisterHandler( m_api_common_handler.get() );
    }

    if( appType == FRAME_MERGETOOL )
    {
        MERGETOOL_PATHS paths{ parser.GetParam( 0 ), parser.GetParam( 1 ),
                               parser.GetParam( 2 ), parser.GetParam( 3 ) };
        mergetoolFrame = new MERGETOOL_FRAME( &Kiway, nullptr, paths );
        frame          = mergetoolFrame;
    }
    else if( appType == KICAD_MAIN_FRAME_T )
    {
        managerFrame = new KICAD_MANAGER_FRAME( nullptr, wxT( "KiCad" ), wxDefaultPosition,
                                                wxWindow::FromDIP( wxSize( 775, -1 ), NULL ) );
        frame = managerFrame;

        if( !automationMode )
        {
            STARTWIZARD startWizard;
            startWizard.CheckAndRun( frame );
        }
    }
    else
    {
        // Use KIWAY to create a top window, which registers its existence also.
        // "TOP_FRAME" is a macro that is passed on compiler command line from CMake,
        // and is one of the types in FRAME_T.
        playerFrame = Kiway.Player( appType, true );
        frame = playerFrame;

        if( frame == nullptr )
        {
            return false;
        }
    }

    App().SetTopWindow( frame );

    if( playerFrame )
        App().SetAppDisplayName( playerFrame->GetAboutTitle() );

    Kiway.SetTop( frame );

    KIPLATFORM::ENV::SetAppDetailsForWindow( frame, '"' + wxStandardPaths::Get().GetExecutablePath() + '"' + " -n",
                                             frame->GetTitle() );

    KICAD_SETTINGS* settings = static_cast<KICAD_SETTINGS*>( PgmSettings() );

    GetLibraryManager().LoadGlobalTables();

    wxString projToLoad;

    HideSplash();

    if( playerFrame && parser.GetParamCount() )
    {
        // Now after the frame processing, the rest of the positional args are files
        std::vector<wxString> fileArgs;
        /*
            gerbview handles multiple project data files, i.e. gerber files on
            cmd line. Others currently do not, they handle only one. For common
            code simplicity we simply pass all the arguments in however, each
            program module can do with them what they want, ignore, complain
            whatever.  We don't establish policy here, as this is a multi-purpose
            launcher.
        */

        for( size_t i = 0; i < parser.GetParamCount(); i++ )
            fileArgs.push_back( parser.GetParam( i ) );

        // special attention to a single argument: argv[1] (==argSet[0])
        if( fileArgs.size() == 1 )
        {
            wxFileName argv1( fileArgs[0] );

#if defined( PGM_DATA_FILE_EXT )
            // PGM_DATA_FILE_EXT, if present, may be different for each compile,
            // it may come from CMake on the compiler command line, but often does not.
            // This facility is mostly useful for those program footprints
            // supporting a single argv[1].
            if( !argv1.GetExt() )
                argv1.SetExt( wxT( PGM_DATA_FILE_EXT ) );
#endif
            argv1.MakeAbsolute();

            fileArgs[0] = argv1.GetFullPath();
        }

        // Use the KIWAY_PLAYER::OpenProjectFiles() API function:
        if( !playerFrame->OpenProjectFiles( fileArgs ) )
        {
            // OpenProjectFiles() API asks that it report failure to the UI.
            // Nothing further to say here.

            // We've already initialized things at this point, but wx won't call OnExit if
            // we fail out. Call our own cleanup routine here to ensure the relevant resources
            // are freed at the right time (if they aren't, segfaults will occur).
            OnPgmExit();

            // Fail the process startup if the file could not be opened,
            // although this is an optional choice, one that can be reversed
            // also in the KIFACE specific OpenProjectFiles() return value.
            return false;
        }
    }
    else if( managerFrame )
    {
        if( parser.GetParamCount() > 0 )
        {
            wxFileName tmp = parser.GetParam( 0 );

            if( tmp.GetExt() != FILEEXT::ProjectFileExtension && tmp.GetExt() != FILEEXT::LegacyProjectFileExtension )
            {
                DisplayErrorMessage( nullptr, wxString::Format( _( "File '%s'\n"
                                                                   "does not appear to be a KiCad project file." ),
                                                                tmp.GetFullPath() ) );
            }
            else
            {
                projToLoad = tmp.GetFullPath();
            }
        }

        // If no file was given as an argument, check that there was a file open.
        if( !updateManagerMode && projToLoad.IsEmpty() && settings->m_OpenProjects.size()
                && !parser.FoundSwitch( "new" ) )
        {
            wxString last_pro = settings->m_OpenProjects.front();
            settings->m_OpenProjects.erase( settings->m_OpenProjects.begin() );

            if( wxFileExists( last_pro ) )
            {
                // Try to open the last opened project,
                // if a project name is not given when starting Kicad
                projToLoad = last_pro;
            }
        }

        bool loaded = false;

        // An automation start that fails after the manager window was built destroys that window
        // here, so APP_KICAD::OnInit() deletes it while the program it uses still exists. Left to
        // wxWidgets, the window was deleted only after OnPgmExit() and KiCad crashed on its way
        // out (SIGSEGV) instead of ending with its normal failed-start exit code.
        auto failAutomationStart = [&]()
        {
            if( managerFrame )
            {
                Kiway.SetTop( nullptr );
                managerFrame->Destroy();
            }

            return false;
        };

        // Do not attempt to load a non-existent project file.
        if( !projToLoad.empty() )
        {
            wxFileName fn( projToLoad );

            if( fn.Exists() && (   fn.GetExt() == FILEEXT::ProjectFileExtension
                                || fn.GetExt() == FILEEXT::LegacyProjectFileExtension ) )
            {
                fn.MakeAbsolute();

                if( appType == KICAD_MAIN_FRAME_T )
                {
                    try
                    {
                        loaded = managerFrame->LoadProject( fn );
                    }
                    catch( const IO_ERROR& error )
                    {
                        if( !automationMode )
                            throw;

                        wxLogError( "Automation startup failed: %s", error.What() );
                        return failAutomationStart();
                    }
                }
            }
        }

        if( !loaded && automationMode && !updateManagerMode )
        {
            wxLogError( "Automation project could not be loaded: %s", automationProject );
            return failAutomationStart();
        }

        if( !loaded && appType == KICAD_MAIN_FRAME_T )
            managerFrame->PreloadAllLibraries();
    }

    if( mergetoolFrame )
    {
        // Stay hidden; the merge dialog is its own modal. The frame just needs
        // to be the wxApp top window so the modal has a parent and the wxApp
        // loop has something to drive. Defer the run via CallAfter so the
        // event loop is up before ShowModal spins its nested loop. The
        // wxWeakRef guards against the frame being destroyed (window-manager
        // close, fatal init) before the callback fires.
        wxWeakRef<MERGETOOL_FRAME> mergeToolFrameRef( mergetoolFrame );

        mergetoolFrame->CallAfter(
                [mergeToolFrameRef]() mutable
                {
                    if( !mergeToolFrameRef || mergeToolFrameRef->IsBeingDeleted() )
                        return;

                    int exitCode = mergeToolFrameRef->RunMerge();

                    // Propagate the merge JOB's exit code to the kicad
                    // process exit status so `git mergetool` sees a non-zero
                    // status on unresolved conflicts.
                    if( wxEventLoopBase* loop = wxTheApp->GetMainLoop() )
                        loop->ScheduleExit( exitCode );

                    mergeToolFrameRef->Close( true );
                } );
    }
    else
    {
        frame->Show( true );
        frame->Raise();
    }

    if( m_api_server )
    {
        if( automationMode && managerFrame && !updateManagerMode )
        {
            m_api_common_handler->SetOpenDocumentHandler(
                    [this, managerFrame]( const kiapi::common::commands::OpenDocument& request )
                            -> HANDLER_RESULT<kiapi::common::commands::OpenDocumentResponse>
                    {
                        using namespace kiapi::common;
                        auto fail = []( ApiStatusCode code, const std::string& message )
                                -> HANDLER_RESULT<commands::OpenDocumentResponse>
                        {
                            ApiResponseStatus status;
                            status.set_status( code );
                            status.set_error_message( message );
                            return tl::unexpected( status );
                        };

                        if( request.type() != types::DOCTYPE_SCHEMATIC && request.type() != types::DOCTYPE_PCB )
                            return fail( AS_UNIMPLEMENTED, "This graphical open operation supports project schematics and PCBs" );

                        const auto frameType = request.type() == types::DOCTYPE_SCHEMATIC
                                                       ? FRAME_SCH : FRAME_PCB_EDITOR;

                        wxFileName requested( wxString::FromUTF8( request.path() ) );
                        wxFileName expected( request.type() == types::DOCTYPE_SCHEMATIC
                                                     ? managerFrame->SchFileName() : managerFrame->PcbFileName() );
                        requested.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
                        expected.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );

                        // The current editor loader switches projects for other
                        // basenames. Refuse that path until explicit multi-root
                        // project ownership is integrated, never switch silently.
                        if( !wxFileName( wxString::FromUTF8( request.path() ) ).IsAbsolute()
                                || requested != expected
                                || ( !requested.FileExists() && !request.create_if_missing() ) )
                            return fail( AS_BAD_REQUEST, "An absolute root document path for this project is required; missing files require explicit creation" );

                        KIWAY_PLAYER* player = Kiway.Player( frameType, false );

                        if( player && player->IsModal() )
                            return fail( AS_BUSY, "The requested editor has an active modal operation" );

                        if( player && player->GetCurrentFileName().IsEmpty() )
                            return fail( AS_BUSY, "Close the untitled document explicitly before opening another" );

                        if( player && !player->GetCurrentFileName().IsEmpty()
                                && wxFileName( player->GetCurrentFileName() ) != requested )
                            return fail( AS_BUSY, "Close the existing document explicitly before opening another" );

                        bool created = player == nullptr;

                        try
                        {
                            if( !player )
                                player = Kiway.Player( frameType, true );

                            if( !player )
                                return fail( AS_NOT_READY, "The requested editor could not be created" );

                            int controls = KICTL_KICAD_ONLY;
                            if( request.create_if_missing() )
                                controls |= KICTL_CREATE;

                            if( created && !player->OpenProjectFiles( { requested.GetFullPath() }, controls ) )
                            {
                                player->Destroy();
                                return fail( AS_BAD_REQUEST, "Document load failed; inspect native diagnostics" );
                            }

                            player->Show( true );
                            player->Raise();
                        }
                        catch( const IO_ERROR& error )
                        {
                            if( created && player )
                                player->Destroy();

                            return fail( AS_BAD_REQUEST, error.What().ToStdString() );
                        }

                        commands::GetOpenDocuments query;
                        query.set_type( request.type() );
                        ApiRequest envelope;
                        envelope.mutable_message()->PackFrom( query );
                        API_RESULT result = m_api_server->DispatchToHandlers( envelope );

                        if( !result )
                            return tl::unexpected( result.error() );

                        commands::GetOpenDocumentsResponse documents;

                        if( !result->message().UnpackTo( &documents ) || documents.documents_size() != 1 )
                            return fail( AS_NOT_READY, "The editor did not identify the opened document" );

                        commands::OpenDocumentResponse response;
                        response.mutable_document()->CopyFrom( documents.documents( 0 ) );
                        if( created && requested.FileExists() )
                            m_api_server->RememberLoadedDocument( response.document() );
                        return response;
                    } );
            m_api_common_handler->SetCloseDocumentHandler(
                    [this]( const kiapi::common::commands::CloseDocument& request )
                            -> HANDLER_RESULT<google::protobuf::Empty>
                    {
                        using namespace kiapi::common;
                        auto fail = []( ApiStatusCode code, const std::string& message )
                                -> HANDLER_RESULT<google::protobuf::Empty>
                        {
                            ApiResponseStatus status; status.set_status( code ); status.set_error_message( message );
                            return tl::unexpected( status );
                        };
                        const auto type = request.document().type();
                        if( type != types::DOCTYPE_SCHEMATIC && type != types::DOCTYPE_PCB )
                            return fail( AS_UNIMPLEMENTED, "This graphical close operation supports schematics and PCBs" );
                        if( !m_api_server->IsCleanCloseActive() )
                            return fail( AS_BAD_REQUEST, "Use CheckedCloseDocument with an exact native state observation" );
                        const auto frameType = type == types::DOCTYPE_SCHEMATIC ? FRAME_SCH : FRAME_PCB_EDITOR;
                        KIWAY_PLAYER* player = Kiway.Player( frameType, false );
                        if( !player || player->IsBeingDeleted() ) return fail( AS_BAD_REQUEST, "Requested editor is not open" );
                        if( player->IsModal() || player->IsContentModified() )
                            return fail( AS_BUSY, "Close will not discard unsaved edits or interrupt a modal operation" );
                        auto hasDialog = [&]( auto&& self, wxWindow* window ) -> bool
                        {
                            for( wxWindow* child : window->GetChildren() )
                                if( ( wxDynamicCast( child, wxDialog ) && child->IsShown() ) || self( self, child ) )
                                    return true;
                            return false;
                        };
                        if( hasDialog( hasDialog, player ) )
                            return fail( AS_BUSY, "Close the editor dialogs before closing this document" );
                        if( type == types::DOCTYPE_SCHEMATIC
                                && ( Kiway.Player( FRAME_SIMULATOR, false ) || Kiway.Player( FRAME_SCH_SYMBOL_EDITOR, false ) ) )
                            return fail( AS_BUSY, "Close the associated simulator or symbol editor explicitly first" );
                        if( type == types::DOCTYPE_PCB && Kiway.Player( FRAME_FOOTPRINT_EDITOR, false ) )
                            return fail( AS_BUSY, "Close the associated footprint editor explicitly first" );
                        if( type == types::DOCTYPE_PCB
                                && wxWindow::FindWindowByName( QUALIFIED_VIEWER3D_FRAMENAME( player ) ) )
                            return fail( AS_BUSY, "Close the associated 3D viewer explicitly before closing this document" );

                        kiapi::automation::v1::ReadDocumentLifecycleState read;
                        read.mutable_document()->CopyFrom( request.document() );
                        ApiRequest envelope; envelope.mutable_message()->PackFrom( read );
                        auto observed = m_api_server->DispatchToHandlers( envelope );
                        if( !observed ) return tl::unexpected( observed.error() );
                        kiapi::automation::v1::DocumentLifecycleState state;
                        if( observed->status().status() != AS_OK || !observed->message().UnpackTo( &state )
                                || state.native_content_dirty() || state.clean_checkpoint_sha256().empty()
                                || state.clean_checkpoint_sha256() != state.state_sha256() )
                            return fail( AS_BUSY, "Document has not reached a verified clean load/save checkpoint" );
                        for( const auto& file : state.file_baselines() )
                            if( file.status() != kiapi::automation::v1::NFBS_UNCHANGED )
                                return fail( AS_BUSY, "Document files changed outside KiCad; close was refused" );
                        if( !Kiway.PlayerClose( frameType, false ) )
                            return fail( AS_BUSY, "Native editor refused to close; no forced close was attempted" );
                        if( Kiway.Player( frameType, false ) )
                            return fail( AS_BUSY, "Native editor close has not completed" );
                        return google::protobuf::Empty{};
                    } );
        }

        m_api_server->SetReadyToReply();
    }

    return true;
}


int PGM_KICAD::OnPgmRun()
{
    return 0;
}


void PGM_KICAD::OnPgmExit()
{
    // Signal all background library preloads to abort before waiting for the thread pool.
    // The design block preload runs on the global thread pool and checks this flag; without
    // setting it here the pool wait below can block for up to 120 seconds.
    m_libraryPreloadAbort.store( true );

    // Invalid command-line startup can return before InitPgm creates the pool.
    // Quiesce only an initialized pool; do not obtain a reference to null or
    // recreate workers during cleanup. This also permits repeated teardown.
    if( m_singleton.m_ThreadPool )
    {
        m_singleton.m_ThreadPool->purge();
        m_singleton.m_ThreadPool->wait();
    }

    Kiway.OnKiwayEnd();

    m_api_server.reset();

    if( m_settings_manager && m_settings_manager->IsOK() )
    {
        SaveCommonSettings();
        m_settings_manager->Save();
    }

    // Destroy PGM_KICAD earlier than wxApp and static destruction would
    Destroy();
    if( GIT_BACKEND* backend = GetGitBackend() )
    {
        backend->Shutdown();
        delete backend;
        SetGitBackend( nullptr );
    }
}


void PGM_KICAD::MacOpenFile( const wxString& aFileName )
{
#if defined(__WXMAC__)

    KICAD_MANAGER_FRAME* frame = (KICAD_MANAGER_FRAME*) App().GetTopWindow();

    if( !aFileName.empty() && wxFileExists( aFileName ) )
        frame->LoadProject( wxFileName( aFileName ) );

#endif
}


void PGM_KICAD::Destroy()
{
    // unlike a normal destructor, this is designed to be called more
    // than once safely:

    m_bm.End();

    PGM_BASE::Destroy();
}


KIWAY  Kiway( KFCTL_CPP_PROJECT_SUITE );

#ifdef NDEBUG
// Define a custom assertion handler
void CustomAssertHandler(const wxString& file,
                         int line,
                         const wxString& func,
                         const wxString& cond,
                         const wxString& msg)
{
    Pgm().HandleAssert( file, line, func, cond, msg );
}
#endif

/**
 * Not publicly visible because most of the action is in #PGM_KICAD these days.
 */
struct APP_KICAD : public wxApp
{
    APP_KICAD() : wxApp()
    {
        SetPgm( &program );

        // Init the environment each platform wants
        KIPLATFORM::ENV::Init();
    }


    bool OnInit()           override
    {
#ifdef NDEBUG
        // These checks generate extra assert noise
        wxSizerFlags::DisableConsistencyChecks();
        wxDISABLE_DEBUG_SUPPORT();
        wxSetAssertHandler( CustomAssertHandler );
#endif

        // Perform platform-specific init tasks
        if( !KIPLATFORM::APP::Init() )
            return false;

#ifndef DEBUG
        // Enable logging traces to the console in release build.
        // This is usually disabled, but it can be useful for users to run to help
        // debug issues and other problems.
        if( wxGetEnv( wxS( "KICAD_ENABLE_WXTRACE" ), nullptr ) )
        {
            wxLog::EnableLogging( true );
            wxLog::SetLogLevel( wxLOG_Trace );
        }
#endif

        if( !program.OnPgmInit() )
        {
            // Delete the windows a failed start destroyed (PGM_KICAD::OnPgmInit()) while the
            // program they use still exists, as a normal exit does before OnPgmExit().
            DeletePendingObjects();
            program.OnPgmExit();
            return false;
        }

        return true;
    }

    int OnExit() override
    {
        // Drain wxPendingDelete (frames deferred via Destroy()) before tearing down
        // PGM_BASE singletons. On macOS the dock-quit path leaves frames in this
        // queue at OnExit() time, and their canvas destructors call into
        // Pgm().GetGLContextManager(). Running OnPgmExit() first would null that
        // pointer out from under them. See https://gitlab.com/kicad/code/kicad/-/issues/23373
        int ret = wxApp::OnExit();

        // Avoid wxLog crashing when used in destructors invoked from OnPgmExit().
        wxLog::EnableLogging( false );

        program.OnPgmExit();
        return ret;
    }


    int OnRun() override
    {
        try
        {
            return wxApp::OnRun();
        }
        catch(...)
        {
            Pgm().HandleException( std::current_exception() );
        }

        return -1;
    }


    void OnUnhandledException() override
    {
        Pgm().HandleException( std::current_exception(), true );
    }


    int FilterEvent( wxEvent& aEvent ) override
    {
        if( aEvent.GetEventType() == wxEVT_SHOW )
        {
            wxShowEvent& event = static_cast<wxShowEvent&>( aEvent );
            wxDialog*    dialog = dynamic_cast<wxDialog*>( event.GetEventObject() );

            std::vector<void*>& dlgs = Pgm().m_ModalDialogs;

            if( dialog )
            {
                if( event.IsShown() && dialog->IsModal() )
                {
                    dlgs.push_back( dialog );
                }
                // Under GTK, sometimes the modal flag is cleared before hiding
                else if( !event.IsShown() && !dlgs.empty() )
                {
                    // If we close the expected dialog, remove it from our stack
                    if( dlgs.back() == dialog )
                        dlgs.pop_back();
                    // If an out-of-order, remove all dialogs added after the closed one
                    else if( auto it = std::find( dlgs.begin(), dlgs.end(), dialog ) ; it != dlgs.end() )
                        dlgs.erase( it, dlgs.end() );
                }
            }
        }

        return Event_Skip;
    }

#if defined( DEBUG )
    /**
     * Process any unhandled events at the application level.
     */
    bool ProcessEvent( wxEvent& aEvent ) override
    {
        if( aEvent.GetEventType() == wxEVT_CHAR || aEvent.GetEventType() == wxEVT_CHAR_HOOK )
        {
            wxKeyEvent* keyEvent = static_cast<wxKeyEvent*>( &aEvent );

            if( keyEvent )
            {
                wxLogTrace( kicadTraceKeyEvent, "APP_KICAD::ProcessEvent %s", dump( *keyEvent ) );
            }
        }

        aEvent.Skip();
        return false;
    }

    /**
     * Override main loop exception handling on debug builds.
     *
     * It can be painfully difficult to debug exceptions that happen in wxUpdateUIEvent
     * handlers.  The override provides a bit more useful information about the exception
     * and a breakpoint can be set to pin point the event where the exception was thrown.
     */
    bool OnExceptionInMainLoop() override
    {
        try
        {
            throw;
        }
        catch(...)
        {
            Pgm().HandleException( std::current_exception() );
        }

        return false;   // continue on. Return false to abort program
    }
#endif

    /**
     * Set MacOS file associations.
     *
     * @see http://wiki.wxwidgets.org/WxMac-specific_topics
     */
#if defined( __WXMAC__ )
    void MacOpenFile( const wxString& aFileName ) override
    {
        Pgm().MacOpenFile( aFileName );
    }
#endif
};

IMPLEMENT_APP( APP_KICAD )


// The C++ project manager supports one open PROJECT, so Prj() calls within
// this link image need this function.
PROJECT& Prj()
{
    return Kiway.Prj();
}
