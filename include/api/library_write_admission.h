/* Native library write admission. GPL-3.0-or-later. */
#ifndef KICAD_LIBRARY_WRITE_ADMISSION_H
#define KICAD_LIBRARY_WRITE_ADMISSION_H

#include <file_content_baseline.h>

#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

class LOCKFILE;

/**
 * Reserves a complete, caller-supplied set of native files for a future library write.
 *
 * This class only proves that the files were readable and unchanged when admission was
 * acquired, and keeps the corresponding LOCKFILEs alive until destruction.  It does not
 * perform a write and does not claim a commit, rollback, revision, or mutation receipt.
 * The paths returned by Before() and Observe() retain the exact requested path spelling;
 * symlink-resolved paths are used only for identity, reservation, and locking.
 */
class KICOMMON_API LIBRARY_WRITE_ADMISSION
{
public:
    static std::unique_ptr<LIBRARY_WRITE_ADMISSION> Acquire(
            const std::vector<FILE_CONTENT_BASELINE>& aExpected );

    ~LIBRARY_WRITE_ADMISSION();

    LIBRARY_WRITE_ADMISSION( const LIBRARY_WRITE_ADMISSION& ) = delete;
    LIBRARY_WRITE_ADMISSION& operator=( const LIBRARY_WRITE_ADMISSION& ) = delete;

    /// The complete requested-path baselines admitted by Acquire().
    const std::vector<FILE_CONTENT_BASELINE>& Before() const { return m_expected; }

    /// Recheck every admitted requested path and throw if its binding or full baseline changed.
    void CheckUnchanged() const;

    /// Capture current requested-path baselines after checking identity.  Contents may differ.
    std::vector<FILE_CONTENT_BASELINE> Observe() const;

private:
    struct ENTRY
    {
        FILE_CONTENT_BASELINE expected;
        wxString              resolved;
    };

    explicit LIBRARY_WRITE_ADMISSION( std::vector<ENTRY> aEntries );

    static wxString resolvePath( const wxString& aRequested );
    static void validateRequestedBaseline( const FILE_CONTENT_BASELINE& aBaseline );

    void releaseReservations() noexcept;
    void verifyBindings() const;
    std::vector<FILE_CONTENT_BASELINE> observeCurrent() const;

    std::vector<FILE_CONTENT_BASELINE> m_expected;
    std::vector<ENTRY>                  m_entries;
    std::vector<std::unique_ptr<LOCKFILE>> m_locks;
    std::vector<wxString>               m_reserved;

    static std::mutex                   s_registryMutex;
    static std::set<std::string>        s_reservedPaths;
};

#endif // KICAD_LIBRARY_WRITE_ADMISSION_H
