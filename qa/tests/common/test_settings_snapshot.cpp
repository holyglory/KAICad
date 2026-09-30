/* KiCad native read-only settings snapshots. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <settings/json_settings.h>
#include <settings/json_settings_internals.h>
#include <settings/nested_settings.h>
#include <settings/parameters.h>
#include <project/project_file.h>
#include <settings/bom_settings.h>

namespace
{
class SNAPSHOT_ROOT : public JSON_SETTINGS
{
public:
    SNAPSHOT_ROOT() : JSON_SETTINGS( "snapshot-fixture", SETTINGS_LOC::NONE, 0, false, false, false )
    {
        m_params.emplace_back( new PARAM<int>( "value", &value, 1 ) );
        m_params.emplace_back( new PARAM<int>( "design.shared", &shared, 5 ) );
        m_params.emplace_back( new PARAM<BOM_PRESET>( "bom", &bom, {} ) );
        m_params.emplace_back( new PARAM_LAMBDA<int>( "derived", [this]()
        {
            if( fail ) throw std::runtime_error( "fixture getter failure" );
            return value * 2;
        }, []( int ) {}, 2 ) );
    }
    bool Dirty() const { return m_modified; }
    int value = 1;
    int shared = 5;
    bool fail = false;
    BOM_PRESET bom;
};

class SNAPSHOT_CHILD : public NESTED_SETTINGS
{
public:
    SNAPSHOT_CHILD( JSON_SETTINGS* parent, const std::string& path ) :
            NESTED_SETTINGS( "snapshot-child", 0, parent, path, false )
    {
        m_params.emplace_back( new PARAM<int>( "value", &value, 3 ) );
    }
    bool Dirty() const { return m_modified; }
    int value = 3;
};

class DELTA_ROOT : public JSON_SETTINGS
{
public:
    DELTA_ROOT() : JSON_SETTINGS( "delta-fixture", SETTINGS_LOC::NONE, 0, false, false, false )
    {
        m_params.emplace_back( new PARAM<int>( "first", &first, 1 ) );
        m_params.emplace_back( new PARAM<int>( "unrelated", &unrelated, 9 ) );
        m_params.emplace_back( new PARAM_LAMBDA<int>( "last", [this]() { return last; },
            [this]( int value )
            {
                ++setterCalls;
                last = value;
                if( value == reject ) throw std::runtime_error( "fixture setter failure after writing" );
                if( value == coerce ) last = 0;
            }, 2 ) );
    }
    int first = 1, last = 2, unrelated = 9;
    int reject = -1, coerce = -1, setterCalls = 0;
};
}

BOOST_AUTO_TEST_SUITE( SettingsSnapshot )

BOOST_AUTO_TEST_CASE( DeltaPreservesUnrelatedOwnersAndStoresAndSupportsUndoRedo )
{
    SNAPSHOT_ROOT root; SNAPSHOT_CHILD child( &root, "design" );
    SNAPSHOT_CHILD grandchild( &child, "formatting" );
    child.Set<int>( "shared", 99 ); // Stale child-store copy must not override the parent owner.
    root.Set<int>( "unknown.extension", 19 );
    const auto before = root.CaptureCurrentState();
    root.value = 7; root.shared = 11; grandchild.value = 13;
    const auto after = root.CaptureCurrentState();
    root.value = 1; root.shared = 5; grandchild.value = 3;
    child.value = 17; // Unrelated edit made after the draft was captured.
    const auto live = root.CaptureCurrentState();
    const auto store = static_cast<const nlohmann::json&>( *root.Internals() );
    const auto nestedStore = static_cast<const nlohmann::json&>( *child.Internals() );
    auto expected = after; expected["design"]["value"] = 17;
    root.ApplyCurrentStateDelta( before, after );
    BOOST_CHECK( root.CaptureCurrentState() == expected );
    root.ApplyCurrentStateDelta( after, before );
    BOOST_CHECK( root.CaptureCurrentState() == live );
    root.ApplyCurrentStateDelta( before, after );
    BOOST_CHECK( root.CaptureCurrentState() == expected );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *root.Internals() ) == store );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *child.Internals() ) == nestedStore );
    BOOST_CHECK( !root.Dirty() && !child.Dirty() && !grandchild.Dirty() );
}

BOOST_AUTO_TEST_CASE( DeltaRejectsUnownedOrStaleChangesBeforeAnySetter )
{
    DELTA_ROOT root;
    const auto before = root.CaptureCurrentState();
    auto after = before; after["first"] = 3; after["last"] = 4;
    root.first = 5;
    const auto live = root.CaptureCurrentState();
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, after ), std::runtime_error );
    BOOST_CHECK( root.CaptureCurrentState() == live );
    BOOST_CHECK_EQUAL( root.setterCalls, 0 );
    root.first = 1;
    auto unowned = after; unowned["unknown"] = 8;
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, unowned ), std::runtime_error );
    auto missing = after; missing.erase( "last" );
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, missing ), std::runtime_error );
    auto schema = after; schema["meta"]["version"] = 100;
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, schema ), std::runtime_error );
    BOOST_CHECK( root.CaptureCurrentState() == before );
    BOOST_CHECK_EQUAL( root.setterCalls, 0 );
    root.ApplyCurrentStateDelta( before, before );
    BOOST_CHECK_EQUAL( root.setterCalls, 0 );
}

BOOST_AUTO_TEST_CASE( DeltaRestoresEarlierAndThrowingSettersAndRejectsSilentCoercion )
{
    DELTA_ROOT root; SNAPSHOT_CHILD child( &root, "nested" );
    const auto before = root.CaptureCurrentState();
    const auto store = static_cast<const nlohmann::json&>( *root.Internals() );
    auto after = before; after["first"] = 3; after["last"] = 4; after["nested"]["value"] = 7;
    root.reject = 4;
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, after ), std::runtime_error );
    BOOST_CHECK( root.CaptureCurrentState() == before );
    BOOST_CHECK_EQUAL( root.setterCalls, 2 );
    root.reject = -1; root.coerce = 4;
    BOOST_CHECK_THROW( root.ApplyCurrentStateDelta( before, after ), std::runtime_error );
    BOOST_CHECK( root.CaptureCurrentState() == before );
    root.coerce = -1;
    root.ApplyCurrentStateDelta( before, after );
    BOOST_CHECK( root.CaptureCurrentState() == after );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *root.Internals() ) == store );
}

BOOST_AUTO_TEST_CASE( DetachedCopyPreservesSourceStoresAndOwnsNestedValues )
{
    SNAPSHOT_ROOT source; SNAPSHOT_CHILD child( &source, "design" );
    SNAPSHOT_CHILD grandchild( &child, "formatting" );
    source.value = 23; child.value = 45; grandchild.value = 67;
    source.Set<int>( "unknown.extension", 19 );
    const auto stored = static_cast<const nlohmann::json&>( *source.Internals() );
    const auto nestedStored = static_cast<const nlohmann::json&>( *child.Internals() );
    const auto before = source.CaptureCurrentState();
    SNAPSHOT_ROOT target; SNAPSHOT_CHILD targetChild( &target, "design" );
    SNAPSHOT_CHILD targetGrandchild( &targetChild, "formatting" );
    source.CopyCurrentStateTo( target );
    BOOST_CHECK( target.CaptureCurrentState() == before );
    target.value = 101; targetChild.value = 103; targetGrandchild.value = 107;
    BOOST_CHECK( source.CaptureCurrentState() == before );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *source.Internals() ) == stored );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *child.Internals() ) == nestedStored );
    source.fail = true;
    BOOST_CHECK_THROW( source.CopyCurrentStateTo( target ), std::runtime_error );
}

BOOST_AUTO_TEST_CASE( CapturesLiveNestedValuesWithoutWritingAnyStoreOrDirtyFlag )
{
    SNAPSHOT_ROOT root;
    root.Set<int>( "unknown.extension", 19 );
    SNAPSHOT_CHILD child( &root, "design" );
    SNAPSHOT_CHILD grandchild( &child, "formatting" );
    const nlohmann::json rootStore = static_cast<const nlohmann::json&>( *root.Internals() );
    const nlohmann::json childStore = static_cast<const nlohmann::json&>( *child.Internals() );
    const nlohmann::json grandchildStore = static_cast<const nlohmann::json&>( *grandchild.Internals() );
    const auto first = root.CaptureCurrentState();
    BOOST_CHECK( first == root.CaptureCurrentState() );
    BOOST_CHECK_EQUAL( first.at( "unknown" ).at( "extension" ).get<int>(), 19 );
    BOOST_CHECK_EQUAL( first.at( "design" ).at( "formatting" ).at( "value" ).get<int>(), 3 );
    root.value = 7; child.value = 11; grandchild.value = 13;
    const auto changed = root.CaptureCurrentState();
    BOOST_CHECK_EQUAL( changed.at( "value" ).get<int>(), 7 );
    BOOST_CHECK_EQUAL( changed.at( "derived" ).get<int>(), 14 );
    BOOST_CHECK_EQUAL( changed.at( "design" ).at( "value" ).get<int>(), 11 );
    BOOST_CHECK_EQUAL( changed.at( "design" ).at( "formatting" ).at( "value" ).get<int>(), 13 );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *root.Internals() ) == rootStore );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *child.Internals() ) == childStore );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *grandchild.Internals() ) == grandchildStore );
    BOOST_CHECK( !root.Dirty() && !child.Dirty() && !grandchild.Dirty() );
    BOOST_CHECK( !root.IsFileSynced() && !child.IsFileSynced() && !grandchild.IsFileSynced() );
    root.value = 1; child.value = 3; grandchild.value = 3;
    BOOST_CHECK( first == root.CaptureCurrentState() );
}

BOOST_AUTO_TEST_CASE( SwallowedGetterFailureCannotProduceAnApparentlySuccessfulSnapshot )
{
    SNAPSHOT_ROOT root;
    const auto before = root.CaptureCurrentState();
    const nlohmann::json stored = static_cast<const nlohmann::json&>( *root.Internals() );
    root.fail = true;
    BOOST_CHECK_THROW( root.CaptureCurrentState(), std::runtime_error );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *root.Internals() ) == stored );
    BOOST_CHECK( !root.Dirty() );
    root.fail = false;
    BOOST_CHECK( root.CaptureCurrentState() == before );
}

BOOST_AUTO_TEST_CASE( ParentOwnedLiveParameterOverridesStaleNestedCopy )
{
    SNAPSHOT_ROOT root;
    SNAPSHOT_CHILD child( &root, "design" );
    child.Set<int>( "shared", 99 );
    auto before = root.CaptureCurrentState();
    BOOST_CHECK_EQUAL( before.at( "design" ).at( "shared" ).get<int>(), 5 );
    root.shared = 7;
    auto after = root.CaptureCurrentState();
    BOOST_CHECK_EQUAL( after.at( "design" ).at( "shared" ).get<int>(), 7 );
    BOOST_CHECK( before != after );
    BOOST_CHECK_EQUAL( child.Get<int>( "shared" ).value(), 99 );
    BOOST_CHECK( !root.Dirty() && !child.Dirty() );
}

BOOST_AUTO_TEST_CASE( RealProjectSettingsSnapshotIsStableAndIncludesLiveTextVariables )
{
    PROJECT_FILE project( "settings-snapshot-fixture.kicad_pro" );
    project.Load();
    const nlohmann::json stored = static_cast<const nlohmann::json&>( *project.Internals() );
    const auto before = project.CaptureCurrentState();
    BOOST_CHECK( before == project.CaptureCurrentState() );
    project.m_TextVars["CPU_POSITION"] = "Near the heatsink";
    const auto after = project.CaptureCurrentState();
    BOOST_CHECK_EQUAL( after.at( "text_variables" ).at( "CPU_POSITION" ).get<std::string>(),
                       "Near the heatsink" );
    BOOST_CHECK( before != after );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *project.Internals() ) == stored );
    project.m_TextVars.clear();
    BOOST_CHECK( before == project.CaptureCurrentState() );
}

BOOST_AUTO_TEST_CASE( SnapshotComparesPersistedValuesNotTransientPresetFlags )
{
    SNAPSHOT_ROOT root;
    root.bom = BOM_PRESET::DefaultEditing();
    const auto before = root.CaptureCurrentState();
    BOOST_CHECK( root.bom.readOnly );
    root.bom.readOnly = false; // Not persisted by KiCad's BOM serializer.
    BOOST_CHECK( before == root.CaptureCurrentState() );
    root.bom.sortAsc = !root.bom.sortAsc;
    BOOST_CHECK( before != root.CaptureCurrentState() );
}

BOOST_AUTO_TEST_CASE( StrictSnapshotDoesNotChangeLegacyBestEffortStore )
{
    JSON_SETTINGS scratch( "store-fixture", SETTINGS_LOC::NONE, 0, false, false, false );
    scratch.Set<int>( "value", 7 );
    PARAM_LAMBDA<int> parameter( "value", []() -> int
    {
        throw std::runtime_error( "fixture getter failure" );
    }, []( int ) {}, 0 );
    BOOST_CHECK_NO_THROW( parameter.Store( &scratch ) );
    BOOST_CHECK_EQUAL( scratch.Get<int>( "value" ).value(), 7 );
    BOOST_CHECK_THROW( parameter.StoreStrict( &scratch ), std::runtime_error );
    BOOST_CHECK_EQUAL( scratch.Get<int>( "value" ).value(), 7 );
}

BOOST_AUTO_TEST_SUITE_END()
