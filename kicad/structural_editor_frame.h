/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_STRUCTURAL_EDITOR_FRAME_H
#define KICAD_STRUCTURAL_EDITOR_FRAME_H

#include <wx/frame.h>
#include <wx/geometry.h>
#include <wx/process.h>
#include <wx/timer.h>
#include <api/common/types/structural_types.pb.h>
#include <functional>
#include <memory>
#include <string>
#include <vector>

class wxPanel;
class wxTextCtrl;
class wxChoice;
class wxTreeCtrl;
class wxStaticText;
class wxButton;
class wxToolBar;
class wxDC;

namespace kiapi::automation::structure::v1 { class StructuralDiagramData; }

/** Native editable structural view. Engineering XML validation/publication
 * belongs to the existing compiled .NET companion, not this window. */
class STRUCTURAL_EDITOR_FRAME : public wxFrame
{
public:
    STRUCTURAL_EDITOR_FRAME( wxWindow* aParent,
            const kiapi::automation::structure::v1::StructuralEditorDocument& aDocument,
            wxString aRepositoryRoot, wxString aHelper,
            std::function<void()> aOpenSchematic = {} );
    ~STRUCTURAL_EDITOR_FRAME() override;

    const std::string& DocumentId() const { return m_document.document_id(); }
    const kiapi::automation::structure::v1::StructuralEditorDocument& Document() const { return m_document; }
    bool IsDirty() const { return m_dirty; }
    bool IsSaving() const { return m_process != nullptr; }
    bool IsClosing() const { return m_closing; }
    bool HasRendered() const { return m_rendered; }
    uint64_t Revision() const { return m_revision; }
    uint64_t CompletedSaveCount() const { return m_completedSaveCount; }
    const std::string& LastSaveError() const { return m_lastSaveError; }
    const std::string& SelectedBlockId() const { return m_selected; }

private:
    enum class MODE { SELECT, ADD_BLOCK, CONNECT };
    void paint( wxDC& aDC );
    void mouseDown( wxMouseEvent& aEvent );
    void mouseMove( wxMouseEvent& aEvent );
    void mouseUp( wxMouseEvent& aEvent );
    void select( const std::string& aId );
    void refreshModel();
    void fillInspector();
    void commit( const kiapi::automation::structure::v1::StructuralDiagramData& aBefore );
    bool propertiesChanged();
    void addInstruction();
    void addProperty();
    void undo();
    void redo();
    void removeSelection();
    void save();
    void helperFinished( wxProcessEvent& aEvent );
    void drainHelper();
    void closeEditor( wxCloseEvent& aEvent );
    void fit();
    wxPoint screenPoint( int64_t aX, int64_t aY ) const;
    wxPoint2DDouble modelPoint( const wxPoint& aPoint ) const;
    kiapi::automation::structure::v1::StructuralBlockPlacement* placement( const std::string& aBlockId );
    const kiapi::automation::structure::v1::StructuralBlockData* selectedBlock() const;
    std::string hitBlock( const wxPoint& aPoint ) const;
    std::string createPort( const std::string& aBlockId, bool aRight );
    kiapi::automation::structure::v1::StructuralBlockPlacement blockLayout( const std::string& aId ) const;
    wxPoint portPoint( const std::string& aId ) const;
    wxPoint portDirection( const std::string& aId ) const;
    void zoom( double aFactor, wxPoint aAnchor );

    kiapi::automation::structure::v1::StructuralEditorDocument m_document;
    std::vector<kiapi::automation::structure::v1::StructuralDiagramData> m_undo;
    std::vector<kiapi::automation::structure::v1::StructuralDiagramData> m_redo;
    kiapi::automation::structure::v1::StructuralDiagramData m_dragBefore;
    wxString m_repositoryRoot, m_helper;
    std::function<void()> m_openSchematic;
    wxPanel* m_canvas = nullptr;
    wxTextCtrl* m_name = nullptr;
    wxTextCtrl* m_purpose = nullptr;
    wxTextCtrl* m_instruction = nullptr;
    wxChoice* m_strength = nullptr;
    wxChoice* m_instructions = nullptr;
    wxTreeCtrl* m_tree = nullptr;
    wxStaticText* m_part = nullptr;
    wxStaticText* m_source = nullptr;
    wxToolBar* m_toolbar = nullptr;
    wxButton* m_addProperty = nullptr;
    std::string m_selected, m_instructionId, m_connectFrom;
    MODE m_mode = MODE::SELECT;
    double m_scale = 0.000006;
    wxPoint2DDouble m_origin{ 0, 0 };
    wxPoint m_dragStart;
    int64_t m_dragX = 0, m_dragY = 0;
    int64_t m_dragWidth = 0, m_dragHeight = 0;
    bool m_resizing = false, m_panning = false;
    wxPoint2DDouble m_panOrigin;
    bool m_dragging = false, m_updating = false, m_dirty = false, m_closeAfterSave = false;
    uint64_t m_revision = 0, m_saveRevision = 0;
    std::unique_ptr<wxProcess> m_process;
    wxTimer m_ioTimer;
    std::string m_stdout, m_stderr;
    long m_helperPid = 0;
    bool m_rendered = false;
    bool m_closing = false;
    std::vector<std::string> m_instructionIds;
    uint64_t m_completedSaveCount = 0;
    std::string m_lastSaveError;
};

#endif
