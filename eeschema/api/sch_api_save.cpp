/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
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
 * You should have received a copy of the GNU General Public License along
 * with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

#include <api/sch_api_save.h>

#include <base_screen.h>
#include <pgm_base.h>
#include <project.h>
#include <project/project_file.h>
#include <sch_io/sch_io.h>
#include <sch_io/sch_io_mgr.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <schematic.h>
#include <sch_root_instance.h>
#include <settings/settings_manager.h>
#include <wildcards_and_files_ext.h>

#include <wx/filename.h>
#include <wx/log.h>


namespace SCH_API_SAVE
{

namespace
{
bool WritableDestination( const wxString& aPath )
{
    wxFileName path( aPath );
    if( aPath.empty() || !path.IsOk() || !path.IsAbsolute() || wxDirExists( aPath ) ) return false;
    if( path.FileExists() ) return path.IsFileWritable();
    if( path.DirExists() ) return path.IsDirWritable();
    // Preserve SaveSheetToFile's existing single-directory creation workflow.
    wxFileName parent( path.GetPath(), wxEmptyString );
    if( parent.GetDirCount() == 0 ) return false;
    parent.RemoveLastDir();
    return parent.DirExists() && parent.IsDirWritable();
}
}

bool SaveSheetToFile( SCH_SHEET* aSheet, SCHEMATIC& aSchematic, const wxString& aPath )
{
    wxCHECK( aSheet, false );

    if( aPath.IsEmpty() )
        return false;

    wxFileName schematicFileName( aPath );
    schematicFileName.MakeAbsolute();

    if( !schematicFileName.DirExists() && !wxMkdir( schematicFileName.GetPath() ) )
        return false;

    if( schematicFileName.FileExists() && !schematicFileName.IsFileWritable() )
        return false;

    SCH_IO_MGR::SCH_FILE_T pluginType = SCH_IO_MGR::GuessPluginTypeFromSchPath( schematicFileName.GetFullPath() );

    if( pluginType == SCH_IO_MGR::SCH_FILE_UNKNOWN )
        pluginType = SCH_IO_MGR::SCH_KICAD;

    IO_RELEASER<SCH_IO> pi( SCH_IO_MGR::FindPlugin( pluginType ) );

    try
    {
        pi->SaveSchematicFile( schematicFileName.GetFullPath(), aSheet, &aSchematic );
        return true;
    }
    catch( const IO_ERROR& ioe )
    {
        wxLogTrace( wxS( "KI_TRACE_API" ), wxS( "SaveSheetToFile failed: %s" ), ioe.What() );
        return false;
    }
}


bool UpdateProjectFile( SCHEMATIC& aSchematic, PROJECT& aProject )
{
    SCH_SCREEN* rootScreen = aSchematic.RootScreen();

    if( !rootScreen )
        return false;

    wxFileName projectFile( aProject.GetProjectFullName() );

    if( !projectFile.HasName() || !projectFile.IsOk() )
        return false;

    aSchematic.RecordERCExclusions();

    if( rootScreen )
    {
        aProject.GetProjectFile().m_IP2581Bom.schRevision = rootScreen->GetTitleBlock().GetRevision();
    }

    const std::vector<SCH_SHEET*>& topLevelSheets = aSchematic.GetTopLevelSheets();

    if( !topLevelSheets.empty() )
    {
        std::vector<TOP_LEVEL_SHEET_INFO>& projectSheets = aProject.GetProjectFile().GetTopLevelSheets();
        projectSheets.clear();

        wxString projectPath = aProject.GetProjectPath();

        for( SCH_SHEET* sheet : topLevelSheets )
        {
            TOP_LEVEL_SHEET_INFO info;
            info.uuid = sheet->m_Uuid;
            info.name = sheet->GetName();

            wxString filename;

            if( sheet->GetScreen() )
                filename = sheet->GetScreen()->GetFileName();

            wxFileName sheetFn( filename );

            if( sheetFn.IsAbsolute() )
                sheetFn.MakeRelativeTo( projectPath );

            info.filename = sheetFn.GetFullPath();
            projectSheets.push_back( std::move( info ) );
        }
    }

    std::vector<FILE_INFO_PAIR>& sheets = aProject.GetProjectFile().GetSheets();
    sheets.clear();

    if( !aSchematic.HasHierarchy() )
        aSchematic.RefreshHierarchy();

    for( SCH_SHEET_PATH& sheetPath : aSchematic.Hierarchy() )
    {
        SCH_SHEET* sheet = sheetPath.Last();

        wxCHECK2( sheet, continue );

        if( !sheet->IsVirtualRootSheet() )
            sheets.emplace_back( std::make_pair( sheet->m_Uuid, sheet->GetName() ) );
    }

    return Pgm().GetSettingsManager().SaveProject( projectFile.GetFullPath(), &aProject );
}


bool SaveSchematic( SCHEMATIC& aSchematic, PROJECT& aProject )
{
    // All callers, including Save Copy targeting the current root filename,
    // must reject known conflicts before writing the first screen.
    if( HasRootInstanceConflicts( aSchematic ) )
        return false;

    SCH_SCREEN* rootScreen = aSchematic.RootScreen();

    if( !rootScreen || rootScreen->GetFileName().IsEmpty() )
        return false;

    SCH_SCREENS screens( aSchematic.Root() );
    screens.BuildClientSheetPathList();

    if( aProject.IsReadOnly() || aProject.GetProjectFile().IsReadOnly()
            || !WritableDestination( aProject.GetProjectFullName() ) )
        return false;
    for( size_t i = 0; i < screens.GetCount(); ++i )
    {
        const SCH_SHEET* sheet = screens.GetSheet( i );
        if( sheet && sheet->IsVirtualRootSheet() ) continue;
        const SCH_SCREEN* screen = screens.GetScreen( i );
        if( !sheet || !screen || screen->GetFileName().empty()
                || !WritableDestination( aProject.AbsolutePath( screen->GetFileName() ) ) )
            return false;
    }

    bool success = true;

    for( size_t i = 0; i < screens.GetCount(); i++ )
    {
        if( screens.GetSheet( i )->IsVirtualRootSheet() ) continue;
        SCH_SCREEN* screen = screens.GetScreen( i );

        wxCHECK2( screen, continue );

        wxFileName fileName = aProject.AbsolutePath( screen->GetFileName() );

        if( !fileName.IsOk() )
            continue;

        std::vector<SCH_SHEET_PATH>& sheets = screen->GetClientSheetPaths();

        if( sheets.size() == 1 )
            screen->SetVirtualPageNumber( 1 );
        else
            screen->SetVirtualPageNumber( 0 );

        success &= SaveSheetToFile( screens.GetSheet( i ), aSchematic, fileName.GetFullPath() );

    }

    if( success )
        success = UpdateProjectFile( aSchematic, aProject );

    if( success )
        for( size_t i = 0; i < screens.GetCount(); ++i )
            if( !screens.GetSheet( i )->IsVirtualRootSheet() )
                screens.GetScreen( i )->SetContentModified( false );

    return success;
}


bool SaveSchematicCopy( SCHEMATIC& aSchematic, PROJECT& aProject, const wxString& aFileName, bool aCreateProject )
{
    wxFileName schematicFileName( aFileName );
    schematicFileName.MakeAbsolute();

    if( !schematicFileName.IsOk() || !schematicFileName.IsDirWritable() )
        return false;

    // Root() is the virtual root, whose screen only holds the top-level sheets
    SCH_SHEET*  rootSheet = aSchematic.GetTopLevelSheet();
    SCH_SCREEN* rootScreen = aSchematic.RootScreen();

    if( !rootSheet || !rootScreen )
        return false;

    if( schematicFileName.GetFullPath() == rootScreen->GetFileName() )
        return SaveSchematic( aSchematic, aProject );

    if( !SaveSheetToFile( rootSheet, aSchematic, schematicFileName.GetFullPath() ) )
        return false;

    if( aCreateProject )
    {
        wxFileName projectFile( schematicFileName );
        projectFile.SetExt( FILEEXT::ProjectFileExtension );

        if( !projectFile.FileExists() )
            return Pgm().GetSettingsManager().SaveProjectCopy( projectFile.GetFullPath(), &aProject );
    }

    return true;
}

} // namespace SCH_API_SAVE
