/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "recursive_diagram_frame.h"
#include "dialogs/dialog_diagram_field_history.h"
#include "dialogs/dialog_diagram_conflict.h"
#include <bitmaps.h>
#include <kiid.h>
#include <google/protobuf/util/json_util.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <wx/button.h>
#include <wx/choice.h>
#include <wx/dcbuffer.h>
#include <wx/filename.h>
#include <wx/menu.h>
#include <wx/msgdlg.h>
#include <wx/panel.h>
#include <wx/scrolwin.h>
#include <wx/settings.h>
#include <wx/sizer.h>
#include <wx/splitter.h>
#include <wx/stattext.h>
#include <wx/textctrl.h>
#include <wx/toolbar.h>

namespace D = kiapi::automation::diagrams::v1;
namespace
{
enum { BACK = wxID_HIGHEST + 3900, UP, FIT, NOTE };
wxString text( const std::string& value ) { return wxString::FromUTF8( value ); }
std::string utf8( const wxString& value ) { return value.ToStdString( wxConvUTF8 ); }
std::string freshId() { return utf8( KIID().AsString() ); }
bool same( const D::BlockSelectionData& a, const D::BlockSelectionData& b )
{ return a.block_id() == b.block_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
std::string field( const D::RequirementFieldsData& fields, int which )
{ return which == 0 ? fields.general() : which == 1 ? fields.schematic() : fields.routing(); }
void setField( D::RequirementFieldsData* fields, int which, const std::string& value )
{ if( which == 0 ) fields->set_general( value ); else if( which == 1 ) fields->set_schematic( value ); else fields->set_routing( value ); }
void editorOrigin( D::DiagramRevisionOriginData* origin, const std::string& summary )
{
    origin->set_kind( D::DAK_EDITOR ); origin->set_actor( "Native editor" ); origin->set_summary( summary );
    auto now = std::chrono::system_clock::now().time_since_epoch(); auto seconds = std::chrono::duration_cast<std::chrono::seconds>( now );
    origin->mutable_recorded_at()->set_seconds( seconds.count() );
    origin->mutable_recorded_at()->set_nanos( static_cast<int>( std::chrono::duration_cast<std::chrono::nanoseconds>( now - seconds ).count() / 100 * 100 ) );
}
}

RECURSIVE_DIAGRAM_FRAME::RECURSIVE_DIAGRAM_FRAME( wxWindow* parent, const D::OpenRecursiveDiagramEditor& request ) :
        wxFrame( parent, wxID_ANY, _( "Structural diagram" ), wxDefaultPosition, wxSize( 1536, 1024 ) ),
        m_request( request ), m_ioTimer( this )
{
    SetName( "RecursiveDiagramEditor" ); SetMinSize( FromDIP( wxSize( 900, 650 ) ) );
    wxFont body = GetFont(); body.SetPointSize( std::max( 12, body.GetPointSize() ) ); SetFont( body );
    auto* menu = new wxMenuBar(); auto* file = new wxMenu();
    file->Append( wxID_SAVE, _( "Save\tCtrl+S" ) ); file->AppendSeparator(); file->Append( wxID_CLOSE, _( "Close\tCtrl+W" ) );
    menu->Append( file, _( "File" ) ); SetMenuBar( menu );
    m_toolbar = CreateToolBar( wxTB_HORIZONTAL | wxTB_FLAT | wxTB_TEXT );
    m_toolbar->AddTool( BACK, _( "Back" ), KiBitmap( BITMAPS::left ) );
    m_toolbar->AddTool( UP, _( "Up" ), KiBitmap( BITMAPS::up ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( wxID_UNDO, _( "Undo" ), KiBitmap( BITMAPS::undo ) );
    m_toolbar->AddTool( wxID_REDO, _( "Redo" ), KiBitmap( BITMAPS::redo ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( FIT, _( "Fit" ), KiBitmap( BITMAPS::zoom_fit_in_page ) );
    m_toolbar->AddTool( NOTE, _( "Note" ), KiBitmap( BITMAPS::add_textbox ) ); m_toolbar->Realize();
    auto* splitter = new wxSplitterWindow( this, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxSP_LIVE_UPDATE );
    splitter->SetMinimumPaneSize( FromDIP( 300 ) ); splitter->SetSashGravity( 1.0 );
    auto* diagram = new wxPanel( splitter ); auto* main = new wxBoxSizer( wxVERTICAL );
    m_breadcrumb = new wxStaticText( diagram, wxID_ANY, _( "Loading diagram…" ) );
    m_breadcrumb->SetName( "RecursiveDiagramPath" );
    main->Add( m_breadcrumb, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_canvas = new wxPanel( diagram ); m_canvas->SetName( "RecursiveDiagramCanvas" );
    m_canvas->SetBackgroundStyle( wxBG_STYLE_PAINT ); main->Add( m_canvas, 1, wxEXPAND ); diagram->SetSizer( main );
    auto* inspector = new wxPanel( splitter ); inspector->SetMinSize( FromDIP( wxSize( 380, -1 ) ) );
    auto* side = new wxBoxSizer( wxVERTICAL );
    auto* scroll = new wxScrolledWindow( inspector ); scroll->SetScrollRate( 0, FromDIP( 12 ) );
    m_inspectorScroll = scroll;
    auto* fields = new wxBoxSizer( wxVERTICAL );
    m_owner = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_owner->SetFont( GetFont().Bold().Larger() );
    fields->Add( m_owner, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_savedVersion = new wxStaticText( scroll, wxID_ANY, wxEmptyString );
    fields->Add( m_savedVersion, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_openDiagram = new wxButton( scroll, wxID_ANY, _( "Open diagram" ) ); m_openDiagram->SetName( "RecursiveOpenDiagram" );
    fields->Add( m_openDiagram, 0, wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_endpointHeading = new wxStaticText( scroll, wxID_ANY, _( "Endpoints" ) );
    fields->Add( m_endpointHeading, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_endpoints = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition,
            FromDIP( wxSize( 320, 125 ) ), wxTE_MULTILINE | wxTE_READONLY );
    m_endpoints->SetName( "RecursiveConnectionEndpoints" );
    fields->Add( m_endpoints, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    const wxString labels[] = { _( "General requirements" ), _( "Schematic requirements" ), _( "Routing requirements" ) };
    for( int i = 0; i < 3; ++i )
    {
        auto* heading = new wxBoxSizer( wxHORIZONTAL );
        heading->Add( new wxStaticText( scroll, wxID_ANY, labels[i] ), 1, wxALIGN_CENTER_VERTICAL );
        m_history[i] = new wxButton( scroll, wxID_ANY, _( "History" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
        m_history[i]->SetName( wxString::Format( "RecursiveFieldHistory%d", i ) );
        heading->Add( m_history[i], 0 ); fields->Add( heading, 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
        m_fields[i] = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition,
                FromDIP( wxSize( 320, 90 ) ), wxTE_MULTILINE );
        m_fields[i]->SetName( wxString::Format( "RecursiveRequirements%d", i ) );
        fields->Add( m_fields[i], 0, wxEXPAND | wxALL, FromDIP( 12 ) );
        m_fields[i]->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) edit(); } );
        m_history[i]->Bind( wxEVT_BUTTON, [this, i]( wxCommandEvent& ) { history( i ); } );
    }
    auto* commentsHeading = new wxBoxSizer( wxHORIZONTAL );
    commentsHeading->Add( new wxStaticText( scroll, wxID_ANY, _( "Comments" ) ), 1, wxALIGN_CENTER_VERTICAL );
    m_commentChoice = new wxChoice( scroll, wxID_ANY, wxDefaultPosition, FromDIP( wxSize( 190, -1 ) ) );
    m_commentChoice->SetName( "RecursiveCommentSelection" ); commentsHeading->Add( m_commentChoice, 0 );
    fields->Add( commentsHeading, 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
    m_comments = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition, FromDIP( wxSize( 320, 110 ) ), wxTE_MULTILINE );
    m_comments->SetName( "RecursiveComments" ); fields->Add( m_comments, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_comments->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) editComment(); } );
    m_commentChoice->Bind( wxEVT_CHOICE, [this]( wxCommandEvent& )
    {
        int chosen = m_commentChoice->GetSelection();
        if( chosen >= 0 && chosen < static_cast<int>( m_commentIds.size() ) )
        { m_commentId = m_commentIds[chosen]; m_newComment = m_commentId.empty(); fillComments(); m_comments->SetFocus(); }
    } );
    scroll->SetSizer( fields ); side->Add( scroll, 1, wxEXPAND );
    auto* actions = new wxBoxSizer( wxHORIZONTAL ); actions->AddStretchSpacer();
    m_decline = new wxButton( inspector, wxID_ANY, _( "&Decline" ) ); m_decline->SetName( "RecursiveDecline" );
    m_save = new wxButton( inspector, wxID_SAVE, _( "Save" ) ); m_save->SetName( "RecursiveSave" );
    actions->Add( m_decline, 0, wxRIGHT, FromDIP( 12 ) ); actions->Add( m_save, 0 );
    side->Add( actions, 0, wxEXPAND | wxALL, FromDIP( 12 ) ); inspector->SetSizer( side );
    splitter->SplitVertically( diagram, inspector, FromDIP( 1100 ) );
    auto* frameSizer = new wxBoxSizer( wxVERTICAL ); frameSizer->Add( splitter, 1, wxEXPAND ); SetSizer( frameSizer ); CreateStatusBar();
    m_canvas->Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { wxAutoBufferedPaintDC dc( m_canvas ); paint( dc ); } );
    m_canvas->Bind( wxEVT_SIZE, [this]( wxSizeEvent& event ) { m_rendered = false; ++m_viewRevision; event.Skip(); } );
    m_canvas->Bind( wxEVT_LEFT_DOWN, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_canvas->Bind( wxEVT_LEFT_DCLICK, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_canvas->Bind( wxEVT_MOTION, &RECURSIVE_DIAGRAM_FRAME::moveNote, this );
    m_canvas->Bind( wxEVT_LEFT_UP, [this]( wxMouseEvent& ) { finishNoteDrag(); } );
    m_canvas->Bind( wxEVT_MOUSE_CAPTURE_LOST, [this]( wxMouseCaptureLostEvent& ) { finishNoteDrag(); } );
    m_canvas->Bind( wxEVT_KEY_DOWN, [this]( wxKeyEvent& event )
    {
        if( !m_ready || m_process || !current() ) { event.Skip(); return; }
        int index = -1;
        for( int i = 0; i < current()->children_size(); ++i ) if( current()->children( i ).block_id() == m_selected ) index = i;
        if( event.GetKeyCode() == WXK_RIGHT || event.GetKeyCode() == WXK_DOWN )
        { if( current()->children_size() ) select( current()->children( ( index + 1 ) % current()->children_size() ).block_id() ); return; }
        if( event.GetKeyCode() == WXK_LEFT || event.GetKeyCode() == WXK_UP )
        { if( current()->children_size() ) select( current()->children( ( index + current()->children_size() - 1 ) % current()->children_size() ).block_id() ); return; }
        if( event.GetKeyCode() == WXK_RETURN ) { navigate( m_selected ); return; }
        if( event.GetKeyCode() == 'N' )
        { m_noteMode = true; m_canvas->SetCursor( wxCursor( wxCURSOR_CROSS ) ); SetStatusText( _( "Click the diagram to place a comment." ) ); return; }
        if( event.GetKeyCode() == 'L' && current()->local_diagram().connections_size() )
        {
            int selected = -1;
            for( int i = 0; i < current()->local_diagram().connections_size(); ++i )
                if( current()->local_diagram().connections( i ).connection_id() == m_connectionId ) selected = i;
            selectConnection( current()->local_diagram().connections( ( selected + 1 ) % current()->local_diagram().connections_size() ).connection_id() ); return;
        }
        if( event.GetKeyCode() == WXK_BACK && m_path.size() > 1 ) { navigate( m_path[m_path.size() - 2].block_id() ); return; }
        event.Skip();
    } );
    m_openDiagram->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { navigate( m_selected ); } );
    m_save->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { save(); } );
    m_decline->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { decline(); } );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { save(); }, wxID_SAVE );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { Close(); }, wxID_CLOSE );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( !m_back.empty() ) { auto id = m_back.back(); navigate( id, false ); if( current() && current()->selection().block_id() == id ) m_back.pop_back(); refresh(); } }, BACK );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( m_path.size() > 1 ) navigate( m_path[m_path.size() - 2].block_id() ); }, UP );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { fit(); }, FIT );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& )
    { m_noteMode = true; m_canvas->SetFocus(); m_canvas->SetCursor( wxCursor( wxCURSOR_CROSS ) ); SetStatusText( _( "Click the diagram to place a comment." ) ); }, NOTE );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( false ); }, wxID_UNDO );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( true ); }, wxID_REDO );
    Bind( wxEVT_TIMER, [this]( wxTimerEvent& ) { drain(); }, m_ioTimer.GetId() );
    Bind( wxEVT_END_PROCESS, &RECURSIVE_DIAGRAM_FRAME::completed, this );
    Bind( wxEVT_CLOSE_WINDOW, &RECURSIVE_DIAGRAM_FRAME::close, this );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    {
        if( event.ControlDown() && event.GetKeyCode() >= '1' && event.GetKeyCode() <= '5' )
        { if( m_ready && !m_process ) { if( event.GetKeyCode() == '5' ) { if( m_commentChoice->IsShown() ) m_commentChoice->SetFocus(); }
            else if( event.GetKeyCode() == '4' ) m_comments->SetFocus(); else m_fields[event.GetKeyCode() - '1']->SetFocus(); } return; }
        if( event.AltDown() && event.GetKeyCode() == 'H' )
        { for( int i = 0; i < 3; ++i ) if( wxWindow::FindFocus() == m_fields[i] ) { history( i ); return; } }
        if( event.GetKeyCode() == WXK_ESCAPE && !m_process )
        { m_noteMode = false; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); finishNoteDrag(); m_canvas->SetFocus(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'S' ) { save(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'W' ) { Close(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Z' ) { undo( false ); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Y' ) { undo( true ); return; }
        event.StopPropagation(); event.Skip();
    } );
    refresh(); CallAfter( [this, splitter]
    { splitter->SetSashPosition( splitter->GetClientSize().x - FromDIP( 400 ) ); load( m_request.expected_source_token() ); } );
}

RECURSIVE_DIAGRAM_FRAME::~RECURSIVE_DIAGRAM_FRAME()
{
    m_ioTimer.Stop();
    if( m_process ) { m_process->Detach(); wxProcess::Kill( m_pid, wxSIGTERM, wxKILL_CHILDREN ); }
}

const RECURSIVE_DIAGRAM_FRAME::REVISION* RECURSIVE_DIAGRAM_FRAME::revision( const SELECTION& selection ) const
{
    for( const auto& item : m_document.graph().revisions() ) if( same( item.selection(), selection ) ) return &item;
    return nullptr;
}
const D::RequirementRevisionData* RECURSIVE_DIAGRAM_FRAME::requirements( const REVISION& item ) const
{
    for( const auto& history : m_document.graph().requirement_histories() ) if( history.state_id() == item.selection().state_id() )
        for( const auto& value : history.revisions() ) if( value.id() == item.requirement_revision_id() ) return &value;
    return nullptr;
}
const RECURSIVE_DIAGRAM_FRAME::REVISION* RECURSIVE_DIAGRAM_FRAME::current() const
{ return m_path.empty() ? nullptr : revision( m_path.back() ); }
const D::ConnectionRevisionData* RECURSIVE_DIAGRAM_FRAME::connection( const std::string& id ) const
{
    if( !current() ) return nullptr;
    for( const auto& selected : current()->local_diagram().connections() ) if( selected.connection_id() == id )
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == current()->selection().block_id() )
            for( const auto& item : archive.revisions() ) if( item.selection().revision_id() == selected.revision_id()
                && item.selection().connection_id() == selected.connection_id() && item.selection().state_id() == selected.state_id() ) return &item;
    return nullptr;
}
int RECURSIVE_DIAGRAM_FRAME::version( const REVISION& item ) const
{
    int result = 1; const REVISION* found = &item;
    while( found->has_parent_revision_id() && result <= m_document.graph().revisions_size() )
    {
        SELECTION parent = found->selection(); parent.set_revision_id( found->parent_revision_id() );
        found = revision( parent ); if( !found ) return result; ++result;
    }
    return result;
}
bool RECURSIVE_DIAGRAM_FRAME::findPath( const std::string& blockId, std::vector<SELECTION>& path ) const
{
    std::vector<std::vector<SELECTION>> pending{ { m_document.graph().selected_root() } };
    size_t count = 0;
    while( !pending.empty() && count++ <= static_cast<size_t>( m_document.graph().revisions_size() ) )
    {
        auto candidate = std::move( pending.back() ); pending.pop_back();
        auto* item = revision( candidate.back() ); if( !item ) continue;
        if( item->selection().block_id() == blockId ) { path = std::move( candidate ); return true; }
        for( const auto& child : item->children() ) { auto next = candidate; next.push_back( child ); pending.push_back( std::move( next ) ); }
    }
    return false;
}

void RECURSIVE_DIAGRAM_FRAME::load( const std::string& expected )
{
    REQUEST request; request.set_action( D::RFA_READ ); request.set_expected_source_token( expected ); execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::execute( REQUEST request )
{
    if( m_process ) return;
    request.set_schema_version( 1 ); request.set_repository_root( m_request.repository_root() );
    request.set_source_path( m_request.source_path() ); request.set_document_id( m_request.document_id() );
    std::string json;
    if( !google::protobuf::util::MessageToJsonString( request, &json ).ok() ) return;
    m_activeRequest = std::move( request ); m_error.clear(); m_errorCode.clear(); m_stdout.clear(); m_stderr.clear();
    m_process = std::make_unique<wxProcess>( this ); m_process->Redirect();
    wxString helper = text( m_request.helper_path() ), dotnet = "dotnet", mode = "--diagram-file";
    const wxChar* binary[] = { helper.c_str(), mode.c_str(), nullptr };
    const wxChar* managed[] = { dotnet.c_str(), helper.c_str(), mode.c_str(), nullptr };
    m_pid = wxExecute( helper.EndsWith( ".dll" ) ? managed : binary, wxEXEC_ASYNC | wxEXEC_MAKE_GROUP_LEADER, m_process.get() );
    if( m_pid <= 0 ) { m_process.reset(); m_errorCode = "companion_start_failed"; m_error = "The compiled companion could not start."; refresh(); return; }
    m_process->GetOutputStream()->Write( json.data(), json.size() ); m_process->CloseOutput(); m_ioTimer.Start( 50 ); refresh();
}
void RECURSIVE_DIAGRAM_FRAME::drain()
{
    if( !m_process ) return;
    auto read = []( wxInputStream* stream, std::string& out )
    { if( !stream ) return; char buffer[4096]; while( stream->CanRead() ) { stream->Read( buffer, sizeof( buffer ) ); size_t count = stream->LastRead(); if( !count ) break; out.append( buffer, count ); } };
    read( m_process->GetInputStream(), m_stdout ); read( m_process->GetErrorStream(), m_stderr );
}
void RECURSIVE_DIAGRAM_FRAME::completed( wxProcessEvent& event )
{
    if( !m_process || event.GetPid() != m_pid ) return;
    drain(); m_ioTimer.Stop(); m_process.reset();
    D::RecursiveFileResult result;
    bool parsed = google::protobuf::util::JsonStringToMessage( m_stdout, &result ).ok();
    if( m_activeRequest.action() == D::RFA_SAVE_BLOCK || m_activeRequest.action() == D::RFA_SAVE_CONNECTION ) ++m_saveCount;
    if( event.GetExitCode() != 0 || !parsed || !result.success() )
    {
        m_errorCode = parsed ? result.error_code() : "invalid_companion_response";
        m_error = parsed && !result.error_message().empty() ? result.error_message() : "The operation failed; the current draft remains open.";
        if( m_activeRequest.action() == D::RFA_SAVE_BLOCK && m_errorCode == "recursive_block_file_changed" && m_rebaseAttempts++ < 2 )
        {
            REQUEST compare; compare.set_action( D::RFA_REBASE_REQUIREMENTS );
            *compare.mutable_rebase()->mutable_draft() = m_draft;
            execute( std::move( compare ) ); return;
        }
        m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); refresh(); return;
    }
    if( m_activeRequest.action() == D::RFA_REBASE_REQUIREMENTS )
    {
        const auto& merge = result.merge();
        if( !result.has_merge() || result.source_token().size() != 64 || !merge.has_original_draft()
            || !result.has_document() || result.document().document_id() != DocumentId()
            || result.document().source_path() != SourcePath() || result.document().source_token() != result.source_token()
            || !same( merge.original_draft().baseline(), m_activeRequest.rebase().draft().baseline() )
            || merge.original_draft().SerializeAsString() != m_activeRequest.rebase().draft().SerializeAsString() )
        { m_errorCode = "requirement_comparison_mismatch"; m_error = "The comparison does not match the retained editing draft."; refresh(); return; }
        if( merge.has_candidate() )
        {
            // Save the revalidated candidate against this exact new file token.
            // The normal disk guard rejects another change between compare and save.
            REQUEST save; save.set_action( D::RFA_SAVE_BLOCK ); save.set_expected_source_token( result.source_token() );
            auto* candidate = save.mutable_save(); *candidate->mutable_draft() = merge.candidate();
            *candidate->mutable_expected_root() = merge.expected_root(); *candidate->mutable_block_path() = merge.block_path();
            candidate->set_new_revision_id( freshId() ); candidate->set_new_requirement_revision_id( freshId() );
            for( int i = 1; i < merge.block_path_size(); ++i ) candidate->add_ancestor_revision_ids( freshId() );
            auto* origin = candidate->mutable_origin(); origin->set_kind( D::DAK_EDITOR ); origin->set_actor( "Native editor" );
            origin->set_summary( "Reconcile requirement edits" );
            auto now = std::chrono::system_clock::now().time_since_epoch(); auto seconds = std::chrono::duration_cast<std::chrono::seconds>( now );
            origin->mutable_recorded_at()->set_seconds( seconds.count() );
            origin->mutable_recorded_at()->set_nanos( static_cast<int>( std::chrono::duration_cast<std::chrono::nanoseconds>( now - seconds ).count() / 100 * 100 ) );
            std::string scope = current() ? current()->selection().block_id() : merge.expected_root().block_id();
            m_document = result.document();
            if( !findPath( scope, m_path ) ) m_path = { m_document.graph().selected_root() };
            m_savedDraft = merge.saved_draft(); m_draft = merge.candidate(); m_dirty = true;
            execute( std::move( save ) ); return;
        }
        if( merge.conflicts_size() == 0 ) { m_error = "No savable comparison was returned; the draft remains open."; refresh(); return; }
        DIALOG_DIAGRAM_CONFLICT dialog( this, m_owner->GetLabel(), merge );
        if( dialog.ShowModal() != wxID_OK )
        {
            m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear();
            m_errorCode = "requirement_conflict"; m_error = "The saved design changed. Your draft is still open."; refresh(); return;
        }
        REQUEST resolve; resolve.set_action( D::RFA_REBASE_REQUIREMENTS ); resolve.set_expected_source_token( result.source_token() );
        *resolve.mutable_rebase()->mutable_draft() = merge.original_draft();
        m_undo.push_back( m_draft ); m_redo.clear();
        for( auto choice : dialog.Resolutions() )
        {
            choice.set_document_id( DocumentId() );
            *resolve.mutable_rebase()->add_resolutions() = choice;
            // Keep the user's chosen or composed text as draft work even if the
            // saved file advances again before the companion can revalidate it.
            int which = static_cast<int>( choice.field() ) - 1;
            if( which >= 0 && which < 3 )
            {
                setField( m_draft.mutable_fields(), which, choice.text() );
                auto* restored = m_draft.mutable_restored_fields();
                for( int i = restored->size() - 1; i >= 0; --i )
                    if( restored->Get( i ).field() == choice.field() ) restored->DeleteSubrange( i, 1 );
            }
        }
        m_dirty = true; ++m_viewRevision; execute( std::move( resolve ) ); return;
    }
    if( m_activeRequest.action() == D::RFA_BLOCK_FIELD_HISTORY || m_activeRequest.action() == D::RFA_CONNECTION_FIELD_HISTORY )
    {
        bool link = m_activeRequest.action() == D::RFA_CONNECTION_FIELD_HISTORY;
        std::string ownerId = link ? m_connectionDraft.baseline().connection_id() : m_draft.baseline().block_id();
        std::string stateId = link ? m_connectionDraft.baseline().state_id() : m_draft.baseline().state_id();
        std::string revisionId = link ? m_connectionDraft.baseline().revision_id() : m_draft.baseline().revision_id();
        if( !result.has_history() || result.source_token() != m_document.source_token()
            || result.history().document_id() != DocumentId()
            || result.history().owner_id() != ownerId || result.history().state_id() != stateId
            || result.history().context_revision_id() != revisionId
            || result.history().field() != m_activeRequest.field() )
        { m_error = "The history response belongs to another saved context."; refresh(); return; }
        int which = static_cast<int>( result.history().field() ) - 1;
        std::vector<DIAGRAM_FIELD_HISTORY_ENTRY> rows;
        for( const auto& row : result.history().entries() )
            rows.push_back( { row.requirement_revision_id(), wxString::Format( "v%u", row.context_version() ),
                text( row.origin().actor() ), text( row.text() ), wxEmptyString, row.is_saved_text() } );
        const wxString labels[] = { _( "General requirements" ), _( "Schematic requirements" ), _( "Routing requirements" ) };
        if( which < 0 || which > 2 ) { m_error = "The history field is unsupported."; refresh(); return; }
        DIALOG_DIAGRAM_FIELD_HISTORY dialog( this, labels[which], m_owner->GetLabel(),
                wxString::Format( "v%u", result.history().context_version() ), text( result.history().saved_text() ), std::move( rows ) );
        if( dialog.ShowModal() == wxID_OK && dialog.RestoreRevision() )
            for( const auto& row : result.history().entries() ) if( row.requirement_revision_id() == *dialog.RestoreRevision() )
            {
                if( field( link ? m_connectionDraft.fields() : m_draft.fields(), which ) == row.text() ) break;
                if( link ) { m_connectionUndo.push_back( m_connectionDraft ); m_connectionRedo.clear(); }
                else { m_undo.push_back( m_draft ); m_redo.clear(); }
                setField( link ? m_connectionDraft.mutable_fields() : m_draft.mutable_fields(), which, row.text() );
                auto* restores = link ? m_connectionDraft.mutable_restored_fields() : m_draft.mutable_restored_fields();
                for( int i = restores->size() - 1; i >= 0; --i ) if( restores->Get( i ).field() == result.history().field() ) restores->DeleteSubrange( i, 1 );
                auto* restored = restores->Add(); restored->set_field( result.history().field() ); restored->set_source_revision_id( row.requirement_revision_id() );
                m_dirty = link ? m_connectionDraft.SerializeAsString() != m_savedConnectionDraft.SerializeAsString()
                    : m_draft.SerializeAsString() != m_savedDraft.SerializeAsString(); ++m_viewRevision; break;
            }
        refresh(); return;
    }
    if( !result.has_document() || result.document().schema_version() != 1 || result.document().document_id() != DocumentId()
        || result.document().source_path() != SourcePath() || result.document().source_token().size() != 64 )
    { m_errorCode = "diagram_target_mismatch"; m_error = "The loaded document does not match the requested diagram."; refresh(); return; }
    std::string scope = m_pendingScope.empty() ? current() ? current()->selection().block_id() : result.document().graph().selected_root().block_id() : m_pendingScope;
    std::string selected = m_pendingSelected.empty() ? m_selected : m_pendingSelected;
    std::string selectedConnection = m_pendingConnection.value_or( m_connectionId );
    m_document = result.document(); m_ready = true; m_dirty = false;
    if( !findPath( scope, m_path ) ) m_path = { m_document.graph().selected_root() };
    m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); m_selected.clear(); m_connectionId.clear();
    select( selected.empty() ? m_path.back().block_id() : selected );
    if( !selectedConnection.empty() ) selectConnection( selectedConnection );
    fit(); m_canvas->SetFocus();
    if( m_closeAfterSave ) { m_closeAfterSave = false; Close(); }
}

void RECURSIVE_DIAGRAM_FRAME::makeDraft( const REVISION& item )
{
    auto* saved = requirements( item ); if( !saved ) return;
    m_draft.Clear(); *m_draft.mutable_baseline() = item.selection(); m_draft.set_name( item.name() );
    *m_draft.mutable_children() = item.children(); m_draft.set_baseline_requirement_revision_id( saved->id() );
    *m_draft.mutable_baseline_fields() = saved->fields(); *m_draft.mutable_fields() = saved->fields();
    if( item.has_local_diagram() ) *m_draft.mutable_local_diagram() = item.local_diagram();
    m_savedDraft = m_draft; m_undo.clear(); m_redo.clear(); m_dirty = false;
    m_commentId.clear(); m_newComment = false;
}
void RECURSIVE_DIAGRAM_FRAME::refresh()
{
    m_updating = true; bool available = m_ready && !m_process;
    bool link = !m_connectionId.empty();
    wxString path;
    for( const auto& step : m_path ) if( auto* item = revision( step ) ) { if( !path.empty() ) path += wxS( "  ›  " ); path += text( item->name() ); }
    m_breadcrumb->SetLabel( path.empty() ? _( "Loading diagram…" ) : path );
    m_owner->SetLabel( m_ready ? text( link ? m_connectionDraft.name() : m_draft.name() ) : wxString() );
    auto* selected = m_ready ? revision( m_draft.baseline() ) : nullptr;
    m_savedVersion->SetLabel( selected ? wxString::Format( _( "Selected design: v%d" ), version( *selected ) ) : wxString() );
    if( link && current() )
        for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == current()->selection().block_id() )
        {
            auto* item = connection( m_connectionId ); int number = 1;
            while( item && item->has_parent_revision_id() && number <= archive.revisions_size() )
            {
                const D::ConnectionRevisionData* parent = nullptr;
                for( const auto& row : archive.revisions() ) if( row.selection().revision_id() == item->parent_revision_id() ) { parent = &row; break; }
                item = parent; ++number;
            }
            m_savedVersion->SetLabel( wxString::Format( _( "Selected connection: v%d" ), number ) ); break;
        }
    m_openDiagram->Show( !link ); m_endpointHeading->Show( link ); m_endpoints->Show( link );
    wxString endpoints;
    if( link && current() )
        for( const auto& item : m_connectionDraft.endpoints() )
        {
            const REVISION* owner = item.block_id() == current()->selection().block_id() ? current() : nullptr;
            for( const auto& child : current()->children() ) if( child.block_id() == item.block_id() ) owner = revision( child );
            if( !endpoints.empty() ) endpoints += wxS( "\n\n" );
            endpoints += owner ? text( owner->name() ) : _( "Unavailable endpoint" ); endpoints += wxS( "\n" );
            switch( item.kind() )
            {
            case D::DEK_UNRESOLVED: endpoints += _( "Endpoint unresolved." ); break;
            case D::DEK_COMPATIBLE: endpoints += _( "Compatible endpoint unresolved." ); break;
            case D::DEK_CANDIDATES: endpoints += wxString::Format( _( "Pin not selected (%d candidates)." ), item.candidates_size() ); break;
            case D::DEK_PIN: endpoints += wxString::Format( _( "Selected pin: %s" ), text( item.pin().pin() ) ); break;
            case D::DEK_INTERFACE:
                if( owner ) for( const auto& boundary : owner->local_diagram().interfaces() )
                    if( boundary.id() == item.interface_id() ) endpoints += wxString::Format( _( "Interface: %s" ), text( boundary.name() ) );
                break;
            default: endpoints += _( "Endpoint type unavailable." ); break;
            }
        }
    m_endpoints->ChangeValue( endpoints );
    for( int i = 0; i < 3; ++i ) { m_fields[i]->Enable( available ); m_history[i]->Enable( available ); m_fields[i]->ChangeValue( text( field( link ? m_connectionDraft.fields() : m_draft.fields(), i ) ) ); }
    m_openDiagram->Enable( available && !link && current() && m_selected != current()->selection().block_id() );
    m_save->Enable( available && m_dirty ); m_decline->Enable( available && m_dirty );
    m_toolbar->EnableTool( BACK, available && !m_back.empty() ); m_toolbar->EnableTool( UP, available && m_path.size() > 1 );
    m_toolbar->EnableTool( wxID_UNDO, available && ( link ? !m_connectionUndo.empty() : !m_undo.empty() ) );
    m_toolbar->EnableTool( wxID_REDO, available && ( link ? !m_connectionRedo.empty() : !m_redo.empty() ) );
    m_toolbar->EnableTool( FIT, available );
    m_toolbar->EnableTool( NOTE, available );
    SetStatusText( !m_error.empty() ? text( m_error ) : m_process ? _( "Working…" ) : m_dirty ? _( "Unsaved changes" ) : wxString() );
    fillComments(); m_inspectorScroll->Layout(); m_inspectorScroll->FitInside();
    m_updating = false; m_rendered = false; m_canvas->Refresh();
}
bool RECURSIVE_DIAGRAM_FRAME::confirmChange()
{
    if( m_process ) return false;
    if( !m_dirty ) return true;
    wxMessageDialog choice( this, _( "Save changes before leaving this draft?" ), _( "Unsaved changes" ), wxYES_NO | wxCANCEL | wxICON_QUESTION );
    choice.SetYesNoLabels( _( "Save" ), _( "Decline" ) );
    int answer = choice.ShowModal();
    if( answer == wxID_YES ) save();
    else if( answer == wxID_NO ) load();
    else { m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); }
    return false;
}
void RECURSIVE_DIAGRAM_FRAME::select( const std::string& id )
{
    if( !m_ready || m_process || !current() ) return;
    const REVISION* target = current();
    for( const auto& child : current()->children() ) if( child.block_id() == id ) target = revision( child );
    if( !target ) return;
    if( m_selected == target->selection().block_id() && m_connectionId.empty() ) return;
    m_pendingScope = current()->selection().block_id(); m_pendingSelected = target->selection().block_id();
    m_pendingConnection = "";
    if( !confirmChange() ) return;
    m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); m_connectionId.clear();
    m_selected = target->selection().block_id(); makeDraft( *target ); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::selectConnection( const std::string& id )
{
    if( !m_ready || m_process || !current() || m_connectionId == id ) return;
    auto* item = connection( id ); if( !item ) return;
    m_pendingScope = current()->selection().block_id(); m_pendingSelected = current()->selection().block_id(); m_pendingConnection = id;
    if( !confirmChange() ) return;
    const D::RequirementRevisionData* requirement = nullptr;
    for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == current()->selection().block_id() )
        for( const auto& history : archive.requirement_histories() ) if( history.state_id() == item->selection().state_id() )
            for( const auto& row : history.revisions() ) if( row.id() == item->requirement_revision_id() ) requirement = &row;
    if( !requirement ) { m_error = "The connection requirement history is unavailable."; refresh(); return; }
    makeDraft( *current() ); m_selected = current()->selection().block_id();
    m_connectionDraft.Clear(); *m_connectionDraft.mutable_baseline() = item->selection(); m_connectionDraft.set_name( item->name() );
    m_connectionDraft.set_kind( item->kind() ); *m_connectionDraft.mutable_endpoints() = item->endpoints(); *m_connectionDraft.mutable_members() = item->members();
    m_connectionDraft.set_baseline_requirement_revision_id( requirement->id() ); *m_connectionDraft.mutable_baseline_fields() = requirement->fields();
    *m_connectionDraft.mutable_fields() = requirement->fields(); m_savedConnectionDraft = m_connectionDraft;
    *m_connectionDraft.mutable_diagram_annotations()->mutable_annotations() = current()->local_diagram().annotations();
    m_savedConnectionDraft = m_connectionDraft;
    m_connectionId = id; m_connectionUndo.clear(); m_connectionRedo.clear();
    m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset(); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::navigate( const std::string& id, bool remember )
{
    if( !m_ready || m_process || !current() || current()->selection().block_id() == id ) return;
    std::vector<SELECTION> path; if( !findPath( id, path ) ) return;
    m_pendingScope = id; m_pendingSelected = id;
    m_pendingConnection = "";
    if( !confirmChange() ) return;
    if( remember ) m_back.push_back( current()->selection().block_id() );
    m_views[current()->selection().block_id()] = { m_scale, m_origin, m_selected };
    m_path = std::move( path ); m_selected.clear(); m_connectionId.clear(); m_pendingScope.clear(); m_pendingSelected.clear(); m_pendingConnection.reset();
    select( id );
    if( auto saved = m_views.find( id ); saved != m_views.end() ) { m_scale = saved->second.scale; m_origin = saved->second.origin; select( saved->second.selected ); }
    else fit();
    ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::edit()
{
    if( !m_ready || m_process ) return;
    if( !m_connectionId.empty() )
    {
        auto before = m_connectionDraft;
        for( int i = 0; i < 3; ++i ) if( field( m_connectionDraft.fields(), i ) != utf8( m_fields[i]->GetValue() ) )
        {
            setField( m_connectionDraft.mutable_fields(), i, utf8( m_fields[i]->GetValue() ) );
            auto* restores = m_connectionDraft.mutable_restored_fields();
            for( int n = restores->size() - 1; n >= 0; --n ) if( static_cast<int>( restores->Get( n ).field() ) == i + 1 ) restores->DeleteSubrange( n, 1 );
        }
        if( before.SerializeAsString() == m_connectionDraft.SerializeAsString() ) return;
        m_connectionUndo.push_back( std::move( before ) ); m_connectionRedo.clear();
        m_dirty = m_connectionDraft.SerializeAsString() != m_savedConnectionDraft.SerializeAsString(); ++m_viewRevision;
        m_save->Enable( m_dirty ); m_decline->Enable( m_dirty ); m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
        SetStatusText( m_dirty ? _( "Unsaved changes" ) : wxString() ); return;
    }
    DRAFT before = m_draft;
    for( int i = 0; i < 3; ++i ) if( field( m_draft.fields(), i ) != utf8( m_fields[i]->GetValue() ) )
    {
        setField( m_draft.mutable_fields(), i, utf8( m_fields[i]->GetValue() ) );
        auto* restores = m_draft.mutable_restored_fields();
        for( int n = restores->size() - 1; n >= 0; --n ) if( static_cast<int>( restores->Get( n ).field() ) == i + 1 ) restores->DeleteSubrange( n, 1 );
    }
    if( before.SerializeAsString() == m_draft.SerializeAsString() ) return;
    m_undo.push_back( std::move( before ) ); m_redo.clear(); m_dirty = m_draft.SerializeAsString() != m_savedDraft.SerializeAsString(); ++m_viewRevision;
    // Do not refill text controls while typing: it would move the caret.
    m_save->Enable( m_dirty ); m_decline->Enable( m_dirty ); m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    SetStatusText( m_dirty ? _( "Unsaved changes" ) : wxString() );
}
void RECURSIVE_DIAGRAM_FRAME::fillComments()
{
    bool wasUpdating = m_updating; m_updating = true;
    bool link = !m_connectionId.empty();
    const auto& notes = link ? m_connectionDraft.diagram_annotations().annotations() : m_draft.local_diagram().annotations();
    const std::string target = link ? m_connectionId : m_draft.baseline().block_id();
    D::DiagramAnnotationTargetKind kind = link ? D::DAT_CONNECTION : D::DAT_BLOCK;
    m_commentIds.clear(); m_commentChoice->Clear();
    const D::DiagramAnnotationData* selected = nullptr;
    for( const auto& note : notes ) if( ( ( note.target_kind() == kind && note.target_id() == target )
        || ( !link && note.target_kind() == D::DAT_CANVAS ) ) && !note.has_unresolved_reason() )
    {
        if( m_commentId.empty() && !m_newComment ) m_commentId = note.id();
        wxString title = text( note.text() ).BeforeFirst( '\n' );
        if( title.length() > 36 ) title = title.Left( 36 ) + wxS( "…" );
        if( title.empty() ) title = _( "Sketch comment" );
        m_commentChoice->Append( title ); m_commentIds.push_back( note.id() );
        if( note.id() == m_commentId ) { selected = &note; m_commentChoice->SetSelection( m_commentIds.size() - 1 ); }
    }
    m_commentChoice->Append( _( "New comment" ) ); m_commentIds.emplace_back();
    if( !selected ) m_commentChoice->SetSelection( m_commentIds.size() - 1 );
    wxString value = selected ? text( selected->text() ) : wxString();
    if( m_comments->GetValue() != value ) m_comments->ChangeValue( value );
    m_comments->Enable( m_ready && !m_process ); m_commentChoice->Enable( m_ready && !m_process );
    m_commentChoice->Show( m_commentIds.size() > 1 );
    m_updating = wasUpdating;
}
void RECURSIVE_DIAGRAM_FRAME::editComment()
{
    if( !m_ready || m_process ) return;
    bool link = !m_connectionId.empty();
    DRAFT blockBefore = m_draft; auto connectionBefore = m_connectionDraft;
    auto* notes = link ? m_connectionDraft.mutable_diagram_annotations()->mutable_annotations()
                      : m_draft.mutable_local_diagram()->mutable_annotations();
    D::DiagramAnnotationData* selected = nullptr; int index = -1;
    for( int i = 0; i < notes->size(); ++i ) if( notes->Get( i ).id() == m_commentId ) { selected = notes->Mutable( i ); index = i; break; }
    std::string value = utf8( m_comments->GetValue() );
    if( ( selected && selected->text() == value ) || ( !selected && value.empty() ) ) return;
    if( selected && value.empty() && selected->strokes_size() == 0 )
    { notes->DeleteSubrange( index, 1 ); m_commentId.clear(); m_newComment = true; }
    else
    {
        if( !selected )
        {
            selected = notes->Add(); selected->set_id( freshId() ); selected->set_role( D::DAR_COMMENT ); selected->set_units( "diagram-unit" );
            selected->set_target_kind( link ? D::DAT_CONNECTION : D::DAT_BLOCK );
            selected->set_target_id( link ? m_connectionId : m_draft.baseline().block_id() ); m_commentId = selected->id(); m_newComment = false;
        }
        selected->set_text( value ); editorOrigin( selected->mutable_origin(), "Edit diagram comment" );
    }
    if( link )
    { m_connectionUndo.push_back( std::move( connectionBefore ) ); m_connectionRedo.clear(); m_dirty = m_connectionDraft.SerializeAsString() != m_savedConnectionDraft.SerializeAsString(); }
    else
    { m_undo.push_back( std::move( blockBefore ) ); m_redo.clear(); m_dirty = m_draft.SerializeAsString() != m_savedDraft.SerializeAsString(); }
    ++m_viewRevision; m_save->Enable( m_dirty ); m_decline->Enable( m_dirty ); m_toolbar->EnableTool( wxID_UNDO, true ); m_toolbar->EnableTool( wxID_REDO, false );
    fillComments(); m_inspectorScroll->Layout(); m_inspectorScroll->FitInside(); SetStatusText( m_dirty ? _( "Unsaved changes" ) : wxString() );
}
void RECURSIVE_DIAGRAM_FRAME::save()
{
    if( !m_ready || m_process || !m_dirty ) return;
    m_rebaseAttempts = 0;
    if( !m_connectionId.empty() )
    {
        REQUEST request; request.set_action( D::RFA_SAVE_CONNECTION ); request.set_expected_source_token( m_document.source_token() );
        auto* save = request.mutable_save_connection(); *save->mutable_expected_root() = m_document.graph().selected_root();
        for( const auto& step : m_path ) *save->add_block_path() = step;
        *save->add_connection_path() = m_connectionDraft.baseline(); *save->mutable_draft() = m_connectionDraft;
        save->set_new_connection_revision_id( freshId() ); save->set_new_requirement_revision_id( freshId() );
        save->set_new_block_revision_id( freshId() ); save->set_new_block_requirement_revision_id( freshId() );
        for( size_t i = 1; i < m_path.size(); ++i ) save->add_block_ancestor_revision_ids( freshId() );
        editorOrigin( save->mutable_origin(), "Edit connection requirements" ); execute( std::move( request ) ); return;
    }
    REQUEST request; request.set_action( D::RFA_SAVE_BLOCK ); request.set_expected_source_token( m_document.source_token() );
    auto* save = request.mutable_save(); *save->mutable_expected_root() = m_document.graph().selected_root(); *save->mutable_draft() = m_draft;
    std::vector<SELECTION> path; if( !findPath( m_draft.baseline().block_id(), path ) ) return;
    for( const auto& step : path ) *save->add_block_path() = step;
    for( size_t i = 1; i < path.size(); ++i ) save->add_ancestor_revision_ids( freshId() );
    save->set_new_revision_id( freshId() ); save->set_new_requirement_revision_id( freshId() );
    auto* origin = save->mutable_origin(); origin->set_kind( D::DAK_EDITOR ); origin->set_actor( "Native editor" ); origin->set_summary( "Edit requirements" );
    auto now = std::chrono::system_clock::now().time_since_epoch(); auto seconds = std::chrono::duration_cast<std::chrono::seconds>( now );
    origin->mutable_recorded_at()->set_seconds( seconds.count() );
    origin->mutable_recorded_at()->set_nanos( static_cast<int>( std::chrono::duration_cast<std::chrono::nanoseconds>( now - seconds ).count() / 100 * 100 ) );
    execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::decline()
{ if( m_ready && !m_process && m_dirty ) { m_pendingScope = current()->selection().block_id(); m_pendingSelected = m_selected; load(); } }
void RECURSIVE_DIAGRAM_FRAME::history( int which )
{
    if( !m_ready || m_process ) return;
    REQUEST request; request.set_expected_source_token( m_document.source_token() );
    if( m_connectionId.empty() ) { request.set_action( D::RFA_BLOCK_FIELD_HISTORY ); *request.mutable_block() = m_draft.baseline(); }
    else { request.set_action( D::RFA_CONNECTION_FIELD_HISTORY ); *request.mutable_block() = m_path.back(); *request.mutable_connection() = m_connectionDraft.baseline(); }
    request.set_field( static_cast<D::RequirementFieldKind>( which + 1 ) ); request.set_limit( 200 ); execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::undo( bool redo )
{
    if( m_process ) return;
    if( !m_connectionId.empty() )
    {
        auto& from = redo ? m_connectionRedo : m_connectionUndo; auto& to = redo ? m_connectionUndo : m_connectionRedo;
        if( from.empty() ) return; to.push_back( m_connectionDraft ); m_connectionDraft = std::move( from.back() ); from.pop_back();
        m_dirty = m_connectionDraft.SerializeAsString() != m_savedConnectionDraft.SerializeAsString(); ++m_viewRevision; refresh(); return;
    }
    auto& from = redo ? m_redo : m_undo; auto& to = redo ? m_undo : m_redo;
    if( from.empty() ) return; to.push_back( m_draft ); m_draft = std::move( from.back() ); from.pop_back();
    m_dirty = m_draft.SerializeAsString() != m_savedDraft.SerializeAsString(); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::close( wxCloseEvent& event )
{
    if( m_process ) { event.Veto(); return; }
    if( m_dirty && event.CanVeto() ) { m_closeAfterSave = true; if( !confirmChange() ) { if( !m_process ) m_closeAfterSave = false; event.Veto(); return; } }
    m_closing = true; Destroy();
}

wxRect RECURSIVE_DIAGRAM_FRAME::nodeRect( int index ) const
{
    int count = current() ? current()->children_size() : 0; int columns = std::max( 1, static_cast<int>( std::ceil( std::sqrt( count ) ) ) );
    return { static_cast<int>( ( 140 + ( index % columns ) * 370 - m_origin.m_x ) * m_scale ),
             static_cast<int>( ( 110 + ( index / columns ) * 250 - m_origin.m_y ) * m_scale ),
             static_cast<int>( 240 * m_scale ), static_cast<int>( 145 * m_scale ) };
}
wxPoint RECURSIVE_DIAGRAM_FRAME::endpoint( const D::DiagramEndpointBindingData& value, bool first ) const
{
    auto* scope = current(); if( !scope ) return {};
    if( value.block_id() == scope->selection().block_id() )
    {
        int index = 0; for( const auto& port : scope->local_diagram().interfaces() ) { if( port.id() == value.interface_id() ) break; ++index; }
        return { static_cast<int>( ( 40 - m_origin.m_x ) * m_scale ), static_cast<int>( ( 90 + index * 85 - m_origin.m_y ) * m_scale ) };
    }
    for( int i = 0; i < scope->children_size(); ++i ) if( scope->children( i ).block_id() == value.block_id() )
    {
        auto box = nodeRect( i ); auto* child = revision( scope->children( i ) ); int index = 0;
        if( child ) for( const auto& port : child->local_diagram().interfaces() ) { if( port.id() == value.interface_id() ) break; ++index; }
        int count = child ? child->local_diagram().interfaces_size() : 0;
        return { first ? box.GetRight() : box.GetLeft(), box.GetTop() + ( index + 1 ) * box.GetHeight() / std::max( 2, count + 1 ) };
    }
    return {};
}
std::array<wxPoint, 4> RECURSIVE_DIAGRAM_FRAME::connectionPath( const D::ConnectionRevisionData& link, int index ) const
{
    auto center = [&]( const D::DiagramEndpointBindingData& e )
    { auto left = endpoint( e, false ), right = endpoint( e, true ); return ( left.x + right.x ) / 2; };
    const auto& first = link.endpoints( 0 ); const auto& second = link.endpoints( index );
    wxPoint from = endpoint( first, center( first ) < center( second ) );
    wxPoint to = endpoint( second, center( second ) < center( first ) );
    int middle = ( from.x + to.x ) / 2;
    return { from, wxPoint( middle, from.y ), wxPoint( middle, to.y ), to };
}
wxRect RECURSIVE_DIAGRAM_FRAME::noteRect( const D::DiagramAnnotationData& note, int index ) const
{
    double x = 60 + index * 260, y = 420;
    if( note.has_position() ) { text( note.position().x() ).ToDouble( &x ); text( note.position().y() ).ToDouble( &y ); }
    return { static_cast<int>( ( x - m_origin.m_x ) * m_scale ), static_cast<int>( ( y - m_origin.m_y ) * m_scale ),
             static_cast<int>( 240 * m_scale ), static_cast<int>( 100 * m_scale ) };
}
void RECURSIVE_DIAGRAM_FRAME::paint( wxDC& dc )
{
    wxColour background = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOW );
    wxColour foreground = wxSystemSettings::GetColour( wxSYS_COLOUR_WINDOWTEXT );
    dc.SetBackground( wxBrush( background ) ); dc.Clear(); dc.SetTextForeground( foreground );
    auto* scope = current(); if( !m_ready || !scope ) { dc.DrawText( m_error.empty() ? _( "Loading diagram…" ) : text( m_error ), 24, 24 ); return; }
    dc.SetPen( wxPen( wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT ) ) );
    for( const auto& archive : m_document.graph().connection_archives() ) if( archive.owner_block_id() == scope->selection().block_id() )
        for( const auto& selected : scope->local_diagram().connections() )
            for( const auto& connection : archive.revisions() ) if( connection.selection().revision_id() == selected.revision_id() && connection.endpoints_size() >= 2 )
            {
                bool highlighted = selected.connection_id() == m_connectionId;
                dc.SetPen( wxPen( wxSystemSettings::GetColour( highlighted ? wxSYS_COLOUR_HIGHLIGHT : wxSYS_COLOUR_GRAYTEXT ), highlighted ? 2 : 1 ) );
                for( int i = 1; i < connection.endpoints_size(); ++i )
                {
                    // Port side is a presentation choice toward the peer, not an
                    // inferred electrical signal direction. Boundary links must
                    // not exit through the far side of a child and cross its body.
                    const auto& first = connection.endpoints( 0 ); const auto& second = connection.endpoints( i );
                    auto route = connectionPath( connection, i );
                    wxPoint from = route.front(), to = route.back();
                    for( size_t segment = 1; segment < route.size(); ++segment ) dc.DrawLine( route[segment - 1], route[segment] );
                    // A boundary already names its interface. Avoid duplicating
                    // the relationship title on top of that boundary label.
                    if( first.block_id() != scope->selection().block_id() && second.block_id() != scope->selection().block_id() )
                        dc.DrawText( text( connection.name() ), std::min( from.x, to.x ) + 8, from.y - 24 );
                }
            }
    bool dark = background.Red() + background.Green() + background.Blue() < 384;
    for( int i = 0; i < scope->children_size(); ++i ) if( auto* child = revision( scope->children( i ) ) )
    {
        auto box = nodeRect( i ); bool selected = child->selection().block_id() == m_selected;
        wxColour accent = wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT );
        dc.SetPen( wxPen( selected ? accent : wxSystemSettings::GetColour( wxSYS_COLOUR_GRAYTEXT ), selected ? 2 : 1 ) );
        dc.SetBrush( wxBrush( selected ? accent.ChangeLightness( dark ? 60 : 175 ) : background.ChangeLightness( dark ? 120 : 97 ) ) ); dc.DrawRectangle( box );
        dc.SetClippingRegion( box.Deflate( 8 ) ); dc.SetFont( GetFont().Bold().Larger() );
        dc.DrawText( text( child->name() ), box.x + 10, box.y + 24 ); dc.SetFont( GetFont() );
        dc.DrawText( wxString::Format( "v%d", version( *child ) ), box.x + 10, box.y + 58 ); dc.DestroyClippingRegion();
    }
    for( const auto& port : scope->local_diagram().interfaces() )
    { D::DiagramEndpointBindingData value; value.set_block_id( scope->selection().block_id() ); value.set_interface_id( port.id() ); auto point = endpoint( value, true ); dc.DrawRectangle( point.x - 4, point.y - 4, 8, 8 ); dc.DrawText( text( port.name() ), point.x + 12, point.y - 24 ); }
    const auto& notes = !m_connectionId.empty() ? m_connectionDraft.diagram_annotations().annotations()
        : m_draft.baseline().block_id() == scope->selection().block_id() ? m_draft.local_diagram().annotations() : scope->local_diagram().annotations();
    for( int i = 0; i < notes.size(); ++i )
    {
        const auto& note = notes.Get( i );
        if( note.target_kind() != D::DAT_CANVAS && !note.has_position() ) continue;
        wxRect box = noteRect( note, i );
        dc.SetPen( wxPen( note.id() == m_commentId ? wxSystemSettings::GetColour( wxSYS_COLOUR_HIGHLIGHT ) : wxColour( 176, 142, 52 ), 1 ) );
        dc.SetBrush( wxBrush( dark ? wxColour( 76, 66, 34 ) : wxColour( 255, 247, 213 ) ) ); dc.DrawRectangle( box );
        box.Deflate( 8 ); dc.SetClippingRegion( box );
        wxString value = text( note.text() ); int y = box.y;
        while( !value.empty() && y + dc.GetCharHeight() <= box.GetBottom() )
        {
            size_t count = value.find( '\n' ); if( count == wxString::npos ) count = value.length();
            while( count > 0 && dc.GetTextExtent( value.Left( count ) ).x > box.width ) --count;
            if( count == 0 && value[0] != '\n' ) count = 1;
            wxString line = value.Left( count ); value = value.Mid( count ); if( value.StartsWith( "\n" ) ) value = value.Mid( 1 );
            if( !value.empty() && y + dc.GetCharHeight() * 2 > box.GetBottom() )
            { while( !line.empty() && dc.GetTextExtent( line + wxS( "…" ) ).x > box.width ) line.RemoveLast(); line += wxS( "…" ); }
            dc.DrawText( line, box.x, y ); y += dc.GetCharHeight();
        }
        dc.DestroyClippingRegion();
    }
    m_rendered = true;
}
void RECURSIVE_DIAGRAM_FRAME::click( wxMouseEvent& event )
{
    if( !m_ready || m_process || !current() ) return;
    m_canvas->SetFocus();
    if( m_noteMode )
    {
        select( current()->selection().block_id() );
        if( m_process || !m_connectionId.empty() || m_draft.baseline().block_id() != current()->selection().block_id() ) return;
        m_undo.push_back( m_draft ); m_redo.clear(); auto* note = m_draft.mutable_local_diagram()->add_annotations();
        note->set_id( freshId() ); note->set_role( D::DAR_COMMENT ); note->set_target_kind( D::DAT_CANVAS ); note->set_units( "diagram-unit" );
        note->mutable_position()->set_x( std::to_string( event.GetX() / m_scale + m_origin.m_x ) );
        note->mutable_position()->set_y( std::to_string( event.GetY() / m_scale + m_origin.m_y ) );
        editorOrigin( note->mutable_origin(), "Place diagram comment" ); m_commentId = note->id(); m_newComment = false;
        m_noteMode = false; m_canvas->SetCursor( wxCursor( wxCURSOR_ARROW ) ); m_dirty = true; ++m_viewRevision; refresh(); m_comments->SetFocus(); return;
    }
    const auto& visibleNotes = m_draft.baseline().block_id() == current()->selection().block_id() && m_connectionId.empty()
        ? m_draft.local_diagram().annotations() : current()->local_diagram().annotations();
    for( int i = visibleNotes.size() - 1; i >= 0; --i )
    {
        const auto& note = visibleNotes.Get( i );
        if( note.target_kind() != D::DAT_CANVAS || !noteRect( note, i ).Contains( event.GetPosition() ) ) continue;
        std::string id = note.id();
        select( current()->selection().block_id() );
        if( m_process || !m_connectionId.empty() || m_draft.baseline().block_id() != current()->selection().block_id() ) return;
        m_commentId = id; m_newComment = false; refresh();
        if( event.LeftDClick() ) { m_comments->SetFocus(); return; }
        for( const auto& item : m_draft.local_diagram().annotations() ) if( item.id() == id )
        {
            wxRect box = noteRect( item, i ); m_noteStartX = box.x / m_scale + m_origin.m_x; m_noteStartY = box.y / m_scale + m_origin.m_y;
            m_noteDragStart = event.GetPosition(); m_noteDragBefore = m_draft; m_draggingNote = true;
            if( !m_canvas->HasCapture() ) m_canvas->CaptureMouse(); return;
        }
    }
    for( int i = 0; i < current()->children_size(); ++i ) if( nodeRect( i ).Contains( event.GetPosition() ) )
    { auto id = current()->children( i ).block_id(); if( event.LeftDClick() ) navigate( id ); else select( id ); return; }
    for( const auto& selected : current()->local_diagram().connections() ) if( auto* link = connection( selected.connection_id() ) )
        for( int i = 1; i < link->endpoints_size(); ++i )
        {
            auto route = connectionPath( *link, i ); wxPoint point = event.GetPosition();
            for( size_t j = 1; j < route.size(); ++j )
            {
                int x = std::clamp( point.x, std::min( route[j - 1].x, route[j].x ), std::max( route[j - 1].x, route[j].x ) );
                int y = std::clamp( point.y, std::min( route[j - 1].y, route[j].y ), std::max( route[j - 1].y, route[j].y ) );
                if( std::abs( point.x - x ) + std::abs( point.y - y ) <= FromDIP( 6 ) ) { selectConnection( selected.connection_id() ); return; }
            }
        }
    select( current()->selection().block_id() );
}
void RECURSIVE_DIAGRAM_FRAME::moveNote( wxMouseEvent& event )
{
    if( !m_draggingNote || !event.Dragging() || m_process ) return;
    for( auto& note : *m_draft.mutable_local_diagram()->mutable_annotations() ) if( note.id() == m_commentId )
    {
        note.mutable_position()->set_x( std::to_string( m_noteStartX + ( event.GetX() - m_noteDragStart.x ) / m_scale ) );
        note.mutable_position()->set_y( std::to_string( m_noteStartY + ( event.GetY() - m_noteDragStart.y ) / m_scale ) );
        m_rendered = false; m_canvas->Refresh(); return;
    }
}
void RECURSIVE_DIAGRAM_FRAME::finishNoteDrag()
{
    if( !m_draggingNote ) return; m_draggingNote = false;
    if( m_canvas->HasCapture() ) m_canvas->ReleaseMouse();
    if( m_draft.SerializeAsString() != m_noteDragBefore.SerializeAsString() )
    {
        for( auto& note : *m_draft.mutable_local_diagram()->mutable_annotations() ) if( note.id() == m_commentId ) editorOrigin( note.mutable_origin(), "Move diagram comment" );
        m_undo.push_back( m_noteDragBefore ); m_redo.clear(); m_dirty = true; ++m_viewRevision;
    }
    refresh();
}
void RECURSIVE_DIAGRAM_FRAME::fit()
{
    if( !current() ) return;
    int count = std::max( 1, current()->children_size() ), columns = static_cast<int>( std::ceil( std::sqrt( count ) ) ), rows = ( count + columns - 1 ) / columns;
    double width = 140 + columns * 370, height = 110 + rows * 250;
    auto area = m_canvas->GetClientSize(); m_scale = std::min( { 1.0, area.x / width, area.y / height } ); m_scale = std::max( 0.1, m_scale );
    m_origin = { -( area.x / m_scale - width ) / 2, -( area.y / m_scale - height ) / 2 }; ++m_viewRevision; m_rendered = false; m_canvas->Refresh();
}
D::RecursiveDiagramEditorState RECURSIVE_DIAGRAM_FRAME::State() const
{
    D::RecursiveDiagramEditorState result; result.set_document_id( DocumentId() ); result.set_source_path( SourcePath() ); result.set_source_token( m_document.source_token() );
    result.set_ready( m_ready ); result.set_busy( m_process != nullptr ); result.set_dirty( m_dirty ); result.set_rendered( m_rendered );
    result.set_view_revision( m_viewRevision ); result.set_completed_save_count( m_saveCount ); result.set_error_code( m_errorCode ); result.set_error_message( m_error );
    for( const auto& step : m_path ) *result.add_diagram_path() = step;
    if( m_ready ) *result.mutable_draft() = m_draft;
    if( !m_connectionId.empty() ) *result.mutable_connection_draft() = m_connectionDraft;
    result.set_selected_annotation_id( m_commentId );
    return result;
}
