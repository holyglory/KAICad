/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2022 Mark Roszko <mark.roszko@gmail.com>
 * Copyright (C) 2024 Alex Shvartzkop <dudesuchamazing@gmail.com>
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

#ifndef JOB_PCB_RENDER_H
#define JOB_PCB_RENDER_H

#include <kicommon.h>
#include "job.h"
#include <optional>
#include <array>
#include <string>
#include <vector>
#include <math/vector3.h>

// Defined in wingdi.h
#ifdef TRANSPARENT
#undef TRANSPARENT
#endif

#ifdef OPAQUE
#undef OPAQUE
#endif

class BOARD;

class KICOMMON_API JOB_PCB_RENDER : public JOB
{
public:
    struct MODEL_METADATA
    {
        std::string nativeUuid;
        unsigned int modelIndex = 0;
        std::string requestedIdentity;
        std::string resolvedIdentity;
        std::string contentSha256;
        bool loaded = false;
        bool missing = false;
        std::string missingReason;
        std::array<double, 16> sceneTransform{};
        std::array<double, 6> nativeGeometryBounds{};
        bool nativeGeometryBoundsAvailable = false;
        bool secondaryAssetsComplete = true;
        std::string secondaryAssetLimitation;
    };

    struct OBJECT_METADATA
    {
        std::string nativeUuid;
        unsigned int modelIndex = 0;
        std::array<double, 16> sceneTransform{};
        std::array<double, 6> nativeGeometryBounds{};
        bool nativeGeometryBoundsAvailable = false;
        std::array<double, 4> projectedConservativeBounds{};
        bool projectedBoundsAvailable = false;
        bool exactVisiblePixels = false;
        bool occlusionTested = false;
    };

    struct INSPECTION_METADATA
    {
        unsigned int widthPixels = 0;
        unsigned int heightPixels = 0;
        std::array<double, 16> viewMatrix{};
        std::array<double, 16> projectionMatrix{};
        std::array<double, 16> boardNanometresToScene{};
        std::string projectionConvention;
        double nearClip = 0.0;
        double farClip = 0.0;
        std::string observedSide;
        bool observedPerspective = false;
        double observedZoom = 0.0;
        std::array<double, 3> observedCameraPosition{};
        std::array<double, 3> observedLookAt{};
        std::string sourceBoardSnapshotSha256;
        std::string effectiveRenderInputSha256;
        std::vector<OBJECT_METADATA> objects;
        std::vector<MODEL_METADATA> models;
        std::vector<std::string> coverageNotes;
        std::vector<std::string> limitations;
        bool matricesFinite = false;
        bool matricesInvertible = false;
    };

    enum class FORMAT
    {
        PNG,
        JPEG
    };

    static std::map<JOB_PCB_RENDER::FORMAT, wxString>& GetFormatNameMap();

public:
    JOB_PCB_RENDER();
    wxString GetDefaultDescription() const override;
    wxString GetSettingsDialogTitle() const override;

    wxString m_filename;
    wxString m_variant;

    // Do not rename enum values as they are used for CLI args
    enum class QUALITY
    {
        BASIC,
        HIGH,
        USER,
        JOB_SETTINGS
    };

    enum class BG_STYLE
    {
        DEFAULT,
        TRANSPARENT,
        OPAQUE
    };

    enum class SIDE
    {
        TOP,
        BOTTOM,
        LEFT,
        RIGHT,
        FRONT,
        BACK
    };

    FORMAT      m_format = FORMAT::PNG;
    QUALITY     m_quality = QUALITY::BASIC;
    BG_STYLE    m_bgStyle = BG_STYLE::DEFAULT;
    int         m_width = 0;
    int         m_height = 0;
    std::string m_appearancePreset;
    bool        m_useBoardStackupColors = true;
    SIDE        m_side = SIDE::TOP;
    double      m_zoom = 1.0;
    bool        m_perspective = false;
    VECTOR3D    m_rotation;
    VECTOR3D    m_pan;
    VECTOR3D    m_pivot;
    bool        m_proceduralTextures = false;
    bool        m_floor = false;
    bool        m_antiAlias = true;
    bool        m_postProcess = false;
    VECTOR3D    m_lightTopIntensity;
    VECTOR3D    m_lightBottomIntensity;
    VECTOR3D    m_lightCameraIntensity;
    VECTOR3D    m_lightSideIntensity = VECTOR3D( 0.5, 0.5, 0.5 );
    int         m_lightSideElevation = 60;

    // Set only by the dedicated native inspection handler. It points at an
    // owned PCB_DRC_DOCUMENT_SNAPSHOT held by that handler for the synchronous
    // job; ordinary jobs keep this null and retain their existing source path.
    BOARD* m_boardSnapshot = nullptr;
    std::string m_sourceBoardSnapshotSha256;
    std::string m_effectiveRenderInputSha256;
    INSPECTION_METADATA m_inspectionMetadata;
    bool m_hasInspectionMetadata = false;
};

#endif
