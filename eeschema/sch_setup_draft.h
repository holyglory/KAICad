/* Detached Schematic Setup working state. GPL-3.0-or-later. */
#ifndef SCH_SETUP_DRAFT_H
#define SCH_SETUP_DRAFT_H

#include <json_common.h>
#include <project/project_file.h>
#include <erc/erc_settings.h>
#include <schematic_settings.h>

// A dialog can share these native typed owners between pages without exposing
// provisional values through its live PROJECT/SCHEMATIC. This class never saves
// or applies the draft; final validation and SCH_COMMIT remain the dialog owner.
class SCH_SETUP_DRAFT
{
public:
    explicit SCH_SETUP_DRAFT( const PROJECT_FILE& aSource ) :
        m_file( wxEmptyString ), m_erc( &m_file, "erc" ), m_schematic( &m_file, "schematic" )
    {
        m_file.m_ErcSettings = &m_erc;
        m_file.m_SchematicSettings = &m_schematic;
        m_baseline = aSource.CaptureCurrentState();
        aSource.CopyCurrentStateTo( m_file );
    }

    SCH_SETUP_DRAFT( const SCH_SETUP_DRAFT& ) = delete;
    SCH_SETUP_DRAFT& operator=( const SCH_SETUP_DRAFT& ) = delete;

    PROJECT_FILE& ProjectSettings() { return m_file; }
    ERC_SETTINGS& ErcSettings() { return m_erc; }
    SCHEMATIC_SETTINGS& SchematicSettings() { return m_schematic; }
    const nlohmann::json& Baseline() const { return m_baseline; }
    bool Changed() const { return m_file.CaptureCurrentState() != m_baseline; }
    bool MatchesLive( const PROJECT_FILE& aSource ) const
    {
        return aSource.CaptureCurrentState() == m_baseline;
    }

private:
    PROJECT_FILE m_file;
    ERC_SETTINGS m_erc;
    SCHEMATIC_SETTINGS m_schematic;
    nlohmann::json m_baseline;
};

#endif
