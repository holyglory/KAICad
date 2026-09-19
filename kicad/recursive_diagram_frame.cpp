/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "recursive_diagram_frame.h"
#include "dialogs/dialog_diagram_field_history.h"
#include <bitmaps.h>
#include <kiid.h>
#include <google/protobuf/util/json_util.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <wx/button.h>
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
enum { BACK = wxID_HIGHEST + 3900, UP, FIT };
wxString text( const std::string& value ) { return wxString::FromUTF8( value ); }
std::string utf8( const wxString& value ) { return value.ToStdString( wxConvUTF8 ); }
std::string freshId() { return utf8( KIID().AsString() ); }
bool same( const D::BlockSelectionData& a, const D::BlockSelectionData& b )
{ return a.block_id() == b.block_id() && a.state_id() == b.state_id() && a.revision_id() == b.revision_id(); }
std::string field( const D::RequirementFieldsData& fields, int which )
{ return which == 0 ? fields.general() : which == 1 ? fields.schematic() : fields.routing(); }
void setField( D::RequirementFieldsData* fields, int which, const std::string& value )
{ if( which == 0 ) fields->set_general( value ); else if( which == 1 ) fields->set_schematic( value ); else fields->set_routing( value ); }
}

RECURSIVE_DIAGRAM_FRAME::RECURSIVE_DIAGRAM_FRAME( wxWindow* parent, const D::OpenRecursiveDiagramEditor& request ) :
        wxFrame( parent, wxID_ANY, _( "Structural diagram" ), wxDefaultPosition, wxSize( 1536, 1024 ) ),
        m_request( request ), m_ioTimer( this )
{
    SetName( "RecursiveDiagramEditor" ); SetMinSize( FromDIP( wxSize( 900, 650 ) ) );
    auto* menu = new wxMenuBar(); auto* file = new wxMenu();
    file->Append( wxID_SAVE, _( "Save\tCtrl+S" ) ); file->AppendSeparator(); file->Append( wxID_CLOSE, _( "Close\tCtrl+W" ) );
    menu->Append( file, _( "File" ) ); SetMenuBar( menu );
    m_toolbar = CreateToolBar( wxTB_HORIZONTAL | wxTB_FLAT | wxTB_TEXT );
    m_toolbar->AddTool( BACK, _( "Back" ), KiBitmap( BITMAPS::left ) );
    m_toolbar->AddTool( UP, _( "Up" ), KiBitmap( BITMAPS::up ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( wxID_UNDO, _( "Undo" ), KiBitmap( BITMAPS::undo ) );
    m_toolbar->AddTool( wxID_REDO, _( "Redo" ), KiBitmap( BITMAPS::redo ) ); m_toolbar->AddSeparator();
    m_toolbar->AddTool( FIT, _( "Fit" ), KiBitmap( BITMAPS::zoom_fit_in_page ) ); m_toolbar->Realize();
    auto* splitter = new wxSplitterWindow( this, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxSP_LIVE_UPDATE );
    splitter->SetMinimumPaneSize( FromDIP( 300 ) ); splitter->SetSashGravity( 1.0 );
    auto* diagram = new wxPanel( splitter ); auto* main = new wxBoxSizer( wxVERTICAL );
    m_breadcrumb = new wxStaticText( diagram, wxID_ANY, _( "Loading diagram…" ) );
    m_breadcrumb->SetName( "RecursiveDiagramPath" );
    main->Add( m_breadcrumb, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_canvas = new wxPanel( diagram ); m_canvas->SetName( "RecursiveDiagramCanvas" );
    m_canvas->SetBackgroundStyle( wxBG_STYLE_PAINT ); main->Add( m_canvas, 1, wxEXPAND ); diagram->SetSizer( main );
    auto* inspector = new wxPanel( splitter ); auto* side = new wxBoxSizer( wxVERTICAL );
    auto* scroll = new wxScrolledWindow( inspector ); scroll->SetScrollRate( 0, FromDIP( 12 ) );
    auto* fields = new wxBoxSizer( wxVERTICAL );
    m_owner = new wxStaticText( scroll, wxID_ANY, wxEmptyString ); m_owner->SetFont( GetFont().Bold().Larger() );
    fields->Add( m_owner, 0, wxEXPAND | wxALL, FromDIP( 12 ) );
    m_savedVersion = new wxStaticText( scroll, wxID_ANY, wxEmptyString );
    fields->Add( m_savedVersion, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    m_openDiagram = new wxButton( scroll, wxID_ANY, _( "Open diagram" ) ); m_openDiagram->SetName( "RecursiveOpenDiagram" );
    fields->Add( m_openDiagram, 0, wxLEFT | wxRIGHT | wxBOTTOM, FromDIP( 12 ) );
    const wxString labels[] = { _( "General requirements" ), _( "Schematic requirements" ), _( "Routing requirements" ) };
    for( int i = 0; i < 3; ++i )
    {
        auto* heading = new wxBoxSizer( wxHORIZONTAL );
        heading->Add( new wxStaticText( scroll, wxID_ANY, labels[i] ), 1, wxALIGN_CENTER_VERTICAL );
        m_history[i] = new wxButton( scroll, wxID_ANY, _( "History" ), wxDefaultPosition, wxDefaultSize, wxBU_EXACTFIT );
        m_history[i]->SetName( wxString::Format( "RecursiveFieldHistory%d", i ) );
        heading->Add( m_history[i], 0 ); fields->Add( heading, 0, wxEXPAND | wxLEFT | wxRIGHT, FromDIP( 12 ) );
        m_fields[i] = new wxTextCtrl( scroll, wxID_ANY, wxEmptyString, wxDefaultPosition,
                FromDIP( wxSize( 320, 115 ) ), wxTE_MULTILINE );
        m_fields[i]->SetName( wxString::Format( "RecursiveRequirements%d", i ) );
        fields->Add( m_fields[i], 0, wxEXPAND | wxALL, FromDIP( 12 ) );
        m_fields[i]->Bind( wxEVT_TEXT, [this]( wxCommandEvent& ) { if( !m_updating ) edit(); } );
        m_history[i]->Bind( wxEVT_BUTTON, [this, i]( wxCommandEvent& ) { history( i ); } );
    }
    scroll->SetSizer( fields ); side->Add( scroll, 1, wxEXPAND );
    auto* actions = new wxBoxSizer( wxHORIZONTAL ); actions->AddStretchSpacer();
    m_decline = new wxButton( inspector, wxID_ANY, _( "Decline" ) ); m_decline->SetName( "RecursiveDecline" );
    m_save = new wxButton( inspector, wxID_SAVE, _( "Save" ) ); m_save->SetName( "RecursiveSave" );
    actions->Add( m_decline, 0, wxRIGHT, FromDIP( 12 ) ); actions->Add( m_save, 0 );
    side->Add( actions, 0, wxEXPAND | wxALL, FromDIP( 12 ) ); inspector->SetSizer( side );
    splitter->SplitVertically( diagram, inspector, FromDIP( 1100 ) );
    auto* frameSizer = new wxBoxSizer( wxVERTICAL ); frameSizer->Add( splitter, 1, wxEXPAND ); SetSizer( frameSizer ); CreateStatusBar();
    m_canvas->Bind( wxEVT_PAINT, [this]( wxPaintEvent& ) { wxAutoBufferedPaintDC dc( m_canvas ); paint( dc ); } );
    m_canvas->Bind( wxEVT_LEFT_DOWN, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_canvas->Bind( wxEVT_LEFT_DCLICK, &RECURSIVE_DIAGRAM_FRAME::click, this );
    m_openDiagram->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { navigate( m_selected ); } );
    m_save->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { save(); } );
    m_decline->Bind( wxEVT_BUTTON, [this]( wxCommandEvent& ) { decline(); } );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { save(); }, wxID_SAVE );
    Bind( wxEVT_MENU, [this]( wxCommandEvent& ) { Close(); }, wxID_CLOSE );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( !m_back.empty() ) { auto id = m_back.back(); navigate( id, false ); if( current() && current()->selection().block_id() == id ) m_back.pop_back(); refresh(); } }, BACK );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { if( m_path.size() > 1 ) navigate( m_path[m_path.size() - 2].block_id() ); }, UP );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { fit(); }, FIT );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( false ); }, wxID_UNDO );
    Bind( wxEVT_TOOL, [this]( wxCommandEvent& ) { undo( true ); }, wxID_REDO );
    Bind( wxEVT_TIMER, [this]( wxTimerEvent& ) { drain(); }, m_ioTimer.GetId() );
    Bind( wxEVT_END_PROCESS, &RECURSIVE_DIAGRAM_FRAME::completed, this );
    Bind( wxEVT_CLOSE_WINDOW, &RECURSIVE_DIAGRAM_FRAME::close, this );
    Bind( wxEVT_CHAR_HOOK, [this]( wxKeyEvent& event )
    {
        if( event.ControlDown() && event.GetKeyCode() == 'S' ) { save(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'W' ) { Close(); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Z' ) { undo( false ); return; }
        if( event.ControlDown() && event.GetKeyCode() == 'Y' ) { undo( true ); return; }
        event.StopPropagation(); event.Skip();
    } );
    refresh(); CallAfter( [this] { load( m_request.expected_source_token() ); } );
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
    if( m_activeRequest.action() == D::RFA_SAVE_BLOCK ) ++m_saveCount;
    if( event.GetExitCode() != 0 || !parsed || !result.success() )
    {
        m_errorCode = parsed ? result.error_code() : "invalid_companion_response";
        m_error = parsed && !result.error_message().empty() ? result.error_message() : "The operation failed; the current draft remains open.";
        m_closeAfterSave = false; m_pendingScope.clear(); m_pendingSelected.clear(); refresh(); return;
    }
    if( m_activeRequest.action() == D::RFA_BLOCK_FIELD_HISTORY )
    {
        if( !result.has_history() || result.source_token() != m_document.source_token()
            || result.history().owner_id() != m_draft.baseline().block_id() )
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
                if( field( m_draft.fields(), which ) == row.text() ) break;
                m_undo.push_back( m_draft ); m_redo.clear(); setField( m_draft.mutable_fields(), which, row.text() );
                auto* restores = m_draft.mutable_restored_fields();
                for( int i = restores->size() - 1; i >= 0; --i ) if( restores->Get( i ).field() == result.history().field() ) restores->DeleteSubrange( i, 1 );
                auto* restored = restores->Add(); restored->set_field( result.history().field() ); restored->set_source_revision_id( row.requirement_revision_id() );
                m_dirty = m_draft.SerializeAsString() != m_savedDraft.SerializeAsString(); ++m_viewRevision; break;
            }
        refresh(); return;
    }
    if( !result.has_document() || result.document().schema_version() != 1 || result.document().document_id() != DocumentId()
        || result.document().source_path() != SourcePath() || result.document().source_token().size() != 64 )
    { m_errorCode = "diagram_target_mismatch"; m_error = "The loaded document does not match the requested diagram."; refresh(); return; }
    std::string scope = m_pendingScope.empty() ? current() ? current()->selection().block_id() : result.document().graph().selected_root().block_id() : m_pendingScope;
    std::string selected = m_pendingSelected.empty() ? m_selected : m_pendingSelected;
    m_document = result.document(); m_ready = true; m_dirty = false;
    if( !findPath( scope, m_path ) ) m_path = { m_document.graph().selected_root() };
    m_pendingScope.clear(); m_pendingSelected.clear(); m_selected.clear();
    select( selected.empty() ? m_path.back().block_id() : selected ); fit();
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
}
void RECURSIVE_DIAGRAM_FRAME::refresh()
{
    m_updating = true; bool available = m_ready && !m_process;
    wxString path;
    for( const auto& step : m_path ) if( auto* item = revision( step ) ) { if( !path.empty() ) path += wxS( "  ›  " ); path += text( item->name() ); }
    m_breadcrumb->SetLabel( path.empty() ? _( "Loading diagram…" ) : path );
    m_owner->SetLabel( m_ready ? text( m_draft.name() ) : wxString() );
    auto* selected = m_ready ? revision( m_draft.baseline() ) : nullptr;
    m_savedVersion->SetLabel( selected ? wxString::Format( _( "Selected design: v%d" ), version( *selected ) ) : wxString() );
    for( int i = 0; i < 3; ++i ) { m_fields[i]->Enable( available ); m_history[i]->Enable( available ); m_fields[i]->ChangeValue( text( field( m_draft.fields(), i ) ) ); }
    m_openDiagram->Enable( available && current() && m_selected != current()->selection().block_id() );
    m_save->Enable( available && m_dirty ); m_decline->Enable( available && m_dirty );
    m_toolbar->EnableTool( BACK, available && !m_back.empty() ); m_toolbar->EnableTool( UP, available && m_path.size() > 1 );
    m_toolbar->EnableTool( wxID_UNDO, available && !m_undo.empty() ); m_toolbar->EnableTool( wxID_REDO, available && !m_redo.empty() );
    m_toolbar->EnableTool( FIT, available );
    SetStatusText( !m_error.empty() ? text( m_error ) : m_process ? _( "Working…" ) : m_dirty ? _( "Unsaved changes" ) : wxString() );
    m_updating = false; m_canvas->Refresh();
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
    else { m_pendingScope.clear(); m_pendingSelected.clear(); }
    return false;
}
void RECURSIVE_DIAGRAM_FRAME::select( const std::string& id )
{
    if( !m_ready || m_process || !current() ) return;
    const REVISION* target = current();
    for( const auto& child : current()->children() ) if( child.block_id() == id ) target = revision( child );
    if( !target ) return;
    if( m_selected == target->selection().block_id() ) return;
    m_pendingScope = current()->selection().block_id(); m_pendingSelected = target->selection().block_id();
    if( !confirmChange() ) return;
    m_pendingScope.clear(); m_pendingSelected.clear(); m_selected = target->selection().block_id(); makeDraft( *target ); ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::navigate( const std::string& id, bool remember )
{
    if( !m_ready || m_process || !current() || current()->selection().block_id() == id ) return;
    std::vector<SELECTION> path; if( !findPath( id, path ) ) return;
    m_pendingScope = id; m_pendingSelected = id;
    if( !confirmChange() ) return;
    if( remember ) m_back.push_back( current()->selection().block_id() );
    m_views[current()->selection().block_id()] = { m_scale, m_origin, m_selected };
    m_path = std::move( path ); m_selected.clear(); m_pendingScope.clear(); m_pendingSelected.clear();
    select( id );
    if( auto saved = m_views.find( id ); saved != m_views.end() ) { m_scale = saved->second.scale; m_origin = saved->second.origin; select( saved->second.selected ); }
    else fit();
    ++m_viewRevision; refresh();
}
void RECURSIVE_DIAGRAM_FRAME::edit()
{
    if( !m_ready || m_process ) return;
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
void RECURSIVE_DIAGRAM_FRAME::save()
{
    if( !m_ready || m_process || !m_dirty ) return;
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
    REQUEST request; request.set_action( D::RFA_BLOCK_FIELD_HISTORY ); request.set_expected_source_token( m_document.source_token() );
    *request.mutable_block() = m_draft.baseline(); request.set_field( static_cast<D::RequirementFieldKind>( which + 1 ) ); request.set_limit( 200 ); execute( std::move( request ) );
}
void RECURSIVE_DIAGRAM_FRAME::undo( bool redo )
{
    if( m_process ) return;
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
                wxPoint from = endpoint( connection.endpoints( 0 ), true );
                for( int i = 1; i < connection.endpoints_size(); ++i )
                { wxPoint to = endpoint( connection.endpoints( i ), false ); int middle = ( from.x + to.x ) / 2; dc.DrawLine( from, { middle, from.y } ); dc.DrawLine( middle, from.y, middle, to.y ); dc.DrawLine( { middle, to.y }, to ); }
                dc.DrawText( text( connection.name() ), from.x + 8, from.y - 24 );
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
    m_rendered = true;
}
void RECURSIVE_DIAGRAM_FRAME::click( wxMouseEvent& event )
{
    if( !m_ready || m_process || !current() ) return;
    for( int i = 0; i < current()->children_size(); ++i ) if( nodeRect( i ).Contains( event.GetPosition() ) )
    { auto id = current()->children( i ).block_id(); if( event.LeftDClick() ) navigate( id ); else select( id ); return; }
    select( current()->selection().block_id() );
}
void RECURSIVE_DIAGRAM_FRAME::fit()
{
    if( !current() ) return;
    int count = std::max( 1, current()->children_size() ), columns = static_cast<int>( std::ceil( std::sqrt( count ) ) ), rows = ( count + columns - 1 ) / columns;
    double width = 140 + columns * 370, height = 110 + rows * 250;
    auto area = m_canvas->GetClientSize(); m_scale = std::min( { 1.0, area.x / width, area.y / height } ); m_scale = std::max( 0.1, m_scale );
    m_origin = { -( area.x / m_scale - width ) / 2, -( area.y / m_scale - height ) / 2 }; ++m_viewRevision; m_canvas->Refresh();
}
D::RecursiveDiagramEditorState RECURSIVE_DIAGRAM_FRAME::State() const
{
    D::RecursiveDiagramEditorState result; result.set_document_id( DocumentId() ); result.set_source_path( SourcePath() ); result.set_source_token( m_document.source_token() );
    result.set_ready( m_ready ); result.set_busy( m_process != nullptr ); result.set_dirty( m_dirty ); result.set_rendered( m_rendered );
    result.set_view_revision( m_viewRevision ); result.set_completed_save_count( m_saveCount ); result.set_error_code( m_errorCode ); result.set_error_message( m_error );
    for( const auto& step : m_path ) *result.add_diagram_path() = step;
    if( m_ready ) *result.mutable_draft() = m_draft;
    return result;
}
