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

/// One splat at the origin in the standard 3DGS PLY layout with SH degree 1 only. `sh1` is
/// coefficient-major, channel-minor (`sh1[k*3 + c]`); the file stores it channel-major
/// (`f_rest_{c*3 + k}`), as 3DGS exports do. `q` is written as rot_0..3 = (w, x, y, z).
pub fn one_splat_ply(sh1: &[f32; 9], q: Quat) -> Vec<u8> {
    let mut header = String::from("ply\nformat binary_little_endian 1.0\nelement vertex 1\n");
    let names = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2"].map(String::from).into_iter()
        .chain((0..9).map(|i| format!("f_rest_{i}")))
        .chain(["opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"].map(String::from));
    for p in names {
        header += &format!("property float {p}\n");
    }
    header += "end_header\n";
    let mut values = vec![0.0, 0.0, 0.0, 0.1, 0.2, 0.3];
    values.extend((0..9).map(|i| sh1[(i % 3) * 3 + i / 3]));
    values.extend([2.0, 0.03f32.ln(), 0.02f32.ln(), 0.01f32.ln(), q.w, q.x, q.y, q.z]);
    let mut bytes = header.into_bytes();
    for v in values {
        bytes.extend_from_slice(&v.to_le_bytes());
    }
    bytes
}

/// A `side`³ grid in the antimatter15 `.splat` layout: 32 B per splat — centre f32×3, linear scale
/// f32×3, RGBA u8×4, quaternion u8×4 as (w, x, y, z) mapped by `q * 128 + 128`.
pub fn synthetic_splat(side: usize) -> Vec<u8> {
    let mut bytes = Vec::new();
    for i in 0..side * side * side {
        let c = [(i % side) as f32 * 0.1, ((i / side) % side) as f32 * 0.1, (i / (side * side)) as f32 * 0.1];
        for v in c.into_iter().chain([0.03, 0.02, 0.01]) {
            bytes.extend_from_slice(&f32::to_le_bytes(v));
        }
        bytes.extend_from_slice(&[200, 100, 50, 220, 255, 128, 128, 128]);
    }
    bytes
}
