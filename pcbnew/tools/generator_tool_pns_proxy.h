/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2023 Alex Shvartzkop <dudesuchamazing@gmail.com>
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef GENERATOR_TOOL_PNS_PROXY_H
#define GENERATOR_TOOL_PNS_PROXY_H

#include <router/pns_tool_base.h>

class BOARD_ITEM;
class BOARD_COMMIT;
namespace PNS { class ROUTING_SETTINGS; }


struct GENERATOR_PNS_CHANGES
{
    std::set<BOARD_ITEM*> addedItems;
    std::set<BOARD_ITEM*> removedItems;
};


/**
 * A proxy class to allow access to the PNS router from the generator tool.
 */
class GENERATOR_TOOL_PNS_PROXY : public PNS::TOOL_BASE
{
public:
    GENERATOR_TOOL_PNS_PROXY( const std::string& aToolName );
    ~GENERATOR_TOOL_PNS_PROXY();

    /// @copydoc TOOL_INTERACTIVE::Reset()
    void Reset( RESET_REASON aReason ) override;
    void InitializeSnapshot( std::unique_ptr<PNS::ROUTING_SETTINGS> aSettings );

    void                                      ClearRouterChanges();
    const std::vector<GENERATOR_PNS_CHANGES>& GetRouterChanges();

    /// Apply buffered in-place changes after staging additions/removals in the owning edit.
    /// Preview routing never modifies the board; clearing changes discards these updates.
    void ApplyRouterUpdates( BOARD_COMMIT& aCommit );

    /// Returns true if the given board item was created by the router interface during the
    /// current generator edit session
    bool ItemCreatedBySession( BOARD_ITEM* aItem ) const;

private:
    std::unique_ptr<PNS::ROUTING_SETTINGS> m_snapshotSettings;
};

#endif // GENERATOR_TOOL_PNS_PROXY_H
