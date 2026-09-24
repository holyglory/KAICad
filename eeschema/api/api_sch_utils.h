/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2024 Jon Evans <jon@craftyjon.com>
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef KICAD_API_SCH_UTILS_H
#define KICAD_API_SCH_UTILS_H

#include <memory>
#include <tl/expected.hpp>
#include <core/typeinfo.h>
#include <math/box2.h>
#include <api/common/envelope.pb.h>
#include <api/schematic/schematic_types.pb.h>
#include <pin_map.h>

#include <optional>

class EDA_ITEM;
class SCH_FIELD;
class SCH_RENDER_SETTINGS;
class SCH_SYMBOL;
class SCH_SHEET;
class SCH_SHEET_PATH;
class SCHEMATIC;

namespace kiapi::automation::v1
{
class SchematicPresentationFacts;
class SchematicSymbolPinGeometry;
}

/// Sheet-space body of a placed symbol at an explicit sheet instance: the drawn body and its
/// visible pins for the unit and body style selected there, without fields.
BOX2I MeasureSchematicSymbolBody( const SCH_SYMBOL& aSymbol, const SCH_SHEET_PATH& aPath );

/// Extent of the glyphs SCH_PAINTER::draw( SCH_FIELD ) paints for @a aField at an explicit sheet
/// instance, from the exact native glyph geometry: the text KiCad shows there, centred on the
/// field's bounding box (offset like the painter for a global label), at the field's draw
/// rotation, with the renderer's effective stroke width, font and metrics. Empty when KiCad
/// paints nothing (hidden, private or empty field).
std::optional<BOX2I> MeasureSchematicFieldGlyphs( const SCH_FIELD& aField, const SCH_SHEET_PATH& aPath,
                                                 const wxString& aVariant,
                                                 const SCH_RENDER_SETTINGS& aSettings );

/// Presentation facts of one loaded sheet instance, measured on private copies at @a aPath rather
/// than the displayed sheet: page bounds, every object with its presentation role, per-instance
/// field text, painted field glyphs and reading directions, which references must show, wires
/// with their native net identity at this instance, and junctions. The caller sets the document
/// and revision. Never changes the design, its caches or the human view.
void PackSchematicPresentationFacts( const SCH_SHEET_PATH& aPath, const SCH_RENDER_SETTINGS& aSettings,
                                     const wxString& aVariant,
                                     kiapi::automation::v1::SchematicPresentationFacts& aOutput );

/// Sheet-space bounds a placement measurement reports for a symbol at an explicit sheet
/// instance: its body and visible pins for the selected unit and body style (every visible
/// pin with the target KiCad draws on an unconnected pin end) plus its visible fields.
/// A symbol whose library definition cannot be resolved is measured the way KiCad draws
/// and bounds it, as its placeholder body with no pins; its pin geometry is reported as
/// incomplete by #PackSchematicPinGeometry instead of refusing the whole sheet.
BOX2I MeasureSchematicSymbolBounds( const SCH_SYMBOL& aSymbol, const SCH_SHEET_PATH& aPath,
                                    const wxString& aVariant );

/// Observe exact active pin identities and sheet-space anchors. Incomplete
/// mappings return no pins; previous output is cleared before every observation.
void PackSchematicPinGeometry( const SCH_SYMBOL& aSymbol, const SCH_SHEET_PATH& aPath,
                              const wxString& aVariant,
                              kiapi::automation::v1::SchematicSymbolPinGeometry& aOutput );

std::unique_ptr<EDA_ITEM> CreateItemForType( KICAD_T aType, EDA_ITEM* aContainer );

bool PackSymbol( kiapi::schematic::types::SchematicSymbolInstance* aOutput, const SCH_SYMBOL* aInput,
                 const SCH_SHEET_PATH& aPath );

/**
 * Unpack the geometry, the library definition, fields, and the default-variant attributes that
 * are shared between every placement. Single-placement data is handled by #ApplySymbolInstance.
 */
bool UnpackSymbol( SCH_SYMBOL* aOutput, const kiapi::schematic::types::SchematicSymbolInstance& aInput );

/**
 * Apply placement-specific data to an @a aSymbol at @a aPath: reference, unit, and
 * the per-placement attribute and field differentials.
 *
 * Variant names are registered with @a aSchematic so that the UI offers them for selection.
 * @a aSymbol must already be in the schematic; the other placements of the symbol are untouched.
 */
void ApplySymbolInstance( SCH_SYMBOL* aSymbol,
                          const kiapi::schematic::types::SchematicSymbolInstance& aInput,
                          const SCH_SHEET_PATH& aPath, SCHEMATIC* aSchematic );

/// Pack/unpack a pin-to-pad map instance override to/from its protobuf form (issue #2282).
void PackPinMapOverride( kiapi::schematic::types::PinMapInstanceOverride* aOutput,
                         const PIN_MAP_INSTANCE_OVERRIDE&                 aOverride );

PIN_MAP_INSTANCE_OVERRIDE UnpackPinMapOverride( const kiapi::schematic::types::PinMapInstanceOverride& aInput );

bool PackSheet( kiapi::schematic::types::SheetSymbol* aOutput, const SCH_SHEET* aInput,
                const SCH_SHEET_PATH& aPath );

/**
 * Unpack the every placement data from the input. Placement data is applied separately by #ApplySheetInstance.
 */
tl::expected<bool, kiapi::common::ApiResponseStatus> UnpackSheet( SCH_SHEET* aOutput, const kiapi::schematic::types::SheetSymbol& aInput );

/**
 * Apply the placement data in a sheet message to @a aSheet: page number and the variants the
 * message carries.
 *
 * @a aParentPath is the path of the sheet that contains @a aSheet, which is how a sheet's
 * placement records are keyed.
 */
void ApplySheetInstance( SCH_SHEET* aSheet, const kiapi::schematic::types::SheetSymbol& aInput,
                         const SCH_SHEET_PATH& aParentPath, SCHEMATIC* aSchematic );

#endif //KICAD_API_SCH_UTILS_H
