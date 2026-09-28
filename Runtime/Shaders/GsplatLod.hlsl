// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

#ifndef GSPLAT_LOD_INCLUDED
#define GSPLAT_LOD_INCLUDED

#include "Gsplat.hlsl"
#include "GsplatLodDecode.hlsl"
#include "GsplatSparkSH.hlsl"

// Two uint4 per node (ExtSplat). source.id is a node index chosen by the LoD selection.
StructuredBuffer<uint4> _LodNodesBuffer;

bool InitSplatData(SplatSource source, float4x4 modelView, out SplatCenter center, out SplatCorner corner,
                   out float4 color)
{
    LodSplat s = UnpackLodSplat(_LodNodesBuffer[source.id * 2], _LodNodesBuffer[source.id * 2 + 1]);
    color = float4(s.rgb, s.alpha);
    if (!InitCenter(modelView, s.center, center))
        return false;
    // Widening the scale widens the covariance, so InitCorner's frustum cull already sees the
    // larger quad of a merged node. The fragment maps uv back into the node's own σ (Gsplat.shader).
    SplatCovariance cov = CalcCovariance(LodQuatToShaderOrder(s.quat), s.scale * LodExtentScale(s.alpha));
    if (!InitCorner(source, cov, center, corner))
        return false;
    return true;
}

#endif
