/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include "structural_editor_control.h"
#include "structural_editor_frame.h"
#include "recursive_diagram_frame.h"
#include "structural_editor_admission.h"
#include "kicad_manager_frame.h"
#include <api/api_server.h>
#include <pgm_base.h>
#include <kiid.h>
#include <wx/filename.h>

namespace S = kiapi::automation::structure::v1;
namespace D = kiapi::automation::diagrams::v1;
namespace
{
auto failure( kiapi::common::ApiStatusCode code, const std::string& message )
{
    kiapi::common::ApiResponseStatus error; error.set_status( code ); error.set_error_message( message );
    return tl::unexpected( error );
}
S::StructuralEditorState state( STRUCTURAL_EDITOR_FRAME& frame )
{
    S::StructuralEditorState result; *result.mutable_document() = frame.Document();
    result.set_dirty( frame.IsDirty() ); result.set_saving( frame.IsSaving() ); result.set_rendered( frame.HasRendered() );
    result.set_revision( frame.Revision() ); result.set_completed_save_count( frame.CompletedSaveCount() );
    result.set_last_save_error( frame.LastSaveError() ); result.set_selected_block_id( frame.SelectedBlockId() ); return result;
}
}
STRUCTURAL_EDITOR_CONTROL::STRUCTURAL_EDITOR_CONTROL( KICAD_MANAGER_FRAME* manager ) : m_manager( manager )
{
    registerHandler<S::OpenStructuralEditor, S::StructuralEditorState>( &STRUCTURAL_EDITOR_CONTROL::open );
    registerHandler<S::ReadStructuralEditor, S::StructuralEditorState>( &STRUCTURAL_EDITOR_CONTROL::read );
    registerHandler<D::OpenRecursiveDiagramEditor, D::RecursiveDiagramEditorState>( &STRUCTURAL_EDITOR_CONTROL::openRecursive );
    registerHandler<D::ReadRecursiveDiagramEditor, D::RecursiveDiagramEditorState>( &STRUCTURAL_EDITOR_CONTROL::readRecursive );
    registerHandler<D::ObserveRecursiveDiagramEditor, D::RecursiveDiagramObservation>( &STRUCTURAL_EDITOR_CONTROL::observeRecursive );
    Pgm().GetApiServer().RegisterHandler( this );
}
STRUCTURAL_EDITOR_CONTROL::~STRUCTURAL_EDITOR_CONTROL()
{ Pgm().GetApiServer().DeregisterHandler( this ); }
bool STRUCTURAL_EDITOR_CONTROL::CloseEditors()
{
    for( auto& editor : m_recursiveEditors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && !editor->Close() ) return false;
    for( auto& editor : m_editors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && !editor->Close() ) return false;
    return true;
}
HANDLER_RESULT<S::StructuralEditorState> STRUCTURAL_EDITOR_CONTROL::open( const HANDLER_CONTEXT<S::OpenStructuralEditor>& ctx )
{
    const auto& request = ctx.Request;
    auto known = request; known.DiscardUnknownFields();
    if( known.ByteSizeLong() != request.ByteSizeLong() || request.schema_version() != 1
        || !request.has_document() || request.document().schema_version() != 1 || !request.document().has_diagram()
        || request.document().document_id() != request.document().diagram().id()
        || !KIID::SniffTest( wxString::FromUTF8( request.document().document_id() ) )
        || request.document().source_token().size() != 64
        || !IsRenderableStructuralDiagram( request.document().diagram() )
        || !wxFileName( wxString::FromUTF8( request.document().source_path() ) ).IsAbsolute()
        || !wxFileName( wxString::FromUTF8( request.helper_path() ) ).IsAbsolute()
        || !wxFileName::FileExists( wxString::FromUTF8( request.helper_path() ) )
        || !wxFileName::DirExists( wxString::FromUTF8( request.repository_root() ) ) )
        return failure( kiapi::common::AS_BAD_REQUEST, "An exact supported structural document and compiled helper are required" );
    for( auto& editor : m_editors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == request.document().document_id() )
    {
        if( editor->Document().source_path() != request.document().source_path()
            || editor->Document().source_token() != request.document().source_token() )
            return failure( kiapi::common::AS_BUSY, "The structural document is already open with another source revision" );
        editor->Show(); editor->Raise(); return state( *editor );
    }
    auto* frame = new STRUCTURAL_EDITOR_FRAME( m_manager, request.document(), wxString::FromUTF8( request.repository_root() ),
                                              wxString::FromUTF8( request.helper_path() ) );
    m_editors.emplace_back( frame ); frame->Show(); frame->Raise(); return state( *frame );
}
HANDLER_RESULT<S::StructuralEditorState> STRUCTURAL_EDITOR_CONTROL::read( const HANDLER_CONTEXT<S::ReadStructuralEditor>& ctx )
{
    for( auto& editor : m_editors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == ctx.Request.document_id() ) return state( *editor );
    return failure( kiapi::common::AS_BAD_REQUEST, "The explicitly identified structural document is not open" );
}

HANDLER_RESULT<D::RecursiveDiagramEditorState> STRUCTURAL_EDITOR_CONTROL::openRecursive( const HANDLER_CONTEXT<D::OpenRecursiveDiagramEditor>& ctx )
{
    const auto& request = ctx.Request;
    auto known = request; known.DiscardUnknownFields();
    if( known.ByteSizeLong() != request.ByteSizeLong() || request.schema_version() != 1
        || !KIID::SniffTest( wxString::FromUTF8( request.document_id() ) ) || request.expected_source_token().size() != 64
        || !wxFileName( wxString::FromUTF8( request.source_path() ) ).IsAbsolute()
        || !wxFileName( wxString::FromUTF8( request.helper_path() ) ).IsAbsolute()
        || !wxFileName::FileExists( wxString::FromUTF8( request.helper_path() ) )
        || !wxFileName::DirExists( wxString::FromUTF8( request.repository_root() ) ) )
        return failure( kiapi::common::AS_BAD_REQUEST, "An exact diagram target and compiled companion are required" );
    for( auto& editor : m_recursiveEditors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == request.document_id() )
    {
        if( editor->SourcePath() != request.source_path() ) return failure( kiapi::common::AS_BUSY, "This diagram is already open from another source" );
        editor->Show(); editor->Raise(); return editor->State();
    }
    auto* frame = new RECURSIVE_DIAGRAM_FRAME( m_manager, request );
    m_recursiveEditors.emplace_back( frame ); frame->Show(); frame->Raise(); return frame->State();
}
HANDLER_RESULT<D::RecursiveDiagramEditorState> STRUCTURAL_EDITOR_CONTROL::readRecursive( const HANDLER_CONTEXT<D::ReadRecursiveDiagramEditor>& ctx )
{
    for( auto& editor : m_recursiveEditors ) if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == ctx.Request.document_id() ) return editor->State();
    return failure( kiapi::common::AS_BAD_REQUEST, "The explicitly identified recursive diagram is not open" );
}
HANDLER_RESULT<D::RecursiveDiagramObservation> STRUCTURAL_EDITOR_CONTROL::observeRecursive( const HANDLER_CONTEXT<D::ObserveRecursiveDiagramEditor>& ctx )
{
    for( auto& editor : m_recursiveEditors )
        if( editor && !editor->IsClosing() && !editor->IsBeingDeleted() && editor->DocumentId() == ctx.Request.document_id() )
            return editor->Observe( ctx.Request );
    return failure( kiapi::common::AS_BAD_REQUEST, "The explicitly identified recursive diagram is not open" );
}
