/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_PANEL_DIAGRAM_HISTORY_H
#define KICAD_PANEL_DIAGRAM_HISTORY_H

#include <api/common/types/diagram_revision_types.pb.h>
#include <wx/panel.h>
#include <functional>
#include <optional>

class wxButton;
class wxStaticText;
class wxTextCtrl;
class DIAGRAM_HISTORY_ROWS;

/** Read-only history navigation. Preview and preparing a restoration are
 * separate callbacks; selecting a row never changes an editor or design. */
class PANEL_DIAGRAM_HISTORY : public wxPanel
{
public:
    using SELECTION = kiapi::automation::diagrams::v1::BlockSelectionData;
    using PAGE = kiapi::automation::diagrams::v1::DiagramHistoryPageData;
    using COMPARISON = kiapi::automation::diagrams::v1::DiagramHistoryComparisonData;
    struct ACTIONS
    {
        std::function<void( unsigned )> load;
        std::function<void( SELECTION )> inspect, preview, restore;
        std::function<void()> close, returnToCurrent, retry;
    };
    PANEL_DIAGRAM_HISTORY( wxWindow* aParent, ACTIONS aActions );
    void Begin( SELECTION aContext, const wxString& aName, int aVersion );
    bool SetPage( const PAGE& aPage );
    bool SetComparison( const COMPARISON& aComparison );
    void SetBusy( bool aBusy );
    void Fail( const wxString& aMessage );
    void SetPreviewing( bool aPreviewing );
    std::optional<SELECTION> Inspected() const;
    const SELECTION& Context() const { return m_context; }
    unsigned LoadedCount() const;
    unsigned TotalCount() const { return m_total; }
    bool Busy() const { return m_busy; }
    const std::string& Error() const { return m_error; }

private:
    void select();
    void updateActions();
    ACTIONS m_actions;
    SELECTION m_context;
    unsigned m_total = 0;
    bool m_busy = false, m_comparisonReady = false, m_previewing = false;
    std::string m_error;
    wxStaticText *m_title, *m_saved, *m_count, *m_failure;
    DIAGRAM_HISTORY_ROWS* m_rows;
    wxTextCtrl* m_details;
    wxButton *m_more, *m_retry, *m_preview, *m_restore, *m_return;
};
#endif
