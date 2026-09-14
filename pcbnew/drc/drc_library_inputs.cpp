/* Owned footprint-library inputs for an isolated DRC invocation. GPL-3.0-or-later. */
#include "drc_library_inputs.h"
#include <board.h>
#include <footprint.h>
#include <footprint_library_adapter.h>
#include <libraries/library_table.h>
#include <progress_reporter.h>
#include <api/native_state_digest.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <json_common.h>
#include <richio.h>
#include <set>
#include <stdexcept>

const DRC_LIBRARY_INPUTS::ENTRY* DRC_LIBRARY_INPUTS::Find( const LIB_ID& aId ) const
{
    auto found = m_entries.find( aId );
    return found == m_entries.end() ? nullptr : &found->second;
}

std::string DRC_LIBRARY_INPUTS::ContentFingerprint() const
{
    NATIVE_STATE_DIGEST digest;
    for( const auto& [id, entry] : m_entries )
    {
        std::string definition;
        if( entry.footprint )
        {
            PCB_IO_KICAD_SEXPR writer( CTL_FOR_LIBRARY );
            STRING_FORMATTER output;
            writer.SetOutputFormatter( &output );
            writer.Format( entry.footprint.get() );
            definition = output.GetString();
        }
        // JSON provides unambiguous boundaries even for arbitrary library names,
        // source URIs and definition strings. Map traversal fixes entry order.
        digest.Append( nlohmann::json( { std::string( id.Format().c_str() ),
                static_cast<int>( entry.status ), entry.uri.utf8_string(), definition } ).dump() );
    }
    return digest.Hex();
}

std::shared_ptr<const DRC_LIBRARY_INPUTS> DRC_LIBRARY_INPUTS::Capture(
        const BOARD& aBoard, FOOTPRINT_LIBRARY_ADAPTER& aAdapter, PROGRESS_REPORTER* aReporter )
{
    auto result = std::make_shared<DRC_LIBRARY_INPUTS>();
    std::set<wxString> refreshed;
    for( const FOOTPRINT* footprint : aBoard.Footprints() )
    {
        if( aReporter && aReporter->IsCancelled() ) return nullptr;
        const LIB_ID& id = footprint->GetFPID();
        if( id.GetLibNickname().empty() || result->m_entries.contains( id ) ) continue;
        ENTRY entry;
        auto row = aAdapter.GetRow( id.GetLibNickname() );
        if( row )
        {
            entry.uri = LIBRARY_MANAGER::GetFullURI( *row, true );
            if( ( *row )->Disabled() ) entry.status = STATUS::DISABLED_LIBRARY;
            else if( !aAdapter.IsLibraryLoaded( id.GetLibNickname() ) )
                entry.status = STATUS::UNAVAILABLE_LIBRARY;
            else
            {
                if( refreshed.insert( id.GetLibNickname() ).second )
                    aAdapter.RefreshLibraryIfChanged( id.GetLibNickname(), true );
                if( aReporter && aReporter->IsCancelled() ) return nullptr;
                std::unique_ptr<FOOTPRINT> loaded( aAdapter.LoadFootprint( id, true ) );
                if( loaded )
                {
                    loaded->SetParent( nullptr );
                    entry.footprint = std::move( loaded );
                    entry.status = STATUS::LOADED;
                }
                else entry.status = STATUS::UNAVAILABLE_FOOTPRINT;
            }
        }
        result->m_entries.emplace( id, std::move( entry ) );
    }
    if( aReporter && aReporter->IsCancelled() ) return nullptr;
    return result;
}
