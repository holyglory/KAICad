/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_DIALOG_DIAGRAM_CONFLICT_H
#define KICAD_DIALOG_DIAGRAM_CONFLICT_H
#include <dialog_shim.h>
#include <api/common/types/diagram_revision_types.pb.h>
#include <map>
#include <optional>
#include <string>
#include <vector>

class wxButton;
class wxChoice;
class wxRadioButton;
class wxStaticText;
class wxTextCtrl;

/** Explicit three-way field choices. Cancel keeps all versions; acceptance only
 * returns revision-bound choices for the companion to revalidate before Save. */
class DIALOG_DIAGRAM_CONFLICT : public DIALOG_SHIM
{
public:
    DIALOG_DIAGRAM_CONFLICT( wxWindow* aParent, const wxString& aOwnerPath,
            const kiapi::automation::diagrams::v1::RequirementMergeData& aMerge );
    std::vector<kiapi::automation::diagrams::v1::RequirementResolutionData> Resolutions() const;

private:
    void displayField();
    void choose( int aKind );
    const kiapi::automation::diagrams::v1::RequirementMergeData m_merge;
    std::map<int, std::string> m_choices;
    std::map<int, int> m_choiceKinds;
    int m_index = 0;
    bool m_updating = false;
    wxChoice* m_field;
    wxTextCtrl* m_base;
    wxTextCtrl* m_draft;
    wxTextCtrl* m_saved;
    wxTextCtrl* m_resolved;
    wxRadioButton* m_mine;
    wxRadioButton* m_latest;
    wxRadioButton* m_custom;
    wxButton* m_save;
};
#endif
