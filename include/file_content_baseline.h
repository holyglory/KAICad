/* Loaded/written native file versions. GPL-3.0-or-later. */
#ifndef KICAD_FILE_CONTENT_BASELINE_H
#define KICAD_FILE_CONTENT_BASELINE_H

#include <kicommon.h>
#include <file_write_observer.h>
#include <wx/string.h>
#include <cstdint>
#include <string>
#include <string_view>

enum class FILE_BASELINE_CHECK { UNKNOWN, UNCHANGED, CHANGED, WRONG_PATH, UNREADABLE };

/** A content baseline, not a lock or permission to overwrite a file. */
class KICOMMON_API FILE_CONTENT_BASELINE
{
public:
    // An unknown default must never be interpreted as an absent file.
    static FILE_CONTENT_BASELINE Read( const wxString& aPath, std::string* aContents = nullptr );
    static FILE_CONTENT_BASELINE FromBytes( const wxString& aPath, std::string_view aBytes,
                                            bool aTextMode = false );
    static bool SamePath( const wxString& aLeft, const wxString& aRight );

    FILE_BASELINE_CHECK Check( const wxString& aPath ) const;
    bool Known() const { return m_known; }
    bool Exists() const { return m_exists; }
    const wxString& Path() const { return m_path; }
    const std::string& Sha256() const { return m_sha256; }
    uint64_t Bytes() const { return m_bytes; }

private:
    friend class DIGESTING_FILE_LINE_READER;
    static FILE_CONTENT_BASELINE FromDigest( const wxString& aPath, const std::string& aSha256,
                                             uint64_t aBytes );
    wxString m_path;
    std::string m_sha256;
    uint64_t m_bytes = 0;
    bool m_known = false;
    bool m_exists = false;
};

#endif
