/* Schematic-wide page-settings undo. GPL-3.0-or-later. */
#ifndef SCH_PAGE_SETTINGS_UNDO_H
#define SCH_PAGE_SETTINGS_UNDO_H

#include <drawing_sheet/ds_proxy_undo_item.h>
#include <sch_edit_frame.h>
#include <sch_screen.h>
#include <schematic.h>
#include <richio.h>
#include <bus_alias.h>
#include <kiway.h>
#include <schematic_text_var_adapter.h>
#include <text_var_dependency.h>
#include <map>
#include <optional>
#include <connection_graph.h>
#include <sch_painter.h>
#include <api/api_sch_formatting.h>
#include <api/api_sch_annotation.h>
#include <api/api_sch_symbol_project_settings.h>
#include <api/api_sch_bom_settings.h>
#include <api/api_sch_net_settings.h>
#include <api/api_sch_erc_settings.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <json_common.h>
#include <set>
#include <stdexcept>
#include <vector>

/// ERC markers are computed diagnostics.  Run ERC and the ERC dialog delete them outside any
/// commit, so a history entry must never keep a marker pointer: it keeps the saved exclusions
/// by ERC_EXCLUSION sort key, and the markers a change removed or rewrote as detached records.
namespace SCH_ERC_HISTORY
{
/// One marker as data: restoring rebuilds it from its exact marker references.
struct RECORD
{
    KIID        uuid;
    KIID        screen;
    std::string marker;     ///< Serialized kiapi::schematic::ErcMarker, the exclusion sort key.
    bool        excluded = false;
    wxString    comment;
    wxString    message;    ///< The violation's own message; empty for the rule's default text.
};

struct STATE
{
    std::map<std::string, wxString> exclusions;   ///< Saved exclusions: sort key to comment.
    std::vector<RECORD>             removed;      ///< Markers to put back when this state returns.
    std::set<KIID>                  created;      ///< Markers to delete when this state returns.
};

/// The saved exclusions of every loaded screen, exactly as saving records them.
std::map<std::string, wxString> CaptureExclusions( SCHEMATIC& aSchematic );

RECORD Record( const SCH_MARKER& aMarker, const SCH_SCREEN& aScreen );

/**
 * Return the live markers to @a aState: delete the markers the undone change created, put back
 * the ones it removed, then exclude exactly the saved exclusions by sort key, creating a marker
 * for each exclusion no marker carries (as SCHEMATIC::ResolveERCExclusionsPostUpdate does).
 * What this deletes and creates is added to @a aOpposite, so the opposite history entry can
 * reverse it.  Returns true when a marker changed.
 */
bool Restore( SCH_EDIT_FRAME* aFrame, const STATE& aState, STATE* aOpposite );
}

// A page dialog can export settings to several screens. Keep the native
// worksheet snapshot plus each screen's exact identity and page/title state;
// repeated sheet instances share one screen and therefore one snapshot.
class SCH_PAGE_SETTINGS_UNDO_ITEM : public DS_PROXY_UNDO_ITEM
{
public:
    explicit SCH_PAGE_SETTINGS_UNDO_ITEM( SCH_EDIT_FRAME* aFrame ) : DS_PROXY_UNDO_ITEM( aFrame )
    {
        m_textVariables = aFrame->Prj().GetTextVars();
        m_variantDescriptions = aFrame->Schematic().Settings().m_VariantDescriptions;
        m_variantNames = aFrame->Schematic().GetVariantNames();
        m_drawingRatios = aFrame->Schematic().Settings().DrawingRatios();
        m_formatting = SCH_FORMATTING::Capture( aFrame->Schematic().Settings() );
        m_annotation = SCH_ANNOTATION::Capture( aFrame->Schematic().Settings() );
        m_fieldTemplates = SCH_FIELD_TEMPLATES::Capture( aFrame->Prj().GetProjectFile().m_TemplateFieldNames );
        m_symbolComparison = SCH_SYMBOL_COMPARISON::Capture( aFrame->Schematic().Settings().m_SymbolParity );
        m_bomSettings = aFrame->Schematic().Settings();
        if( auto settings = aFrame->Prj().GetProjectFile().NetSettings() )
            m_netSettings = SCH_NET_SETTINGS::Capture( *settings );
        if( auto tracker = aFrame->Schematic().Settings().m_refDesTracker )
        {
            m_referenceInventory = std::make_unique<REFDES_TRACKER>();
            m_referenceInventory->CopyAllocatedFrom( *tracker );
        }
        m_ercPolicy = SCH_ERC_SETTINGS::Capture( aFrame->Schematic() );
        m_ercSaved = m_ercPolicy.SerializeAsString();
        m_ercPolicy.clear_exclusions(); // restored by sort key through m_ercMarkers
        m_ercMarkers.exclusions = SCH_ERC_HISTORY::CaptureExclusions( aFrame->Schematic() );
        m_currentVariant = aFrame->Schematic().GetCurrentVariant();
        m_netChains = aFrame->Schematic().ConnectionGraph()->GetNetChainDefinitions();
        if( auto settings = aFrame->Prj().GetProjectFile().NetSettings() )
        {
            m_netChainClasses = settings->GetNetChainClasses();
            m_netChainClassDefinitions = settings->GetNetChainClassDefinitions();
        }
        for( const auto& alias : aFrame->Schematic().GetAllBusAliases() )
            m_busAliases.push_back( alias->Clone() );
        for( const SCH_SHEET_PATH& path : aFrame->Schematic().Hierarchy() )
        {
            SCH_SCREEN* screen = path.LastScreen();
            if( screen )
                m_screens.emplace( screen->GetUuid(), STATE{ screen->GetPageSettings(), screen->GetTitleBlock(),
                                                           screen->IsContentModified() } );
            SCH_SHEET* sheet = path.Last();
            if( sheet )
                m_roots.emplace( sheet->m_Uuid, sheet->HasRootInstance()
                        ? std::optional<SCH_SHEET_INSTANCE>( sheet->GetRootInstance() ) : std::nullopt );
        }
    }

    /// @a aOpposite is the history entry that reverses this restoration; it receives the ERC
    /// markers the restoration deletes or creates.
    void RestoreAll( SCH_EDIT_FRAME* aFrame, bool aRestoreDirtyState = false,
                     SCH_PAGE_SETTINGS_UNDO_ITEM* aOpposite = nullptr )
    {
        if( m_restoreSetup )
        {
            const auto operating = SCH_FORMATTING::Capture( aFrame->Schematic().Settings() )
                                           .operating_point().SerializeAsString();
            aFrame->Prj().GetProjectFile().ApplyCurrentStateDelta( *m_setupAfter, *m_setupBefore );
            RefreshSetup( aFrame, operating != SCH_FORMATTING::Capture( aFrame->Schematic().Settings() )
                                                       .operating_point().SerializeAsString() );
        }
        DS_PROXY_UNDO_ITEM::Restore( aFrame );
        if( !BusAliasesMatch( aFrame->Schematic() ) )
            aFrame->Schematic().SetBusAliases( m_busAliases );
        if( !TextVariablesMatch( aFrame ) )
            ApplyTextVariables( aFrame, m_textVariables );
        if( m_restoreVariantRegistry )
        {
            aFrame->Schematic().RestoreVariantRegistry( m_variantNames, m_variantDescriptions );
            aFrame->UpdateVariantSelectionCtrl( aFrame->Schematic().GetVariantNamesForUI() );
            aFrame->SetCurrentVariant( m_currentVariant );
        }
        else if( m_restoreVariantDescriptions )
            ApplyVariantDescriptions( aFrame, m_variantDescriptions );
        if( m_restoreNetChains )
        {
            aFrame->Schematic().ConnectionGraph()->SetNetChainDefinitions( m_netChains );
            if( auto settings = aFrame->Prj().GetProjectFile().NetSettings(); settings && m_netChainClasses )
            {
                settings->ClearNetChainClasses();
                for( const auto& [name, value] : *m_netChainClasses )
                    settings->SetNetChainClass( name, value );
                settings->SetNetChainClassDefinitions( m_netChainClassDefinitions );
            }
        }
        if( m_restoreDrawingRatios )
            ApplyDrawingRatios( aFrame, m_drawingRatios );
        if( m_restoreFormatting )
            ApplyFormatting( aFrame, m_formatting );
        if( m_restoreAnnotation )
            SCH_ANNOTATION::Restore( aFrame->Schematic().Settings(), m_annotation );
        if( m_restoreFieldTemplates )
            SCH_FIELD_TEMPLATES::Restore( aFrame->Prj().GetProjectFile().m_TemplateFieldNames, m_fieldTemplates );
        if( m_restoreSymbolComparison )
            SCH_SYMBOL_COMPARISON::Restore( aFrame->Schematic().Settings().m_SymbolParity, m_symbolComparison );
        if( m_restoreBomSettings )
        {
            // Undo retains transient native flags as well as persisted values.
            // Prepare the full copy before touching live project settings.
            auto prepared = m_bomSettings;
            SCH_BOM_SETTINGS::Swap( aFrame->Schematic().Settings(), prepared );
        }
        if( m_restoreNetSettings && m_netSettings )
        {
            auto prepared = SCH_NET_SETTINGS::PrepareDeclared( *m_netSettings );
            auto live = aFrame->Prj().GetProjectFile().NetSettings();
            if( !live ) throw std::runtime_error( "Project net settings are unavailable during history restoration" );
            SCH_NET_SETTINGS::ApplyPrepared( *live, *prepared );
        }
        if( m_restoreReferenceInventory )
        {
            auto& tracker = aFrame->Schematic().Settings().m_refDesTracker;
            if( !tracker ) tracker = std::make_shared<REFDES_TRACKER>();
            if( m_referenceInventory ) tracker->CopyAllocatedFrom( *m_referenceInventory );
            else tracker->Clear();
        }
        if( m_restoreErcPolicy || m_restoreErcMarkers )
            RestoreErc( aFrame, aOpposite );
        for( const SCH_SHEET_PATH& path : aFrame->Schematic().Hierarchy() )
        {
            SCH_SCREEN* screen = path.LastScreen();
            auto saved = screen ? m_screens.find( screen->GetUuid() ) : m_screens.end();
            if( saved == m_screens.end() )
                continue;

            const STATE& state = saved->second;
            SCH_SHEET* sheet = path.Last();
            auto root = sheet ? m_roots.find( sheet->m_Uuid ) : m_roots.end();
            if( root != m_roots.end()
                    && ( sheet->HasRootInstance() != root->second.has_value()
                         || ( root->second && sheet->HasRootInstance()
                              && sheet->GetRootInstance().m_PageNumber != root->second->m_PageNumber ) ) )
            {
                sheet->RemoveInstance( KIID_PATH{} );
                if( root->second )
                    sheet->AddInstance( *root->second );
                screen->SetContentModified();
            }
            if( Serialize( screen->GetPageSettings(), screen->GetTitleBlock() )
                    != Serialize( state.page, state.title ) )
            {
                screen->SetPageSettings( state.page );
                screen->SetTitleBlock( state.title );
                screen->SetContentModified();
            }
            if( aRestoreDirtyState )
                screen->SetContentModified( state.modified );
        }
    }

    bool BusAliasesMatch( const SCHEMATIC& aSchematic ) const
    {
        if( !m_restoreBusAliases )
            return true;
        const auto& aliases = aSchematic.GetAllBusAliases();
        return aliases.size() == m_busAliases.size() && std::equal( aliases.begin(), aliases.end(), m_busAliases.begin(),
                []( const auto& a, const auto& b ) { return a->GetName() == b->GetName() && a->Members() == b->Members(); } );
    }

    void IncludeBusAliases() { m_restoreBusAliases = true; }
    void IncludeTextVariables() { m_restoreTextVariables = true; }
    void IncludeVariantDescriptions() { m_restoreVariantDescriptions = true; }
    void IncludeVariantRegistry() { m_restoreVariantRegistry = true; }
    void IncludeNetChains() { m_restoreNetChains = true; }
    void IncludeDrawingRatios() { m_restoreDrawingRatios = true; }
    void IncludeFormatting() { m_restoreFormatting = true; }
    void IncludeAnnotation() { m_restoreAnnotation = true; }
    void IncludeFieldTemplates() { m_restoreFieldTemplates = true; }
    void IncludeSymbolComparison() { m_restoreSymbolComparison = true; }
    void IncludeBomSettings() { m_restoreBomSettings = true; }
    void IncludeNetSettings() { m_restoreNetSettings = true; }
    void IncludeReferenceInventory() { m_restoreReferenceInventory = true; }
    void IncludeErcPolicy() { m_restoreErcPolicy = true; }
    void IncludeErcMarkers() { m_restoreErcMarkers = true; }
    /// A page size, drawing sheet, title block or root page number was changed by this entry.
    /// The screens' page, title and root-page states are always restored; this records that
    /// the entry is more than an ERC edit.
    void IncludePages() { m_restorePages = true; }
    bool IncludesErcMarkers() const { return m_restoreErcMarkers; }
    SCH_ERC_HISTORY::STATE& ErcMarkers() { return m_ercMarkers; }

    /// True when the saved ERC settings (rules, pin conflicts and exclusions) differ from the
    /// ones captured with this entry.
    bool ErcChanged( SCHEMATIC& aSchematic ) const
    {
        return SCH_ERC_SETTINGS::Capture( aSchematic ).SerializeAsString() != m_ercSaved;
    }

    /// True when this entry restores nothing but the ERC settings and markers.  A change that also
    /// staged a page, title block, drawing sheet or root page (IncludePages) is not ERC-only: its
    /// rollback and undo must restore those too.
    bool IncludesOnlyErc() const
    {
        return ( m_restoreErcPolicy || m_restoreErcMarkers ) && !m_restorePages && !m_restoreBusAliases
               && !m_restoreTextVariables
               && !m_restoreVariantDescriptions && !m_restoreVariantRegistry && !m_restoreNetChains
               && !m_restoreDrawingRatios && !m_restoreFormatting && !m_restoreAnnotation
               && !m_restoreFieldTemplates && !m_restoreSymbolComparison && !m_restoreBomSettings
               && !m_restoreNetSettings && !m_restoreReferenceInventory && !m_restoreSetup;
    }

    /// Restore only the ERC rules, pin conflicts, exclusions and markers.  Refreshes an open
    /// ERC dialog (which lists marker pointers) when anything changed.
    bool RestoreErc( SCH_EDIT_FRAME* aFrame, SCH_PAGE_SETTINGS_UNDO_ITEM* aOpposite = nullptr )
    {
        bool changed = false;

        if( m_restoreErcPolicy )
        {
            auto live = SCH_ERC_SETTINGS::Capture( aFrame->Schematic() );
            live.clear_exclusions();

            if( live.SerializeAsString() != m_ercPolicy.SerializeAsString() )
            {
                SCH_ERC_SETTINGS::RestorePolicy( aFrame->Schematic().ErcSettings(), m_ercPolicy );
                changed = true;
            }
        }

        if( m_restoreErcMarkers )
            changed |= SCH_ERC_HISTORY::Restore( aFrame, m_ercMarkers, aOpposite ? &aOpposite->m_ercMarkers : nullptr );

        if( changed )
            aFrame->RefreshErcDialog();

        return changed;
    }

    static void ApplyFormatting( SCH_EDIT_FRAME* aFrame, const SCH_FORMATTING::MESSAGE& aValue )
    {
        const bool operatingChanged = SCH_FORMATTING::Capture( aFrame->Schematic().Settings() )
                                              .operating_point().SerializeAsString()
                                      != aValue.operating_point().SerializeAsString();
        SCH_FORMATTING::Restore( aFrame->Schematic().Settings(), aValue );
        aFrame->Schematic().RecomputeIntersheetRefs( false );
        aFrame->GetCanvas()->GetView()->SetLayerVisible( LAYER_INTERSHEET_REFS,
                                                        aValue.show_intersheet_references() );
        auto* render = aFrame->GetRenderSettings();
        const auto& settings = aFrame->Schematic().Settings();
        render->SetDefaultPenWidth( settings.m_DefaultLineWidth );
        render->m_SymbolLineWidth = settings.m_DefaultLineWidth;
        render->m_PinSymbolSize = settings.m_PinSymbolSize;
        render->m_ShowDNPMarkers = settings.m_ShowDNPMarkers;
        // Formatting changes affect the geometry of already placed objects.
        // Use the same complete cache/R-tree/view invalidation as drawing ratios.
        ApplyDrawingRatios( aFrame, aFrame->Schematic().Settings().DrawingRatios() );
        if( operatingChanged ) aFrame->RefreshOperatingPointDisplay();
    }
    static void ApplyDrawingRatios( SCH_EDIT_FRAME* aFrame, const std::array<double, 5>& aValues )
    {
        aFrame->Schematic().Settings().SetDrawingRatios( aValues );
        aFrame->GetRenderSettings()->SetDashLengthRatio( aValues[0] );
        aFrame->GetRenderSettings()->SetGapLengthRatio( aValues[1] );
        aFrame->GetRenderSettings()->m_TextOffsetRatio = aValues[2];
        aFrame->GetRenderSettings()->m_LabelSizeRatio = aValues[3];
        std::set<SCH_SCREEN*> seen;
        for( const SCH_SHEET_PATH& path : aFrame->Schematic().Hierarchy() )
        {
            SCH_SCREEN* screen = path.LastScreen();
            if( !screen || !seen.insert( screen ).second ) continue;
            std::vector<SCH_ITEM*> items;
            for( SCH_ITEM* item : screen->Items() ) items.push_back( item );
            for( SCH_ITEM* item : items )
            {
                item->ClearCaches();
                screen->Update( item );
            }
        }
        aFrame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::ALL );
        aFrame->GetCanvas()->GetView()->MarkDirty();
    }
    bool IncludesNetChains() const { return m_restoreNetChains; }
    bool IncludesNetSettings() const
    {
        return m_restoreNetSettings || ( m_restoreSetup && m_setupBefore && m_setupAfter
                && m_setupBefore->contains( "net_settings" ) && m_setupAfter->contains( "net_settings" )
                && m_setupBefore->at( "net_settings" ) != m_setupAfter->at( "net_settings" ) );
    }
    bool IncludesSetup() const { return m_restoreSetup; }

    void ApplySetupDelta( SCH_EDIT_FRAME* aFrame, const nlohmann::json& aBefore,
                          const nlohmann::json& aAfter )
    {
        // Allocate rollback records before invoking any live setter. Arm the
        // native undo only after the parameter layer has verified the result.
        if( m_restoreSetup )
            throw std::runtime_error( "A native commit already contains a Setup delta" );
        m_setupBefore = aBefore;
        m_setupAfter = aAfter;
        const auto operating = SCH_FORMATTING::Capture( aFrame->Schematic().Settings() )
                                       .operating_point().SerializeAsString();
        aFrame->Prj().GetProjectFile().ApplyCurrentStateDelta( aBefore, aAfter );
        m_restoreSetup = true;
        RefreshSetup( aFrame, operating != SCH_FORMATTING::Capture( aFrame->Schematic().Settings() )
                                                   .operating_point().SerializeAsString() );
    }

    static void RefreshNetSettings( SCH_EDIT_FRAME* aFrame )
    {
        aFrame->Schematic().ConnectionGraph()->ApplyNetChainNetclasses();
        aFrame->Prj().IncrementNetclassesTicker();
        std::set<SCH_SCREEN*> visited;
        for( const SCH_SHEET_PATH& path : aFrame->Schematic().Hierarchy() )
            if( SCH_SCREEN* screen = path.LastScreen(); screen && visited.insert( screen ).second )
                for( SCH_ITEM* item : screen->Items() ) item->ClearCaches();
        aFrame->Kiway().CommonSettingsChanged( NET_SETTINGS_CHANGED );
        aFrame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
        aFrame->GetCanvas()->Refresh();
    }

    static void RefreshSetup( SCH_EDIT_FRAME* aFrame, bool aOperatingPointChanged )
    {
        auto& schematic = aFrame->Schematic();
        const auto& desiredAliases = aFrame->Prj().GetProjectFile().m_BusAliases;
        std::map<wxString, std::vector<wxString>> actualAliases;
        for( const auto& alias : schematic.GetAllBusAliases() )
            actualAliases[alias->GetName()] = alias->Members();
        if( actualAliases != desiredAliases )
        {
            std::vector<std::shared_ptr<BUS_ALIAS>> aliases;
            for( const auto& [name, members] : desiredAliases )
            {
                auto alias = std::make_shared<BUS_ALIAS>();
                alias->SetName( name );
                alias->SetMembers( members );
                aliases.push_back( std::move( alias ) );
            }
            schematic.SetBusAliases( aliases );
        }
        ApplyFormatting( aFrame, SCH_FORMATTING::Capture( schematic.Settings() ) );
        // The generic parameter delta already installed the new values, so
        // ApplyFormatting cannot detect this difference from its live input.
        // Undo/Redo must update derived overlays as well as persisted settings.
        if( aOperatingPointChanged ) aFrame->RefreshOperatingPointDisplay();
        aFrame->Prj().IncrementTextVarsTicker();
        aFrame->Prj().IncrementNetclassesTicker();
        if( auto* adapter = schematic.GetTextVarAdapter() )
            adapter->Tracker().InvalidateProjectScoped();
        aFrame->Kiway().CommonSettingsChanged( TEXTVARS_CHANGED );
        aFrame->RefreshErcDialog();
    }

    void CopyProjectSettingsScope( const SCH_PAGE_SETTINGS_UNDO_ITEM& aOther )
    {
        m_restoreBusAliases = aOther.m_restoreBusAliases;
        m_restoreTextVariables = aOther.m_restoreTextVariables;
        m_restoreVariantDescriptions = aOther.m_restoreVariantDescriptions;
        m_restoreVariantRegistry = aOther.m_restoreVariantRegistry;
        m_restoreNetChains = aOther.m_restoreNetChains;
        m_restoreDrawingRatios = aOther.m_restoreDrawingRatios;
        m_restoreFormatting = aOther.m_restoreFormatting;
        m_restoreAnnotation = aOther.m_restoreAnnotation;
        m_restoreFieldTemplates = aOther.m_restoreFieldTemplates;
        m_restoreSymbolComparison = aOther.m_restoreSymbolComparison;
        m_restoreBomSettings = aOther.m_restoreBomSettings;
        m_restoreNetSettings = aOther.m_restoreNetSettings;
        m_restoreReferenceInventory = aOther.m_restoreReferenceInventory;
        m_restoreErcPolicy = aOther.m_restoreErcPolicy;
        m_restoreErcMarkers = aOther.m_restoreErcMarkers;
        m_restorePages = aOther.m_restorePages;
        m_restoreSetup = aOther.m_restoreSetup;
        if( m_restoreSetup )
        {
            // The opposite history entry restores precisely the same parameter
            // scope in reverse; unrelated project values do not become undo data.
            m_setupBefore = aOther.m_setupAfter;
            m_setupAfter = aOther.m_setupBefore;
        }
    }

    bool TextVariablesMatch( SCH_EDIT_FRAME* aFrame ) const
    {
        return !m_restoreTextVariables || m_textVariables == aFrame->Prj().GetTextVars();
    }

    static void ApplyTextVariables( SCH_EDIT_FRAME* aFrame, const std::map<wxString, wxString>& aVariables )
    {
        aFrame->Prj().GetTextVars() = aVariables;
        aFrame->Prj().IncrementTextVarsTicker();
        if( auto* adapter = aFrame->Schematic().GetTextVarAdapter() )
            adapter->Tracker().InvalidateProjectScoped();
        aFrame->Kiway().CommonSettingsChanged( TEXTVARS_CHANGED );
    }

    static void ApplyVariantDescriptions( SCH_EDIT_FRAME* aFrame, const std::map<wxString, wxString>& aDescriptions )
    {
        aFrame->Schematic().Settings().m_VariantDescriptions = aDescriptions;
        if( auto* adapter = aFrame->Schematic().GetTextVarAdapter() )
            adapter->Tracker().InvalidateVariantScoped();
    }

    static std::string Serialize( const PAGE_INFO& aPage, const TITLE_BLOCK& aTitle )
    {
        STRING_FORMATTER out;
        aPage.Format( &out );
        aTitle.Format( &out );
        return out.GetString();
    }

private:
    struct STATE { PAGE_INFO page; TITLE_BLOCK title; bool modified; };
    std::map<KIID, STATE> m_screens;
    std::vector<std::shared_ptr<BUS_ALIAS>> m_busAliases;
    std::map<wxString, wxString> m_textVariables;
    std::map<wxString, wxString> m_variantDescriptions;
    bool m_restoreVariantDescriptions = false;
    bool m_restoreVariantRegistry = false;
    std::set<wxString> m_variantNames;
    wxString m_currentVariant;
    bool m_restoreBusAliases = false;
    bool m_restoreTextVariables = false;
    bool m_restoreNetChains = false;
    bool m_restoreDrawingRatios = false;
    bool m_restoreFormatting = false;
    SCH_FORMATTING::MESSAGE m_formatting;
    bool m_restoreAnnotation = false;
    SCH_ANNOTATION::MESSAGE m_annotation;
    bool m_restoreFieldTemplates = false;
    bool m_restoreSymbolComparison = false;
    SCH_FIELD_TEMPLATES::MESSAGE m_fieldTemplates;
    SCH_SYMBOL_COMPARISON::MESSAGE m_symbolComparison;
    bool m_restoreBomSettings = false;
    FIELDS_TABLE_BOM_SETTINGS m_bomSettings;
    bool m_restoreNetSettings = false;
    std::optional<SCH_NET_SETTINGS::MESSAGE> m_netSettings;
    bool m_restoreReferenceInventory = false;
    std::unique_ptr<REFDES_TRACKER> m_referenceInventory;
    bool m_restoreErcPolicy = false;
    bool m_restoreErcMarkers = false;
    bool m_restorePages = false;
    bool m_restoreSetup = false;
    std::optional<nlohmann::json> m_setupBefore;
    std::optional<nlohmann::json> m_setupAfter;
    SCH_ERC_SETTINGS::MESSAGE m_ercPolicy;
    std::string m_ercSaved;                 ///< Rules, pin conflicts and exclusions as captured.
    SCH_ERC_HISTORY::STATE m_ercMarkers;
    std::array<double, 5> m_drawingRatios;
    std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION> m_netChains;
    std::optional<std::map<wxString, wxString>> m_netChainClasses;
    std::set<wxString> m_netChainClassDefinitions;
    std::map<KIID, std::optional<SCH_SHEET_INSTANCE>> m_roots;
};

#endif
