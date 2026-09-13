/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2024 Jon Evans <jon@craftyjon.com>
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

#ifndef KICAD_API_SERVER_H
#define KICAD_API_SERVER_H

#include <atomic>
#include <cstdint>
#include <memory>
#include <set>
#include <string>

#include <wx/event.h>
#include <wx/filename.h>

#include <kicommon.h>
#include <api/api_handler.h>

class API_HANDLER;
class DOCUMENT_LIFECYCLE_CONTROLLER;
class KINNG_REQUEST_SERVER;
class KINNG_PUBLISHER;
class wxEvtHandler;
namespace kiapi::automation::v1 { class SchematicCommitNotification; }
namespace kiapi::common::types { class DocumentSpecifier; }


wxDECLARE_EVENT( API_REQUEST_EVENT, wxCommandEvent );


class KICOMMON_API KICAD_API_SERVER : public wxEvtHandler
{
public:
    KICAD_API_SERVER( bool aAutoStart = true );

    ~KICAD_API_SERVER();

    void Start();

    void Stop();

    bool Running() const;

    /**
     * Adds a new request handler to the server.  Each handler maintains its own list of API
     * messages that it knows how to handle, and the server will pass every incoming message to all
     * handlers in succession until one of them handles it.
     *
     * The caller is responsible for the lifetime of the handler and must call DeregisterHandler
     * before the pointer is freed.
     *
     * @param aHandler is a pointer (non-owned) to API_HANDLER
     */
    void RegisterHandler( API_HANDLER* aHandler );

    void DeregisterHandler( API_HANDLER* aHandler );

    // UI-thread-only dispatch for native operations that need a typed editor
    // result after creating its window. Does not send an IPC reply or bypass
    // handler document validation.
    API_RESULT DispatchToHandlers( ApiRequest& aRequest );
    void RememberLoadedDocument( const kiapi::common::types::DocumentSpecifier& aDocument );
    bool IsCleanCloseActive() const;

    void SetReadyToReply( bool aReady = true )
    {
        m_readyToReply.store( aReady, std::memory_order_release );
    }

    void SetSocketPath( const wxString& aSocketPath )
    {
        m_socketPathOverride = aSocketPath;
    }

    std::string SocketPath() const;

    const std::string& Token() const { return m_token; }

    void ConfigureAutomation( const std::string& aInstanceId, const std::string& aProjectPath )
    {
        m_automationInstanceId = aInstanceId;
        m_automationProjectPath = aProjectPath;
    }

    bool IsAutomation() const { return !m_automationInstanceId.empty(); }
    const std::string& AutomationInstanceId() const { return m_automationInstanceId; }

    // Editor-thread only, after the native journal has accepted the commit.
    void PublishSchematicCommit( const kiapi::automation::v1::SchematicCommitNotification& aCommit );

    /**
     * Return the default API socket path (without the ipc:// scheme).
     */
    static wxFileName StandardSocketPath();

    /**
     * Return the default API socket URL (including the ipc:// scheme).
     */
    static std::string StandardSocketUrl();

private:

    /**
     * Callback that executes on the server thread and generates an event that will be handled by
     * the wxWidgets event loop to process an incoming request.  Temporarily takes ownership of the
     * request pointer so that it can be passed through the event system.
     *
     * @param aRequest is a pointer to a string containing bytes that came in over the wire
     */
    void onApiRequest( std::string* aRequest );

    /**
     * Event handler that receives the event on the main thread sent by onApiRequest
     * @param aEvent will contain a pointer to an incoming API request string in the client data
     */
    void handleApiEvent( wxCommandEvent& aEvent );

    void handleApiRequestString( std::string& aRequestString );

    void log( const std::string& aOutput );

    std::unique_ptr<KINNG_REQUEST_SERVER> m_server;
    std::unique_ptr<KINNG_PUBLISHER> m_eventPublisher;
    std::string m_eventEndpoint;
    std::string m_eventEpoch;
    uint64_t m_eventSequence = 0;

    std::set<API_HANDLER*> m_handlers;
    std::unique_ptr<DOCUMENT_LIFECYCLE_CONTROLLER> m_lifecycle;

    std::string m_token;
    std::string m_automationInstanceId;
    std::string m_automationProjectPath;

    std::atomic<bool> m_readyToReply;

    wxString m_socketPathOverride;

    static wxString s_logFileName;

    wxFileName m_logFilePath;
};

#endif //KICAD_API_SERVER_H
