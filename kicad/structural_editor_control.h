/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_STRUCTURAL_EDITOR_CONTROL_H
#define KICAD_STRUCTURAL_EDITOR_CONTROL_H
#include <api/api_handler.h>
#include <api/common/commands/structural_commands.pb.h>
#include <api/common/commands/recursive_diagram_commands.pb.h>
#include <wx/weakref.h>
#include <vector>

class KICAD_MANAGER_FRAME;
class STRUCTURAL_EDITOR_FRAME;
class RECURSIVE_DIAGRAM_FRAME;

class STRUCTURAL_EDITOR_CONTROL : public API_HANDLER
{
public:
    explicit STRUCTURAL_EDITOR_CONTROL( KICAD_MANAGER_FRAME* aManager );
    ~STRUCTURAL_EDITOR_CONTROL() override;
    bool CloseEditors();
private:
    HANDLER_RESULT<kiapi::automation::structure::v1::StructuralEditorState> open(
        const HANDLER_CONTEXT<kiapi::automation::structure::v1::OpenStructuralEditor>& aCtx );
    HANDLER_RESULT<kiapi::automation::structure::v1::StructuralEditorState> read(
        const HANDLER_CONTEXT<kiapi::automation::structure::v1::ReadStructuralEditor>& aCtx );
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramEditorState> openRecursive(
        const HANDLER_CONTEXT<kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor>& aCtx );
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramEditorState> readRecursive(
        const HANDLER_CONTEXT<kiapi::automation::diagrams::v1::ReadRecursiveDiagramEditor>& aCtx );
    KICAD_MANAGER_FRAME* m_manager;
    std::vector<wxWeakRef<STRUCTURAL_EDITOR_FRAME>> m_editors;
    std::vector<wxWeakRef<RECURSIVE_DIAGRAM_FRAME>> m_recursiveEditors;
};
#endif
