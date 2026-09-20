/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_RECURSIVE_DIAGRAM_FRAME_H
#define KICAD_RECURSIVE_DIAGRAM_FRAME_H

#include <api/common/commands/recursive_diagram_commands.pb.h>
#include <wx/frame.h>
#include <wx/geometry.h>
#include <wx/process.h>
#include <wx/timer.h>
#include <array>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

class wxButton;
class wxDC;
class wxPanel;
class wxStaticText;
class wxTextCtrl;
class wxToolBar;

/** One native diagram level with revision-bound requirement drafts. XML validation
 * and publication remain in the compiled companion. No action invokes an agent. */
class RECURSIVE_DIAGRAM_FRAME : public wxFrame
{
public:
    RECURSIVE_DIAGRAM_FRAME( wxWindow* aParent,
            const kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor& aRequest );
    ~RECURSIVE_DIAGRAM_FRAME() override;
    const std::string& DocumentId() const { return m_request.document_id(); }
    const std::string& SourcePath() const { return m_request.source_path(); }
    bool IsClosing() const { return m_closing; }
    kiapi::automation::diagrams::v1::RecursiveDiagramEditorState State() const;

private:
    using SELECTION = kiapi::automation::diagrams::v1::BlockSelectionData;
    using REVISION = kiapi::automation::diagrams::v1::BlockRevisionData;
    using DRAFT = kiapi::automation::diagrams::v1::BlockDraftData;
    using REQUEST = kiapi::automation::diagrams::v1::RecursiveFileRequest;

    const REVISION* revision( const SELECTION& aSelection ) const;
    const kiapi::automation::diagrams::v1::RequirementRevisionData* requirements( const REVISION& aRevision ) const;
    const REVISION* current() const;
    int version( const REVISION& aRevision ) const;
    bool findPath( const std::string& aBlockId, std::vector<SELECTION>& aPath ) const;
    void load( const std::string& aExpectedToken = {} );
    void execute( REQUEST aRequest );
    void drain();
    void completed( wxProcessEvent& aEvent );
    void refresh();
    void select( const std::string& aBlockId );
    void selectConnection( const std::string& aConnectionId );
    const kiapi::automation::diagrams::v1::ConnectionRevisionData* connection( const std::string& aConnectionId ) const;
    void navigate( const std::string& aBlockId, bool aRemember = true );
    bool confirmChange();
    void makeDraft( const REVISION& aRevision );
    void edit();
    void save();
    void decline();
    void history( int aField );
    void undo( bool aRedo );
    void close( wxCloseEvent& aEvent );
    void paint( wxDC& aDC );
    void click( wxMouseEvent& aEvent );
    void fit();
    wxRect nodeRect( int aIndex ) const;
    wxPoint endpoint( const kiapi::automation::diagrams::v1::DiagramEndpointBindingData& aEndpoint, bool aFirst ) const;
    std::array<wxPoint, 4> connectionPath( const kiapi::automation::diagrams::v1::ConnectionRevisionData& aConnection, int aEndpoint ) const;

    kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor m_request;
    kiapi::automation::diagrams::v1::RecursiveEditorDocument m_document;
    REQUEST m_activeRequest;
    DRAFT m_draft, m_savedDraft;
    kiapi::automation::diagrams::v1::ConnectionDraftData m_connectionDraft, m_savedConnectionDraft;
    std::vector<kiapi::automation::diagrams::v1::ConnectionDraftData> m_connectionUndo, m_connectionRedo;
    std::string m_connectionId;
    std::optional<std::string> m_pendingConnection;
    std::vector<DRAFT> m_undo, m_redo;
    std::vector<SELECTION> m_path;
    std::vector<std::string> m_back;
    std::string m_selected, m_pendingScope, m_pendingSelected, m_errorCode, m_error;
    bool m_rememberNavigation = true, m_closeAfterSave = false;
    bool m_ready = false, m_dirty = false, m_rendered = false, m_updating = false, m_closing = false;
    uint64_t m_viewRevision = 0, m_saveCount = 0;
    unsigned m_rebaseAttempts = 0;
    wxPanel* m_canvas;
    wxStaticText* m_breadcrumb;
    wxStaticText* m_owner;
    wxStaticText* m_savedVersion;
    wxButton* m_openDiagram;
    wxButton* m_save;
    wxButton* m_decline;
    std::array<wxTextCtrl*, 3> m_fields;
    std::array<wxButton*, 3> m_history;
    wxToolBar* m_toolbar;
    double m_scale = 1.0;
    wxPoint2DDouble m_origin{ 0, 0 };
    struct VIEW { double scale; wxPoint2DDouble origin; std::string selected; };
    std::map<std::string, VIEW> m_views;
    std::unique_ptr<wxProcess> m_process;
    wxTimer m_ioTimer;
    long m_pid = 0;
    std::string m_stdout, m_stderr;
};

#endif
