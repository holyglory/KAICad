/* Read-only file comparisons for lifecycle observations. GPL-3.0-or-later. */
#ifndef KICAD_NATIVE_FILE_OBSERVATION_H
#define KICAD_NATIVE_FILE_OBSERVATION_H
#include <file_content_baseline.h>
#include <api/common/commands/automation_commands.pb.h>

inline kiapi::automation::v1::NativeFileBaselineState ObserveNativeFile(
        const wxString& aPath, const FILE_CONTENT_BASELINE& aBaseline )
{
    using namespace kiapi::automation::v1;
    NativeFileBaselineState result;
    result.set_path( aPath.ToStdString( wxConvUTF8 ) );
    result.set_baseline_path( aBaseline.Path().ToStdString( wxConvUTF8 ) );
    result.set_baseline_known( aBaseline.Known() );
    result.set_baseline_exists( aBaseline.Exists() );
    result.set_baseline_sha256( aBaseline.Sha256() );
    result.set_baseline_bytes( aBaseline.Bytes() );
    const auto current = FILE_CONTENT_BASELINE::Read( aPath );
    result.set_current_known( current.Known() );
    result.set_current_exists( current.Exists() );
    result.set_current_sha256( current.Sha256() );
    result.set_current_bytes( current.Bytes() );
    if( !aBaseline.Known() ) result.set_status( NFBS_UNKNOWN );
    else if( !FILE_CONTENT_BASELINE::SamePath( aBaseline.Path(), aPath ) ) result.set_status( NFBS_WRONG_PATH );
    else if( !current.Known() ) result.set_status( NFBS_UNREADABLE );
    else if( aBaseline.Exists() == current.Exists() && aBaseline.Bytes() == current.Bytes()
             && aBaseline.Sha256() == current.Sha256() ) result.set_status( NFBS_UNCHANGED );
    else result.set_status( NFBS_CHANGED );
    return result;
}
#endif
