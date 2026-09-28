//! Synthetic inputs for the integration tests.

#![allow(dead_code)]

use glam::{Quat, Vec3};

/// A binary little-endian PLY in the standard 3DGS layout: a `side`³ grid, 0.1 apart, SH degree 3.
/// Returns the bytes and the source-frame centres in file order.
pub fn synthetic_ply(side: usize) -> (Vec<u8>, Vec<[f32; 3]>) {
    let n = side * side * side;
    let mut header = format!("ply\nformat binary_little_endian 1.0\nelement vertex {n}\n");
    for p in ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2"] {
        header += &format!("property float {p}\n");
    }
    for i in 0..45 {
        header += &format!("property float f_rest_{i}\n");
    }
    for p in ["opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"] {
        header += &format!("property float {p}\n");
    }
    header += "end_header\n";

    let mut bytes = header.into_bytes();
    let mut centers = Vec::with_capacity(n);
    for i in 0..n {
        let c = [(i % side) as f32 * 0.1, ((i / side) % side) as f32 * 0.1, (i / (side * side)) as f32 * 0.1];
        centers.push(c);
        let t = i as f32 / n as f32;
        let q = Quat::from_axis_angle(Vec3::new(1.0, t, -0.5).normalize(), t * 3.0);
        let mut values = vec![c[0], c[1], c[2], t - 0.5, 0.5 - t, 0.25];
        values.extend((0..45).map(|k| ((i * 7 + k * 13) % 17) as f32 / 17.0 - 0.5));
        values.extend([2.0, 0.03f32.ln(), 0.02f32.ln(), (0.01 + 0.01 * t).ln(), q.w, q.x, q.y, q.z]);
        for v in values {
            bytes.extend_from_slice(&v.to_le_bytes());
        }
    }
    (bytes, centers)
}

/// Overwrites the first vertex's x with NaN.
pub fn poison_first_x(bytes: &mut [u8]) {
    let body = bytes.windows(11).position(|w| w == b"end_header\n").unwrap() + 11;
    bytes[body..body + 4].copy_from_slice(&f32::NAN.to_le_bytes());
}
