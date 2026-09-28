// Copyright (c) 2026 Arthur Aillet, Yize Wu
// SPDX-License-Identifier: MIT

// Packed SH decode shared by the SPARK and LOD variants: both store SH in the same
// sint7 / sint8 / sint6 layout, so these bit operations exist exactly once.
#ifndef GSPLAT_SPARK_SH_INCLUDED
#define GSPLAT_SPARK_SH_INCLUDED

#ifndef SH_BANDS_0
StructuredBuffer<uint2> _PackedSH1Buffer;
#if defined(SH_BANDS_2) || defined(SH_BANDS_3) || defined(SH_BANDS_4)
StructuredBuffer<uint4> _PackedSH2Buffer;
#endif
#if defined(SH_BANDS_3) || defined(SH_BANDS_4)
StructuredBuffer<uint4> _PackedSH3Buffer;
#endif
#ifdef SH_BANDS_4
StructuredBuffer<uint4> _PackedSH4Buffer;
#endif

void InitSH(uint id, out float3 sh[SH_COEFFS])
{
    uint2 packedSH1 = _PackedSH1Buffer[id];
    #if defined(SH_BANDS_2) || defined(SH_BANDS_3) || defined(SH_BANDS_4)
    uint4 packedSH2 = _PackedSH2Buffer[id];
    #endif
    #if defined(SH_BANDS_3) || defined(SH_BANDS_4)
    uint4 packedSH3 = _PackedSH3Buffer[id];
    #endif
    #ifdef SH_BANDS_4
    uint4 packedSH4 = _PackedSH4Buffer[id];
    #endif

    // Extract sint7 values packed into 2 x uint32
    sh[0] = float3(
        int(packedSH1.x << 25u) >> 25,
        int(packedSH1.x << 18u) >> 25,
        int(packedSH1.x << 11u) >> 25
    ) / 63.0;
    sh[1] = float3(
        int(packedSH1.x << 4u) >> 25,
        int((packedSH1.x >> 3u) | (packedSH1.y << 29u)) >> 25,
        int(packedSH1.y << 22u) >> 25
    ) / 63.0;
    sh[2] = float3(
        int(packedSH1.y << 15u) >> 25,
        int(packedSH1.y << 8u) >> 25,
        int(packedSH1.y << 1u) >> 25
    ) / 63.0;
    #if defined(SH_BANDS_2) || defined(SH_BANDS_3) || defined(SH_BANDS_4)
    // Extract sint8 values packed into 4 x uint32
    sh[3] = float3(
        int(packedSH2.x << 24u) >> 24,
        int(packedSH2.x << 16u) >> 24,
        int(packedSH2.x << 8u) >> 24
    ) / 127.0;
    sh[4] = float3(
        int(packedSH2.x) >> 24u,
        int(packedSH2.y << 24u) >> 24,
        int(packedSH2.y << 16u) >> 24
    ) / 127.0;
    sh[5] = float3(
        int(packedSH2.y << 8u) >> 24,
        int(packedSH2.y) >> 24,
        int(packedSH2.z << 24u) >> 24
    ) / 127.0;
    sh[6] = float3(
        int(packedSH2.z << 16u) >> 24,
        int(packedSH2.z << 8u) >> 24,
        int(packedSH2.z) >> 24
    ) / 127.0;
    sh[7] = float3(
        int(packedSH2.w << 24u) >> 24,
        int(packedSH2.w << 16u) >> 24,
        int(packedSH2.w << 8u) >> 24
    ) / 127.0;
    #endif
    #if defined(SH_BANDS_3) || defined(SH_BANDS_4)
    // Extract sint6 values packed into 4 x uint32
    sh[8] = float3(
        int(packedSH3.x << 26u) >> 26,
        int(packedSH3.x << 20u) >> 26,
        int(packedSH3.x << 14u) >> 26
    ) / 31.0;
    sh[9] = float3(
        int(packedSH3.x << 8u) >> 26,
        int(packedSH3.x << 2u) >> 26,
        int((packedSH3.x >> 4u) | (packedSH3.y << 28)) >> 26
    ) / 31.0;
    sh[10] = float3(
        int(packedSH3.y << 22u) >> 26,
        int(packedSH3.y << 16u) >> 26,
        int(packedSH3.y << 10u) >> 26
    ) / 31.0;
    sh[11] = float3(
        int(packedSH3.y << 4u) >> 26,
        int((packedSH3.y >> 2u) | (packedSH3.z << 30u)) >> 26,
        int(packedSH3.z << 24u) >> 26
    ) / 31.0;
    sh[12] = float3(
        int(packedSH3.z << 18u) >> 26,
        int(packedSH3.z << 12u) >> 26,
        int(packedSH3.z << 6u) >> 26
    ) / 31.0;
    sh[13] = float3(
        int(packedSH3.z) >> 26,
        int(packedSH3.w << 26u) >> 26,
        int(packedSH3.w << 20u) >> 26
    ) / 31.0;
    sh[14] = float3(
        int(packedSH3.w << 14u) >> 26,
        int(packedSH3.w << 8u) >> 26,
        int(packedSH3.w << 2u) >> 26
    ) / 31.0;
    #endif
    #ifdef SH_BANDS_4
    // Extract sint4 values packed into 4 x uint32 (8 values per word; 5 unused tail slots).
    // value i (i in [0..26]) lives in word i/8 at bit offset (i%8)*4.
    // sh[15] = values 0,1,2 → word 0 bits 0,4,8
    sh[15] = float3(
        int(packedSH4.x << 28u) >> 28,
        int(packedSH4.x << 24u) >> 28,
        int(packedSH4.x << 20u) >> 28
    ) / 7.0;
    // sh[16] = values 3,4,5 → word 0 bits 12,16,20
    sh[16] = float3(
        int(packedSH4.x << 16u) >> 28,
        int(packedSH4.x << 12u) >> 28,
        int(packedSH4.x << 8u) >> 28
    ) / 7.0;
    // sh[17] = values 6,7,8 → word 0 bits 24,28 + word 1 bit 0
    sh[17] = float3(
        int(packedSH4.x << 4u) >> 28,
        int(packedSH4.x) >> 28,
        int(packedSH4.y << 28u) >> 28
    ) / 7.0;
    // sh[18] = values 9,10,11 → word 1 bits 4,8,12
    sh[18] = float3(
        int(packedSH4.y << 24u) >> 28,
        int(packedSH4.y << 20u) >> 28,
        int(packedSH4.y << 16u) >> 28
    ) / 7.0;
    // sh[19] = values 12,13,14 → word 1 bits 16,20,24
    sh[19] = float3(
        int(packedSH4.y << 12u) >> 28,
        int(packedSH4.y << 8u) >> 28,
        int(packedSH4.y << 4u) >> 28
    ) / 7.0;
    // sh[20] = values 15,16,17 → word 1 bit 28 + word 2 bits 0,4
    sh[20] = float3(
        int(packedSH4.y) >> 28,
        int(packedSH4.z << 28u) >> 28,
        int(packedSH4.z << 24u) >> 28
    ) / 7.0;
    // sh[21] = values 18,19,20 → word 2 bits 8,12,16
    sh[21] = float3(
        int(packedSH4.z << 20u) >> 28,
        int(packedSH4.z << 16u) >> 28,
        int(packedSH4.z << 12u) >> 28
    ) / 7.0;
    // sh[22] = values 21,22,23 → word 2 bits 20,24,28
    sh[22] = float3(
        int(packedSH4.z << 8u) >> 28,
        int(packedSH4.z << 4u) >> 28,
        int(packedSH4.z) >> 28
    ) / 7.0;
    // sh[23] = values 24,25,26 → word 3 bits 0,4,8
    sh[23] = float3(
        int(packedSH4.w << 28u) >> 28,
        int(packedSH4.w << 24u) >> 28,
        int(packedSH4.w << 20u) >> 28
    ) / 7.0;
    #endif
}
#endif

#endif
