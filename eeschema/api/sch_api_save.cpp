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
#include <api/document_lifecycle_controller.h>

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
using SAVE_PROBLEM = DOCUMENT_LIFECYCLE_CONTROLLER::SAVE_PROBLEM;

// Tell a running checked save that the file system will not accept a write of this file; plain
// saves only keep the trace.
void ReportBlocked( const wxString& aPath, const wxString& aReason )
{
    DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::WRITE_BLOCKED, aPath, aReason );
    wxLogTrace( wxS( "KI_TRACE_API" ), wxS( "Cannot write '%s': %s" ), aPath, aReason );
}


// Tell a running checked save that KiCad refuses to save for a reason that writable files would
// not fix. @a aPath may be empty.
void ReportRefused( const wxString& aPath, const wxString& aReason )
{
    DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM::SAVE_REFUSED, aPath, aReason );
    wxLogTrace( wxS( "KI_TRACE_API" ), wxS( "Save refused for '%s': %s" ), aPath, aReason );
}


// The file and the folder it is replaced in must both accept the write, following a symbolic
// link as the writer does, or no file of the save is written.
bool WritableDestination( const wxString& aPath )
{
    const wxString reason = DOCUMENT_LIFECYCLE_CONTROLLER::WriteBlocker( aPath );

    if( reason.empty() )
        return true;

    ReportBlocked( aPath, reason );
    return false;
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
    {
        ReportBlocked( schematicFileName.GetFullPath(), wxS( "its folder could not be created" ) );
        return false;
    }

    if( schematicFileName.FileExists() && !schematicFileName.IsFileWritable() )
    {
        ReportBlocked( schematicFileName.GetFullPath(), wxS( "the file is read-only" ) );
        return false;
    }

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
        // Formatting refuses conflicting root page numbers before anything is written; every
        // other error comes from writing the file, for example a full disk, and names the file
        // and the system error.
        if( ResolveRootInstance( &aSchematic, *aSheet ).conflict )
            ReportRefused( schematicFileName.GetFullPath(),
                           wxS( "one shared sheet file has conflicting root page numbers; set its root page "
                                "number explicitly" ) );
        else
            ReportBlocked( schematicFileName.GetFullPath(), ioe.Problem() );

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
    SCH_SCREEN* rootScreen = aSchematic.RootScreen();

    if( HasRootInstanceConflicts( aSchematic ) )
    {
        ReportRefused( wxEmptyString, wxS( "one shared sheet file has conflicting root page numbers; set its root "
                                           "page number explicitly" ) );
        return false;
    }

    if( !rootScreen || rootScreen->GetFileName().IsEmpty() )
    {
        ReportRefused( wxEmptyString, wxS( "the root schematic has no file name" ) );
        return false;
    }

    SCH_SCREENS screens( aSchematic.Root() );
    screens.BuildClientSheetPathList();

    // Check every file before writing the first one, and report each problem, so a blocked
    // save changes nothing on disk and the caller can fix all causes at once.
    bool writable = true;

    if( aProject.IsReadOnly() || aProject.GetProjectFile().IsReadOnly() )
    {
        // KiCad's own state, not the file system: making the file writable does not change it.
        ReportRefused( aProject.GetProjectFullName(),
                       wxS( "KiCad opened this project read-only (another KiCad may hold its lock, or the "
                            "schematic was opened without its project file) and writes no files for it" ) );
        writable = false;
    }
    else if( !WritableDestination( aProject.GetProjectFullName() ) )
    {
        writable = false;
    }

    for( size_t i = 0; i < screens.GetCount(); ++i )
    {
        const SCH_SHEET* sheet = screens.GetSheet( i );
        if( sheet && sheet->IsVirtualRootSheet() ) continue;
        const SCH_SCREEN* screen = screens.GetScreen( i );
        if( !sheet || !screen || screen->GetFileName().empty() )
        {
            ReportRefused( wxEmptyString,
                           sheet ? wxString::Format( wxS( "sheet '%s' has no file name" ), sheet->GetName() )
                                 : wxString( wxS( "a sheet has no file name" ) ) );
            writable = false;
        }
        else if( !WritableDestination( aProject.AbsolutePath( screen->GetFileName() ) ) )
        {
            writable = false;
        }
    }

    if( !writable )
        return false;

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

        // Stop at the first failure: later files keep their old content, the editor keeps every
        // change, and the next successful save writes the whole hierarchy again.
        if( !SaveSheetToFile( screens.GetSheet( i ), aSchematic, fileName.GetFullPath() ) )
            return false;
    }

    if( !UpdateProjectFile( aSchematic, aProject ) )
    {
        // The project file and its folder accepted writes before the first sheet was written,
        // so check again for what changed; the settings writer itself gives no reason.
        const wxString reason = DOCUMENT_LIFECYCLE_CONTROLLER::WriteBlocker( aProject.GetProjectFullName() );
        ReportBlocked( aProject.GetProjectFullName(),
                       reason.empty() ? wxString( wxS( "writing the project settings failed after the sheets were "
                                                       "written, and the settings writer gave no system reason" ) )
                                      : reason );
        return false;
    }

    for( size_t i = 0; i < screens.GetCount(); ++i )
        if( !screens.GetSheet( i )->IsVirtualRootSheet() )
            screens.GetScreen( i )->SetContentModified( false );

    return true;
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
