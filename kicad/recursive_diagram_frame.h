/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_RECURSIVE_DIAGRAM_FRAME_H
#define KICAD_RECURSIVE_DIAGRAM_FRAME_H

#include "recursive_diagram_canvas.h"
#include <api/common/commands/recursive_diagram_commands.pb.h>
#include <api/api_handler.h>
#include <wx/frame.h>
#include <wx/geometry.h>
#include <wx/process.h>
#include <wx/timer.h>
#include <wx/weakref.h>
#include <array>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

class wxButton;
class wxToggleButton;
class wxDC;
class wxPanel;
class wxStaticText;
class wxTextCtrl;
class wxToolBar;
class wxScrolledWindow;
class wxChoice;
class wxRadioButton;
class wxSplitterWindow;
class wxSizer;
class wxSizerItem;
class wxStaticLine;
class DIALOG_DIAGRAM_FIELD_HISTORY;
class PANEL_DIAGRAM_HISTORY;
class wxSimplebook;

/** One native diagram level with one level draft (contract rbg-v2 section 9.1): the level itself,
 * its children and connections, and the blocks, connections and ports drawn into it are edited
 * together and saved or declined together. XML validation and publication remain in the compiled
 * companion. No action invokes an agent. */
class RECURSIVE_DIAGRAM_FRAME : public wxFrame
{
public:
    RECURSIVE_DIAGRAM_FRAME( wxWindow* aParent,
            const kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor& aRequest );
    ~RECURSIVE_DIAGRAM_FRAME() override;
    const std::string& DocumentId() const { return m_request.document_id(); }
    const std::string& SourcePath() const { return m_request.source_path(); }
    bool IsClosing() const { return m_closing; }
    kiapi::automation::diagrams::v1::RecursiveDiagramEditorState State() const;
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramObservation> Observe(
        const kiapi::automation::diagrams::v1::ObserveRecursiveDiagramEditor& aRequest );

private:
    using SELECTION = kiapi::automation::diagrams::v1::BlockSelectionData;
    using REVISION = kiapi::automation::diagrams::v1::BlockRevisionData;
    using DRAFT = kiapi::automation::diagrams::v1::BlockDraftData;
    using LINK_DRAFT = kiapi::automation::diagrams::v1::ConnectionDraftData;
    using LEVEL = kiapi::automation::diagrams::v1::LevelDraftData;
    using REQUEST = kiapi::automation::diagrams::v1::RecursiveFileRequest;
    using TOOL = RECURSIVE_DIAGRAM::TOOL;

    const REVISION* revision( const SELECTION& aSelection ) const;
    const kiapi::automation::diagrams::v1::RequirementRevisionData* requirements( const REVISION& aRevision ) const;
    /// The saved revision of the viewed level (or its history or implementation preview).
    const REVISION* current() const;
    int version( const REVISION& aRevision ) const;
    bool findPath( const std::string& aBlockId, std::vector<SELECTION>& aPath ) const;
    void load( const std::string& aExpectedToken = {} );
    void execute( REQUEST aRequest );
    void drain();
    void completed( wxProcessEvent& aEvent );
    void levelResult( const kiapi::automation::diagrams::v1::RecursiveFileResult& aResult );
    void rebaseResult( const kiapi::automation::diagrams::v1::RecursiveFileResult& aResult );
    void refresh();
    /// The status bar line: an error, a running request, a notice, the active tool's hint, why Save is unavailable
    /// for a read-only file, or unsaved changes. Every path that changes the draft shows it the same way.
    void showStatus();

    // The level draft.
    const kiapi::automation::diagrams::v1::ConnectionRevisionData* savedConnection( const std::string& aConnectionId ) const;
    DRAFT draftFor( const REVISION& aRevision ) const;
    LINK_DRAFT connectionDraftFor( const kiapi::automation::diagrams::v1::ConnectionRevisionData& aConnection ) const;
    void resetLevel();
    bool levelChanged() const;
    bool hasChanges() const;
    void pushUndo();
    void changed();
    /// The requirement draft of the selected block (the level itself or a child), created on first edit.
    DRAFT* editBlock( bool aCreate );
    const DRAFT* selectedBlockDraft() const;
    kiapi::automation::diagrams::v1::NewBlockOccurrenceData* newChild( const std::string& aBlockId );
    const kiapi::automation::diagrams::v1::NewBlockOccurrenceData* newChild( const std::string& aBlockId ) const;
    kiapi::automation::diagrams::v1::NewConnectionData* newConnection( const std::string& aConnectionId );
    const kiapi::automation::diagrams::v1::NewConnectionData* newConnection( const std::string& aConnectionId ) const;
    LINK_DRAFT* editConnection( bool aCreate );
    const LINK_DRAFT* connectionDraft( const std::string& aConnectionId ) const;
    kiapi::automation::diagrams::v1::RequirementFieldsData selectedFields() const;
    kiapi::automation::diagrams::v1::RequirementFieldsData savedFields() const;
    bool selectionIsNew() const;
    std::string selectedName() const;
    kiapi::automation::diagrams::v1::DiagramPresentationViewData* presentation();
    RECURSIVE_DIAGRAM::LEVEL_LAYOUT layout( const REVISION* aScope, bool aWithDraft ) const;
    void materialize();
    void materializeFrame();
    void encloseInFrame( const RECURSIVE_DIAGRAM::RECT& aRect );
    /// Keeps each unlocked channel route level with its ends' current heights, and its offset from the middle
    /// when its ends moved since aBefore.
    void followRoutes( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aBefore );

    // Selection and navigation.
    void select( const std::string& aBlockId );
    void selectConnection( const std::string& aConnectionId );
    void selectPort( const std::string& aOwnerId, const std::string& aInterfaceId );
    void navigate( std::string aBlockId, bool aRemember = true );
    void chooseImplementation();
    void previewImplementation( const std::string& aStateId );
    void manageImplementation( kiapi::automation::diagrams::v1::ImplementationActionKind aAction, std::string aStateId );
    void reloadSaved();
    void updateImplementationLabel();
    bool confirmChange();

    // Inspector.
    void edit();
    void editComment();
    void fillComments();
    void revealField( int aField );
    /// Add requirement…: a requirement box that is not shown yet (owner decision n98a3f3c41084f0ed hides
    /// empty requirement boxes until someone adds one).
    void chooseRequirement();
    /// + Add detail: for a block, a component-choice facet that has no value yet (Round A4, owner decision
    /// n0b2a908b00e78823); for a connection, a connection detail it does not have yet (Round A3, owner decision
    /// nf53af9d74841b7d3, which pairs "+ Add detail" with "+ Add requirement" so blocks and connections grow the same way).
    void chooseDetail();
    void save();
    void decline();
    void history( int aField );
    void showHistory( const kiapi::automation::diagrams::v1::RecursiveFileResult& aResult,
                      const REQUEST& aQuery );
    void openDiagramHistory();
    void loadDiagramHistory( unsigned aOffset );
    void inspectDiagramHistory( SELECTION aSelection );
    void previewDiagramHistory( SELECTION aSelection );
    void restoreDiagramHistory( SELECTION aSelection );
    void prepareDiagramRestoration();
    void returnFromHistoryPreview();
    void closeDiagramHistory();
    void undo( bool aRedo );
    void close( wxCloseEvent& aEvent );

    // Component choices (Round A4, owner decision n0b2a908b00e78823): chips on blocks, the inspector's
    // facet overview and one facet's detail. Edits change the level draft only.
    const kiapi::automation::diagrams::v1::BlockDefinitionData* selectedDefinition() const;
    /// The selected block's definition in the saved revision the draft started from; nullptr for a new block.
    const kiapi::automation::diagrams::v1::BlockDefinitionData* savedDefinition() const;
    /// Stores one facet of the selected block in the level draft; nullptr clears it (unspecified).
    void storeFacet( int aFacet, const kiapi::automation::diagrams::v1::DefinitionTextChoiceData* aChoice );
    void fillFacets( bool aAvailable );
    void fillFacetForm();
    void showFacetFields();
    void openFacet( int aFacet, bool aFocusState );
    void closeFacet( bool aFocusRow );
    void facetStateChanged();
    void facetEdited();
    void clearFacet();
    void reviewFacets( const std::string& aBlockId );
    /// Whether the canvas draws, reports and answers a block's Review facets link: not while the whole-diagram
    /// history is open.
    bool facetLinkOffered() const;
    bool facetFromForm( kiapi::automation::diagrams::v1::DefinitionTextChoiceData& aChoice, wxString& aProblem ) const;
    bool facetHasFocus() const;
    /// The detail's controls, in tab order.
    std::vector<wxWindow*> facetControls() const;
    int facetState() const;
    int facetStrength() const;
    void setFacetState( int aState );
    void setFacetStrength( int aStrength );
    /// Shows the strength choices' full labels when they fit the inspector's width in one row, and their short
    /// labels otherwise, so the row collapses its labels before it wraps; and gives the state and strength rows the
    /// width they wrap within. Returns whether a label or that width changed, so the caller lays out the inspector again.
    bool fitFacetLabels();
    /// The width the strength choices take in one row with their full labels, in pixels.
    int fullStrengthRow() const;
    /// The inspector's default width: 400 DIP, or wider where a facet's detail needs it to show the strength choices' full
    /// labels beside the inspector's scroll bar (design QA round 2, R2-P2-7).
    int inspectorWidth() const;
    wxFont chipFont() const;
    wxColour linkColour() const;
    /// The chips of each drawn block of the viewed level, in canvas pixels.
    std::vector<std::pair<std::string, RECURSIVE_DIAGRAM::BLOCK_CHIPS>> drawnChips() const;

    // Connection details (Round A3, owner decision nf53af9d74841b7d3): a connection shows its title, an editable caption,
    // + Add detail, + Add requirement and Comments, and only the details it has. Add detail adds exactly one detail as its
    // own removable row (signals, direction, domain or type, the kinds the format-2 model stores); removing the row returns
    // the connection to its earlier state. Edits change the level draft only, with undo and redo.
    enum class DETAIL { SIGNALS, DIRECTION, DOMAIN, TYPE };
    static constexpr int DETAILS = 4;
    struct SIGNAL { std::string id, name; bool drawn = false; kiapi::automation::diagrams::v1::DiagramConnectionKind kind = {}; };
    struct LINK_DETAILS
    {
        kiapi::automation::diagrams::v1::DiagramConnectionKind kind = kiapi::automation::diagrams::v1::DCK_ABSTRACT;
        kiapi::automation::diagrams::v1::DiagramDomain domain = kiapi::automation::diagrams::v1::DD_UNSPECIFIED;
        kiapi::automation::diagrams::v1::DiagramConnectionDirection direction = kiapi::automation::diagrams::v1::DCDR_UNSPECIFIED;
        std::vector<kiapi::automation::diagrams::v1::DiagramEndpointBindingData> endpoints;
        std::vector<SIGNAL> signals;
    };
    /// The selected connection's details as the level draft holds them; false when no connection is selected.
    bool linkDetails( LINK_DETAILS& aOut ) const;
    static bool HasDetail( const LINK_DETAILS& aDetails, DETAIL aDetail );
    bool detailShown( const LINK_DETAILS& aDetails, DETAIL aDetail ) const;
    /// Changes the selected connection's kind, domain or direction in the level draft, as one undo step.
    void setLinkValue( DETAIL aDetail, int aValue );
    void revealDetail( DETAIL aDetail );
    void removeDetail( DETAIL aDetail );
    void addSignal();
    void removeSignals( const std::vector<std::string>& aIds );
    /// Returns each end that says more than its block or port (a pin, candidates, a selector or intent) to that block or port,
    /// on the connection and on the signals drawn for it in this draft.
    void removeEndpointDetails();
    void captionEdited();
    void fillConnection( bool aAvailable );
    wxString endpointName( const kiapi::automation::diagrams::v1::DiagramEndpointBindingData& aEndpoint ) const;
    /// Whether an endpoint says more than the block or port it is drawn on (a pin, candidates, a selector or intent).
    static bool EndpointDefined( const kiapi::automation::diagrams::v1::DiagramEndpointBindingData& aEndpoint );
    /// The end as drawn: the block or port it is on, and nothing stated beyond that.
    static kiapi::automation::diagrams::v1::DiagramEndpointBindingData PlainEndpoint(
            const kiapi::automation::diagrams::v1::DiagramEndpointBindingData& aEndpoint );
    /// The arrowheads each drawn connection shows for its direction, in canvas pixels: tip and the point it comes from.
    struct ARROW { std::string connection; unsigned endpoint = 0; wxPoint tip, from; };
    std::vector<ARROW> drawnArrows() const;

    // Drawing tools (Round A1: toolbar strip and canvas-edge palette).
    void setTool( TOOL aTool );
    bool drawingAvailable() const;
    bool canDelete() const;
    void removeSelection( bool aDetach = false );
    void beginCaption( int aKind, const wxRect& aBox, const wxString& aValue );
    void finishCaption( bool aCommit );
    void commitNewBlock( const std::string& aCaption );
    void commitNewConnection( const std::string& aCaption );
    void commitNewPort( const std::string& aCaption );
    void renameNew( const std::string& aCaption );

    // Canvas.
    void paint( wxDC& aDC );
    void click( wxMouseEvent& aEvent );
    void motion( wxMouseEvent& aEvent );
    /// Applies the drag in progress with the pointer at aPoint (canvas pixels).
    void dragTo( const wxPoint& aPoint );
    void release();
    bool canvasKey( wxKeyEvent& aEvent );
    wxRect noteRect( const kiapi::automation::diagrams::v1::DiagramAnnotationData& aNote, int aIndex ) const;
    std::vector<RECURSIVE_DIAGRAM::RECT> noteBoxes( const google::protobuf::RepeatedPtrField<
            kiapi::automation::diagrams::v1::DiagramAnnotationData>& aNotes ) const;
    const google::protobuf::RepeatedPtrField<kiapi::automation::diagrams::v1::DiagramAnnotationData>& visibleNotes() const;
    void fit();
    /// The canvas width the edge palette covers; the diagram fits beside it.
    int paletteReserve() const;
    /// Whether the whole level, with its margin and frame port names, is in view beside the palette.
    bool drawingFits() const;
    /// Pixels the names of ports on the level frame need beyond the frame on each side.
    struct LABEL_ROOM { int left = 0, top = 0, right = 0, bottom = 0; };
    LABEL_ROOM labelRoom( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// Where the placed ports of aBlock name themselves inside its edge, in canvas pixels (sets aDC's font to the chip font).
    std::vector<wxRect> portNames( wxDC& aDC, const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const std::string& aBlock ) const;
    /// Each boundary port's name and where it is drawn beside the level frame, in canvas pixels.
    std::vector<std::pair<std::string, wxRect>> boundaryNames( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// A port's square as drawn at aAt: 12 DIP at every zoom (design QA P2-6).
    wxRect portMark( const wxPoint& aAt ) const;
    /// Every port square as drawn on the canvas (an unplaced child port once per place a connection attaches to it).
    struct PORT_MARK { std::string owner, id; wxString name; wxRect rect; };
    std::vector<PORT_MARK> portMarks( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// Each connection caption and where it is drawn, in canvas pixels: text as drawn and full, the whole caption (they differ
    /// only for a caption shortened with "…"). Every caption is drawn (design QA round 2, R2-P2-1): whole in the nearest place
    /// beside its connection, or in the free space around it, that is clear of blocks, handles, ports and their names, wires,
    /// notes, the palette and the other captions; shortened only when no clear place holds the whole text; and, where not even
    /// a shortened text has one, shortened to four characters and "…" in the place that covers least (a wire before a port, a
    /// name or another caption, and those before a block). clear is false only in that last case.
    struct CAPTION_PLACE { std::string connection; wxString text, full; wxRect rect; bool shown = false, clear = true; };
    std::vector<CAPTION_PLACE> connectionCaptions( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const wxSize& aArea ) const;
    /// The Connect tool's preview of the connection in progress, in canvas pixels, from its first end to the pointer or to the
    /// port or block the pointer is on: the path the committed connection's router would draw, or, where that path would run
    /// through a block or across a port's name, the way around (design QA round 2, R2-P2-2); empty when none is in progress.
    /// The finished connection does not go around: it keeps rule F4's three-segment path until that contract changes.
    std::vector<wxPoint> connectPreview( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// What the canvas shows on hover at aPoint: the whole caption of a shortened connection caption, or the choices behind a
    /// "+N more" chip; empty elsewhere.
    wxString canvasTipAt( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const wxPoint& aPoint ) const;
    /// Sets the canvas's tooltip to what lies under the pointer now (canvasTipAt), or none while the pointer is off the canvas,
    /// a drag is under way or no level is shown. Called on every pointer motion, when the pointer leaves, and after every
    /// repaint, so the tooltip follows a canvas that changes under a pointer that stays still (review of design QA round 2).
    void updateCanvasTip();
    /// Where the Connect hint ("Click a port to finish connection") is drawn beside the end of aPreview, in canvas pixels: the
    /// first of the four places around the pointer that covers no block, port name or part of the preview (design QA round 2,
    /// P3 1); nothing while no connection is in progress or its caption is being typed.
    std::optional<wxRect> connectHint( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const std::vector<wxPoint>& aPreview ) const;
    /// The selected block's eight resize handles as drawn, in canvas pixels; empty when none are drawn.
    std::vector<wxRect> selectionHandles( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// What the Connect tool highlights under the pointer as a valid place to start or finish (design QA P2-6): a port's
    /// ring or a block's outline, in canvas pixels, with its name.
    struct TARGET { wxRect rect; wxString name; bool port = false; std::string owner, id; };
    std::optional<TARGET> connectTarget( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout ) const;
    /// Keeps Save styled as the primary action while it is available, with Decline beside it as the secondary one
    /// (design QA P2-8).
    void enableSave( bool aSave, bool aDecline );
    wxPoint toScreen( const RECURSIVE_DIAGRAM::POINT& aPoint ) const;
    wxRect toScreen( const RECURSIVE_DIAGRAM::RECT& aRect ) const;
    RECURSIVE_DIAGRAM::POINT toDiagram( const wxPoint& aPoint ) const;
    int handleAt( const wxPoint& aPoint ) const;
    const RECURSIVE_DIAGRAM::PORT* portAt( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const wxPoint& aPoint ) const;
    std::string blockAt( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const wxPoint& aPoint ) const;
    std::string connectionAt( const RECURSIVE_DIAGRAM::LEVEL_LAYOUT& aLayout, const wxPoint& aPoint ) const;
    void placePaletteAndEditor();

    kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor m_request;
    kiapi::automation::diagrams::v1::RecursiveEditorDocument m_document;
    REQUEST m_activeRequest;
    DIALOG_DIAGRAM_FIELD_HISTORY* m_historyDialog = nullptr;
    wxEvtHandler m_historyEvents;
    kiapi::automation::diagrams::v1::FieldHistoryPageData m_historyContext;
    LEVEL m_level, m_savedLevel;
    std::vector<LEVEL> m_undo, m_redo;
    std::vector<SELECTION> m_path;
    std::optional<SELECTION> m_preview;
    bool m_diagramHistoryOpen = false;
    std::optional<SELECTION> m_historyPreview, m_pendingHistoryRestore;
    REQUEST m_failedHistoryRequest;
    std::string m_pendingImplementation;
    std::vector<std::string> m_back;
    std::string m_selected, m_connectionId, m_portOwner, m_portId;
    std::string m_pendingScope, m_pendingSelected, m_errorCode, m_error, m_notice;
    std::optional<std::string> m_pendingConnection;
    bool m_rememberNavigation = true, m_closeAfterSave = false;
    bool m_ready = false, m_dirty = false, m_rendered = false, m_updating = false, m_closing = false;
    uint64_t m_viewRevision = 0, m_saveCount = 0;
    uint64_t m_navigationInputRevision = 0;
    /// Presses the canvas received, reported so rendered input can tell a press that changed nothing from one not yet delivered.
    uint64_t m_canvasPresses = 0;
    /// Pointer motions and entries the canvas handled, reported so a journey can tell a hover that has not arrived yet.
    uint64_t m_canvasMotions = 0;
    unsigned m_rebaseAttempts = 0;
    bool m_rebasing = false;
    LEVEL m_sentLevel, m_removalBefore;
    google::protobuf::RepeatedPtrField<kiapi::automation::diagrams::v1::LevelEditEffectData> m_lastEffects;
    /// A plain window, not a panel: a panel would pass keyboard focus on to the palette it contains.
    wxWindow* m_canvas;
    RECURSIVE_DIAGRAM::TOOL_PALETTE* m_palette;
    std::vector<std::pair<TOOL, RECURSIVE_DIAGRAM::TOOL_BUTTON*>> m_strip;
    RECURSIVE_DIAGRAM::TOOL_ACTION* m_stripDelete;
    bool m_paletteShown = true;
    wxTextCtrl* m_caption;
    int m_captionKind = 0;
    RECURSIVE_DIAGRAM::POINT m_pendingPoint;
    std::string m_pendingOwner;
    std::optional<kiapi::automation::diagrams::v1::DiagramEndpointBindingData> m_connectFrom, m_connectTo;
    wxPoint m_pointer;
    /// The canvas's tooltip as set now (see canvasTipAt), and whether a repaint has asked for it to be found again.
    wxString m_canvasTip;
    bool m_canvasTipQueued = false;
    /// The connection captions last placed, and what they were placed for, so a repaint or a state read of an unchanged
    /// canvas does not search for their places again.
    mutable std::string m_captionKey;
    mutable std::vector<CAPTION_PLACE> m_captionCache;
    /// Whether the pointer is over the canvas (the Connect tool highlights what is under it only then).
    bool m_pointerInside = false;
    wxStaticText* m_breadcrumb;
    wxButton* m_implementation;
    wxButton* m_diagramHistory;
    PANEL_DIAGRAM_HISTORY* m_diagramHistoryPanel;
    wxSimplebook* m_inspectorBook;
    wxStaticText* m_owner;
    wxStaticText* m_savedVersion;
    wxStaticText* m_endpointHeading;
    /// What the selected connection's ends say beyond their block or port, as read-only lines.
    wxStaticText* m_endpoints;
    wxScrolledWindow* m_inspectorScroll;
    wxSplitterWindow* m_splitter;
    wxTextCtrl* m_comments;
    wxStaticText* m_commentTargetStatus;
    wxChoice* m_commentChoice;
    wxButton* m_addRequirement;
    wxButton* m_addDetail;
    wxStaticLine* m_addSeparator;
    // Round A3: the connection's caption and detail rows.
    wxStaticText* m_captionLabel;
    wxTextCtrl* m_connectionCaption;
    wxStaticText* m_captionNotice;
    std::array<wxSizer*, DETAILS> m_detailRows;
    std::array<wxButton*, DETAILS> m_detailRemove;
    wxButton* m_endpointRemove;
    struct SIGNAL_LINE { wxStaticText* name; wxButton* remove; wxSizer* row; };
    std::vector<SIGNAL_LINE> m_signalLines;
    wxSizer* m_signalList;
    wxTextCtrl* m_signalEntry;
    wxStaticText* m_signalNotice;
    std::array<wxToggleButton*, 3> m_directionChoices;
    std::array<wxToggleButton*, 5> m_domainChoices;
    std::array<wxToggleButton*, 4> m_typeChoices;
    /// The direction, domain and type rows; they wrap within the width fitFacetLabels gives them.
    std::array<RECURSIVE_DIAGRAM::CHOICE_FLOW*, 3> m_linkChoiceRows{};
    /// Why the caption or the new signal cannot be kept, as shown beside it.
    wxString m_captionProblem, m_signalProblem;
    /// Detail rows a person added on a connection that has no value for them yet (like m_revealed for requirements).
    std::map<std::string, std::array<bool, DETAILS>> m_revealedDetails;
    wxStaticText* m_facetHeading;
    std::array<RECURSIVE_DIAGRAM::FACET_ROW*, RECURSIVE_DIAGRAM::FACETS> m_facetRows;
    wxSizer* m_facetDetail;
    /// The gap between the facet overview and the section after it (design QA P2-12).
    wxSizerItem* m_facetGap;
    RECURSIVE_DIAGRAM::LINK_BUTTON* m_facetBack;
    wxStaticText* m_facetTitle;
    /// One-click choices (Chosen, Candidate, Unknown) and (Information, Preference, Requirement).
    std::array<wxRadioButton*, 3> m_facetStates;
    /// The rows holding the state and strength choices; they wrap within the width fitFacetLabels gives them.
    RECURSIVE_DIAGRAM::CHOICE_FLOW* m_facetStateRow;
    RECURSIVE_DIAGRAM::CHOICE_FLOW* m_facetStrengthRow;
    wxStaticText* m_facetValueLabel;
    wxTextCtrl* m_facetValue;
    wxTextCtrl* m_facetCandidates;
    wxTextCtrl* m_facetReason;
    std::array<wxRadioButton*, 3> m_facetStrengths;
    RECURSIVE_DIAGRAM::LINK_BUTTON* m_facetClear;
    wxStaticText* m_facetNotice;
    /// The facet whose detail is open (-1 for none) and the block it belongs to.
    int m_facet = -1;
    std::string m_facetOwner;
    /// The detail holds an entry that is not in the draft because it cannot be kept yet, and why.
    bool m_facetTouched = false;
    wxString m_facetProblem;
    std::vector<std::string> m_commentIds;
    std::string m_commentId;
    bool m_newComment = false;
    TOOL m_tool = TOOL::SELECT;
    enum class DRAG { NONE, NOTE, MOVE, RESIZE, PORT, CONNECT };
    DRAG m_drag = DRAG::NONE;
    LEVEL m_dragBefore;
    wxPoint m_dragStart;
    RECURSIVE_DIAGRAM::RECT m_dragRect;
    int m_dragHandle = -1;
    bool m_dragMoved = false;
    // How many distinct level geometries drags reached in this window (reported as drag_positions), and the latest one.
    uint64_t m_dragPositions = 0;
    std::string m_dragGeometry;
    double m_noteStartX = 0, m_noteStartY = 0;
    /// Requirement fields the user asked to add on an element whose fields are still empty.
    std::map<std::string, std::array<bool, 3>> m_revealed;
    wxButton* m_openDiagram;
    wxButton* m_save;
    /// How Save is styled: 1 as the primary action, 0 unavailable, -1 not yet styled.
    int m_saveStyle = -1;
    wxButton* m_decline;
    std::array<wxTextCtrl*, 3> m_fields;
    std::array<wxButton*, 3> m_history;
    std::array<wxSizer*, 3> m_fieldHeadings;
    wxToolBar* m_toolbar;
    double m_scale = 1.0;
    /// The view is the one fit() chose, not a restored per-level or history view.
    bool m_fitted = false;
    wxPoint2DDouble m_origin{ 0, 0 };
    struct VIEW { double scale; wxPoint2DDouble origin; std::string selected; };
    std::optional<VIEW> m_historyView;
    std::map<std::string, VIEW> m_views;
    std::unique_ptr<wxProcess> m_process;
    wxTimer m_ioTimer;
    long m_pid = 0;
    std::string m_stdout, m_stderr;
};

/** Serves the per-level diagram editor's native API commands for one project manager.
 * It opens, reads and observes the diagram windows it created, nothing else. */
class RECURSIVE_DIAGRAM_CONTROL : public API_HANDLER
{
public:
    explicit RECURSIVE_DIAGRAM_CONTROL( wxWindow* aParent );
    ~RECURSIVE_DIAGRAM_CONTROL() override;

    /// Ask every open diagram window to close; false when one of them stays open.
    bool CloseEditors();

private:
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramEditorState> open(
        const HANDLER_CONTEXT<kiapi::automation::diagrams::v1::OpenRecursiveDiagramEditor>& aCtx );
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramEditorState> read(
        const HANDLER_CONTEXT<kiapi::automation::diagrams::v1::ReadRecursiveDiagramEditor>& aCtx );
    HANDLER_RESULT<kiapi::automation::diagrams::v1::RecursiveDiagramObservation> observe(
        const HANDLER_CONTEXT<kiapi::automation::diagrams::v1::ObserveRecursiveDiagramEditor>& aCtx );
    RECURSIVE_DIAGRAM_FRAME* openEditor( const std::string& aDocumentId ) const;

    wxWindow* m_parent;
    std::vector<wxWeakRef<RECURSIVE_DIAGRAM_FRAME>> m_editors;
};

#endif
