/* Native DRC invocation lifetime. GPL-3.0-or-later. */
#ifndef KICAD_DRC_RUN_SCOPE_H
#define KICAD_DRC_RUN_SCOPE_H

#include <drc/drc_engine.h>
#include <stdexcept>

/** Own the engine's borrowed invocation state, including exception unwinding. */
class DRC_RUN_SCOPE
{
public:
    DRC_RUN_SCOPE( DRC_ENGINE& aEngine, bool& aRunning ) :
            m_engine( aEngine ),
            m_running( aRunning )
    {
        if( m_running )
            throw std::logic_error( "A native DRC invocation is already active" );

        ClearBorrowedState();
        m_running = true;
    }

    ~DRC_RUN_SCOPE() noexcept
    {
        ClearBorrowedState();
        m_running = false;
    }

    DRC_RUN_SCOPE( const DRC_RUN_SCOPE& ) = delete;
    DRC_RUN_SCOPE& operator=( const DRC_RUN_SCOPE& ) = delete;

private:
    void ClearBorrowedState() noexcept
    {
        m_engine.SetProgressReporter( nullptr );
        m_engine.ClearViolationHandler();
        m_engine.SetSchematicNetlist( nullptr );
    }

    DRC_ENGINE& m_engine;
    bool&       m_running;
};

#endif
