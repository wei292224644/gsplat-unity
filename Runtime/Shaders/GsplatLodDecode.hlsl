// Copyright (c) 2026 wwj
// SPDX-License-Identifier: MIT

// Decoders for the 32-byte ExtSplat node layout of .gsd files. Ported from Spark's
// splatDefines.glsl (World Labs Technologies, MIT). The encoder is Tools~/gsd-build/src/encode.rs;
// Tests/Editor/GsdDecodeTests pins the two together. No dependencies, so a compute shader can
// include it on its own.
#ifndef GSPLAT_LOD_DECODE_INCLUDED
#define GSPLAT_LOD_DECODE_INCLUDED

#define GSPLAT_LOD_PI 3.14159265358979323846

// Returns a real quaternion (x, y, z, w), w = cos(θ/2).
float4 DecodeQuatOctXy1010R12(uint encoded)
{
    float2 f = float2(float(encoded & 0x3FFu), float((encoded >> 10u) & 0x3FFu)) / 1023.0 * 2.0 - 1.0;
    float3 axis = float3(f.xy, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-axis.z, 0.0);
    axis.x += (axis.x >= 0.0) ? -t : t;
    axis.y += (axis.y >= 0.0) ? -t : t;
    axis = normalize(axis);
    float theta = (float(encoded >> 20u) / 4095.0) * GSPLAT_LOD_PI;
    float s, c;
    sincos(theta * 0.5, s, c);
    return float4(axis * s, c);
}

struct LodSplat
{
    float3 center;
    float alpha; // opacity in 0..1, or the merged-node D in (1, 5]
    float3 rgb;
    float3 scale;
    float4 quat; // real quaternion (x, y, z, w)
};

LodSplat UnpackLodSplat(uint4 w0, uint4 w1)
{
    LodSplat s;
    s.center = asfloat(w0.xyz);
    s.alpha = f16tof32(w0.w & 0xFFFFu);
    s.rgb = float3(f16tof32(w1.x & 0xFFFFu), f16tof32(w1.x >> 16u), f16tof32(w1.y & 0xFFFFu));
    s.scale = exp(float3(f16tof32(w1.y >> 16u), f16tof32(w1.z & 0xFFFFu), f16tof32(w1.z >> 16u)));
    s.quat = DecodeQuatOctXy1010R12(w1.w);
    return s;
}

// The shared splat path (QuatToMat3 / CalcCovariance in Gsplat.hlsl) takes the real part first:
// the PLY rot_0..3 order the importers store. Spark's codec yields (x, y, z, w).
float4 LodQuatToShaderOrder(float4 q)
{
    return q.wxyz;
}

// A merged node's opacity profile reaches further than a plain splat's (Spark:
// maxStdDev + 0.7·(D − 1)). Returns how much to widen the √8σ quad for a node.
float LodExtentScale(float alphaOrD)
{
    return alphaOrD > 1.0 ? (sqrt(8.0) + 0.7 * (alphaOrD - 1.0)) / sqrt(8.0) : 1.0;
}

#endif
