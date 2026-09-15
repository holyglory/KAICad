/* Windows native stream mode selection. GPL-3.0-or-later. */
#ifndef KIPLATFORM_WINDOWS_FILE_STREAM_MODE_H
#define KIPLATFORM_WINDOWS_FILE_STREAM_MODE_H

#include <cwchar>
#include <fcntl.h>

namespace KIPLATFORM::IO::DETAIL
{
// _wfdopen associates a stream with an existing descriptor. Select translation
// on that descriptor instead of assuming the stream-mode string changes it.
inline int WritableFileDescriptorFlags( const wchar_t* aMode )
{
    return _O_WRONLY | ( aMode && std::wcschr( aMode, L'b' ) ? _O_BINARY : _O_TEXT );
}
}

#endif
