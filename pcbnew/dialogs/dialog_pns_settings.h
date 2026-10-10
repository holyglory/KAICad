/*
 * KiRouter - a push-and-(sometimes-)shove PCB router
 *
 * Copyright (C) 2014  CERN
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * Author: Maciej Suminski <maciej.suminski@cern.ch>
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

#ifndef DIALOG_PNS_SETTINGS_H
#define DIALOG_PNS_SETTINGS_H

#include "dialog_pns_settings_base.h"

#include <wx/spinctrl.h>

namespace PNS {
class ROUTING_SETTINGS;
}

class BOARD_DESIGN_SETTINGS;

class DIALOG_PNS_SETTINGS : public DIALOG_PNS_SETTINGS_BASE
{
public:
    DIALOG_PNS_SETTINGS( wxWindow* aParent, PNS::ROUTING_SETTINGS& aSettings,
                         BOARD_DESIGN_SETTINGS* aBoardSettings = nullptr );

    bool TransferDataFromWindow() override;

private:
    virtual void onModeChange( wxCommandEvent& aEvent ) override;

private:
    PNS::ROUTING_SETTINGS& m_settings;
    BOARD_DESIGN_SETTINGS* m_boardSettings;
    wxCheckBox*            m_smoothConnections;
    wxCheckBox*            m_smoothRadiusPercentMode;
    wxSpinCtrlDouble*      m_smoothRadiusMm;
    wxSpinCtrlDouble*      m_smoothRadiusPercent;
    wxCheckBox*            m_flexibleRouting;
    wxCheckBox*            m_roundCorners;
    wxCheckBox*            m_cornerRadiusPercentMode;
    wxSpinCtrlDouble*      m_cornerRadiusMm;
    wxSpinCtrlDouble*      m_cornerRadiusPercent;
};

#endif // DIALOG_PNS_SETTINGS_H
