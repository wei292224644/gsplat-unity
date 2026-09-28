//! Bit-level codecs for the `.gsd` node payload.
//!
//! Every encoder here has a decoder twin, and `Runtime/Shaders/GsplatLodDecode.hlsl` mirrors the
//! decoders on the GPU. `tests/fixture.rs` writes the decoded values next to an encoded fixture so
//! the Unity side checks its HLSL against them: the two languages share one contract.
//!
//! The quaternion codec and the 32-byte ExtSplat layout are ported from Spark
//! (`src/shaders/splatDefines.glsl`, World Labs Technologies, MIT).

use glam::{Quat, Vec3};
use half::f16;
use std::f32::consts::PI;

pub fn pack_half2x16(lo: f32, hi: f32) -> u32 {
    u32::from(f16::from_f32(lo).to_bits()) | (u32::from(f16::from_f32(hi).to_bits()) << 16)
}

pub fn unpack_half2x16(packed: u32) -> (f32, f32) {
    (
        f16::from_bits((packed & 0xffff) as u16).to_f32(),
        f16::from_bits((packed >> 16) as u16).to_f32(),
    )
}

/// Spark `encodeQuatOctXy1010R12`. `q` is a real quaternion (x, y, z, w), w = cos(θ/2).
pub fn encode_quat_oct1010r12(q: Quat) -> u32 {
    let mut q = q.normalize();
    if q.w < 0.0 {
        q = -q;
    }
    let half_theta = q.w.clamp(-1.0, 1.0).acos();
    let theta = 2.0 * half_theta;
    let s = half_theta.sin();
    let axis = if s.abs() < 1e-6 { Vec3::X } else { Vec3::new(q.x, q.y, q.z) / s };
    let sum = axis.x.abs() + axis.y.abs() + axis.z.abs();
    let mut px = axis.x / sum;
    let mut py = axis.y / sum;
    if axis.z < 0.0 {
        let old_px = px;
        px = (1.0 - py.abs()) * if px >= 0.0 { 1.0 } else { -1.0 };
        py = (1.0 - old_px.abs()) * if py >= 0.0 { 1.0 } else { -1.0 };
    }
    let quant_u = ((px * 0.5 + 0.5) * 1023.0).round().clamp(0.0, 1023.0) as u32;
    let quant_v = ((py * 0.5 + 0.5) * 1023.0).round().clamp(0.0, 1023.0) as u32;
    let angle = ((theta / PI) * 4095.0).round().clamp(0.0, 4095.0) as u32;
    (angle << 20) | (quant_v << 10) | quant_u
}

/// Spark `decodeQuatOctXy1010R12`. Returns a real quaternion (x, y, z, w).
pub fn decode_quat_oct1010r12(encoded: u32) -> Quat {
    let fx = (encoded & 0x3ff) as f32 / 1023.0 * 2.0 - 1.0;
    let fy = ((encoded >> 10) & 0x3ff) as f32 / 1023.0 * 2.0 - 1.0;
    let mut axis = Vec3::new(fx, fy, 1.0 - fx.abs() - fy.abs());
    let t = (-axis.z).max(0.0);
    axis.x += if axis.x >= 0.0 { -t } else { t };
    axis.y += if axis.y >= 0.0 { -t } else { t };
    let axis = axis.normalize();
    let theta = (encoded >> 20) as f32 / 4095.0 * PI;
    let (s, c) = (theta * 0.5).sin_cos();
    Quat::from_xyzw(axis.x * s, axis.y * s, axis.z * s, c)
}

/// One LoD tree node as laid out in the file: eight little-endian u32 words.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ExtSplat {
    pub center: Vec3,
    /// Linear opacity in 0..=1 for plain splats; the merged-node D in (1, 5] for LoD interiors.
    pub alpha: f32,
    pub rgb: Vec3,
    pub ln_scales: Vec3,
    /// Real quaternion (x, y, z, w).
    pub quat: Quat,
}

pub fn pack_ext_splat(s: &ExtSplat) -> [u32; 8] {
    [
        s.center.x.to_bits(),
        s.center.y.to_bits(),
        s.center.z.to_bits(),
        pack_half2x16(s.alpha, 0.0),
        pack_half2x16(s.rgb.x, s.rgb.y),
        pack_half2x16(s.rgb.z, s.ln_scales.x),
        pack_half2x16(s.ln_scales.y, s.ln_scales.z),
        encode_quat_oct1010r12(s.quat),
    ]
}

pub fn unpack_ext_splat(w: &[u32; 8]) -> ExtSplat {
    let (alpha, _) = unpack_half2x16(w[3]);
    let (r, g) = unpack_half2x16(w[4]);
    let (b, ln_x) = unpack_half2x16(w[5]);
    let (ln_y, ln_z) = unpack_half2x16(w[6]);
    ExtSplat {
        center: Vec3::new(f32::from_bits(w[0]), f32::from_bits(w[1]), f32::from_bits(w[2])),
        alpha,
        rgb: Vec3::new(r, g, b),
        ln_scales: Vec3::new(ln_x, ln_y, ln_z),
        quat: decode_quat_oct1010r12(w[7]),
    }
}

/// Packs `values` as consecutive `bits`-wide two's-complement fields, LSB first, a field allowed
/// to straddle a word boundary: the layout `GsplatAssetSpark.PackSH1/2/3` writes and
/// `GsplatSparkSH.hlsl` reads.
fn pack_signed_bits(values: &[f32], scale: f32, bits: u32, out: &mut [u32]) {
    out.fill(0);
    let mask = (1u32 << bits) - 1;
    for (i, &v) in values.iter().enumerate() {
        let raw = ((v * scale).clamp(-scale, scale).round() as i32 as u32) & mask;
        let bit = i as u32 * bits;
        let (word, offset) = ((bit / 32) as usize, bit % 32);
        out[word] |= raw << offset;
        if offset + bits > 32 {
            out[word + 1] |= raw >> (32 - offset);
        }
    }
}

fn unpack_signed_bits(words: &[u32], count: usize, scale: f32, bits: u32) -> Vec<f32> {
    let mask = (1u64 << bits) - 1;
    (0..count)
        .map(|i| {
            let bit = i as u32 * bits;
            let (word, offset) = ((bit / 32) as usize, bit % 32);
            let mut raw = u64::from(words[word]) >> offset;
            if offset + bits > 32 {
                raw |= u64::from(words[word + 1]) << (32 - offset);
            }
            let raw = (raw & mask) as u32;
            let signed = ((raw << (32 - bits)) as i32) >> (32 - bits);
            signed as f32 / scale
        })
        .collect()
}

pub fn pack_sh1(sh: &[f32; 9]) -> [u32; 2] {
    let mut out = [0; 2];
    pack_signed_bits(sh, 63.0, 7, &mut out);
    out
}

pub fn pack_sh2(sh: &[f32; 15]) -> [u32; 4] {
    let mut out = [0; 4];
    pack_signed_bits(sh, 127.0, 8, &mut out);
    out
}

pub fn pack_sh3(sh: &[f32; 21]) -> [u32; 4] {
    let mut out = [0; 4];
    pack_signed_bits(sh, 31.0, 6, &mut out);
    out
}

pub fn unpack_sh1(w: &[u32; 2]) -> [f32; 9] {
    unpack_signed_bits(w, 9, 63.0, 7).try_into().unwrap()
}

pub fn unpack_sh2(w: &[u32; 4]) -> [f32; 15] {
    unpack_signed_bits(w, 15, 127.0, 8).try_into().unwrap()
}

pub fn unpack_sh3(w: &[u32; 4]) -> [f32; 21] {
    unpack_signed_bits(w, 21, 31.0, 6).try_into().unwrap()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn same_rotation(a: Quat, b: Quat) -> bool {
        a.dot(b).abs() > 1.0 - 1e-4
    }

    #[test]
    fn half_pair_round_trips() {
        assert_eq!(unpack_half2x16(pack_half2x16(1.5, -0.25)), (1.5, -0.25));
    }

    #[test]
    fn identity_quat_round_trips() {
        let q = decode_quat_oct1010r12(encode_quat_oct1010r12(Quat::IDENTITY));
        assert!(same_rotation(q, Quat::IDENTITY), "{q:?}");
    }

    #[test]
    fn sample_quats_round_trip_within_quantisation() {
        let samples = [
            Quat::from_rotation_y(1.2),
            Quat::from_rotation_x(-2.9),
            Quat::from_axis_angle(Vec3::new(1.0, -2.0, 0.5).normalize(), 0.7),
            Quat::from_axis_angle(Vec3::new(-0.3, 0.2, -0.9).normalize(), 3.1),
            Quat::from_xyzw(0.1, 0.2, 0.3, -0.9).normalize(),
        ];
        for q in samples {
            let d = decode_quat_oct1010r12(encode_quat_oct1010r12(q));
            assert!(same_rotation(d, q), "{q:?} decoded as {d:?}");
        }
    }

    #[test]
    fn ext_splat_round_trips() {
        let s = ExtSplat {
            center: Vec3::new(1234.5678, -0.001, 3.0e4),
            alpha: 3.5,
            rgb: Vec3::new(0.25, 0.5, -0.75),
            ln_scales: Vec3::new(-3.0, -2.5, -1.25),
            quat: Quat::IDENTITY,
        };
        let d = unpack_ext_splat(&pack_ext_splat(&s));
        assert_eq!(d.center, s.center, "centers are carried bit-for-bit as f32");
        assert_eq!((d.alpha, d.rgb, d.ln_scales), (s.alpha, s.rgb, s.ln_scales));
        assert!(same_rotation(d.quat, s.quat));
    }

    #[test]
    fn sh_bands_round_trip_on_their_grid() {
        let sh1: [f32; 9] = std::array::from_fn(|i| (i as f32 - 4.0) / 63.0);
        let sh2: [f32; 15] = std::array::from_fn(|i| (i as f32 * 9.0 - 60.0) / 127.0);
        let sh3: [f32; 21] = std::array::from_fn(|i| (i as f32 * 3.0 - 30.0) / 31.0);
        assert_eq!(unpack_sh1(&pack_sh1(&sh1)), sh1);
        assert_eq!(unpack_sh2(&pack_sh2(&sh2)), sh2);
        assert_eq!(unpack_sh3(&pack_sh3(&sh3)), sh3);
    }

    #[test]
    fn sh_values_beyond_unit_range_clamp() {
        assert_eq!(unpack_sh1(&pack_sh1(&[2.0; 9])), [1.0; 9]);
        assert_eq!(unpack_sh3(&pack_sh3(&[-7.0; 21])), [-1.0; 21]);
    }
}
