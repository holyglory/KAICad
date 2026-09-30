/* Owned, identity-bound native schematic input for PCB checks. GPL-3.0-or-later. */
#include "pcb_drc_schematic_input.h"
#include <api/native_state_digest.h>
#include <dsnlexer.h>
#include <google/protobuf/util/message_differencer.h>
#include <netlist_reader/pcb_netlist.h>
#include <netlist_reader/netlist_reader.h>
#include <set>
#include <stdexcept>

namespace
{
// The general native reader tolerates missing sections and old formats for manual
// import. A complete captured comparison may not silently inherit that tolerance.
// Use KiCad's lexer for quoting/escaping and keep object decoding in its reader.
void ValidateEnvelope( const std::string& text )
{
    DSNLEXER lexer( text, "captured schematic comparison" );
    lexer.NeedLEFT();
    lexer.NeedSYMBOL();
    if( std::string( lexer.CurText() ) != "export" )
        throw std::invalid_argument( "Schematic comparison payload is not a native netlist" );
    std::set<std::string> sections;
    const std::set<std::string> allowed{ "version", "design", "components", "groups", "variants",
                                          "libparts", "libraries", "nets", "net_chains" };
    for( int token = lexer.NextTok(); token != DSN_RIGHT; token = lexer.NextTok() )
    {
        if( token != DSN_LEFT ) throw std::invalid_argument( "Incomplete native netlist envelope" );
        lexer.NeedSYMBOL();
        const std::string section = lexer.CurText();
        if( !allowed.contains( section ) || !sections.insert( section ).second )
            throw std::invalid_argument( "Unknown or repeated native netlist section: " + section );
        if( section == "version" )
        {
            lexer.NeedSYMBOLorNUMBER();
            if( std::string( lexer.CurText() ) != "E" )
                throw std::invalid_argument( "Unsupported native schematic comparison format" );
            lexer.NeedRIGHT();
            continue;
        }
        int depth = 1;
        while( depth )
        {
            token = lexer.NextTok();
            if( token == DSN_EOF ) throw std::invalid_argument( "Truncated native netlist section" );
            if( token == DSN_LEFT ) ++depth;
            else if( token == DSN_RIGHT ) --depth;
        }
    }
    if( lexer.NextTok() != DSN_EOF ) throw std::invalid_argument( "Trailing native netlist data" );
    for( const std::string required : { "version", "design", "components", "libparts", "libraries", "nets" } )
        if( !sections.contains( required ) )
            throw std::invalid_argument( "Missing native netlist section: " + required );
}
}

PCB_DRC_SCHEMATIC_INPUT::~PCB_DRC_SCHEMATIC_INPUT() = default;
NETLIST& PCB_DRC_SCHEMATIC_INPUT::Netlist() const { return *m_netlist; }

bool PCB_DRC_SCHEMATIC_INPUT::Matches( const kiapi::automation::v1::DocumentLifecycleState& aCurrent ) const
{
    return google::protobuf::util::MessageDifferencer::Equals( m_source, aCurrent );
}

std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> PCB_DRC_SCHEMATIC_INPUT::Capture(
        const kiapi::automation::v1::SchematicParityNetlistSnapshot& snapshot,
        const kiapi::automation::v1::DocumentLifecycleState& expected,
        const kiapi::common::types::DocumentSpecifier& board, const std::string& processEpoch )
{
    using namespace kiapi::automation::v1;
    using namespace kiapi::common::types;
    const auto& source = snapshot.source_state();
    if( snapshot.schema_version() != 1 || !snapshot.has_source_state()
        || source.scope() != DLS_SCHEMATIC_HIERARCHY || source.document().type() != DOCTYPE_SCHEMATIC
        || source.document().sheet_path().path_size() == 0 || board.type() != DOCTYPE_PCB
        || source.native_identity().empty() || source.revision().epoch().empty()
        || source.state_sha256().size() != 64 || !source.project_settings_included() )
        throw std::invalid_argument( "Schematic comparison requires a complete identity-bound source state" );
    if( processEpoch.empty() || source.process_epoch() != processEpoch
        || !google::protobuf::util::MessageDifferencer::Equals( source, expected )
        || !google::protobuf::util::MessageDifferencer::Equals( source.document().project(), board.project() ) )
        throw std::invalid_argument( "Schematic comparison belongs to a different project, process or state" );
    NATIVE_STATE_DIGEST digest;
    digest.Append( snapshot.native_netlist_sexpr() );
    if( snapshot.netlist_sha256() != digest.Hex() )
        throw std::invalid_argument( "Schematic comparison payload does not match its captured digest" );
    ValidateEnvelope( snapshot.native_netlist_sexpr() );
    auto result = std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT>( new PCB_DRC_SCHEMATIC_INPUT );
    result->m_source = source;
    result->m_netlist = std::make_unique<NETLIST>();
    KICAD_NETLIST_READER reader( new STRING_LINE_READER( snapshot.native_netlist_sexpr(),
                                                       "captured schematic comparison" ), result->m_netlist.get() );
    reader.LoadNetlist();
    std::set<std::pair<KIID_PATH, ::KIID>> identities;
    for( unsigned i = 0; i < result->m_netlist->GetCount(); ++i )
    {
        const auto* component = result->m_netlist->GetComponent( i );
        if( component->GetKIIDs().empty() ) throw std::invalid_argument( "Netlist component has no symbol identity" );
        for( const ::KIID& id : component->GetKIIDs() )
            if( id == niluuid || !identities.emplace( component->GetPath(), id ).second )
                throw std::invalid_argument( "Netlist contains a missing or repeated symbol-instance identity" );
    }
    return result;
}
