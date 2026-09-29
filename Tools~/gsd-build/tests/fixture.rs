use glam::{Quat, Vec3};
use gsd_build::encode::{self, ExtSplat};
use gsd_build::format::{self, GsdFile};
use serde_json::json;
use std::path::PathBuf;

fn fixture_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../Tests/Editor/Fixtures")
}

/// root 0 → [1, 2]; node 1 → [3, 4, 5]; node 2 → [6, 7]: 8 nodes, 5 leaves, SH degree 3.
/// Interiors carry D > 1, one coordinate sits near 1000 (f32 centres, D3), and the rotation angle
/// sweeps −3..3 rad, so the axis meets both signs. w = cos(θ/2) stays positive (|θ| < π): the
/// encoder's w < 0 flip is covered by `encode.rs`'s unit tests, not by this fixture.
fn tiny() -> GsdFile {
    let child_count: [u16; 8] = [2, 3, 2, 0, 0, 0, 0, 0];
    let child_start: [u32; 8] = [1, 3, 6, 0, 0, 0, 0, 0];
    let mut file = GsdFile {
        leaf_count: 5,
        sh_degree: 3,
        bounds_min: [f32::MAX; 3],
        bounds_max: [f32::MIN; 3],
        nodes: Vec::new(),
        sh1: Vec::new(),
        sh2: Vec::new(),
        sh3: Vec::new(),
        child_start: child_start.to_vec(),
        child_count: child_count.to_vec(),
    };
    for i in 0..8 {
        let t = i as f32 / 7.0;
        let center = Vec3::new(1.5 * t - 0.3, -2.0 + t, 1000.0 + 3.25 * t);
        file.nodes.push(encode::pack_ext_splat(&ExtSplat {
            center,
            alpha: if child_count[i] > 0 { 1.25 + 3.5 * t } else { 0.1 + 0.8 * t },
            rgb: Vec3::new(t, 1.0 - t, -0.2 + 1.4 * t),
            ln_scales: Vec3::new(-4.0 + t, -3.0 - t, -2.5),
            quat: Quat::from_axis_angle(Vec3::new(0.3, -1.0, 0.2 + t).normalize(), -3.0 + 6.0 * t),
        }));
        let sh = |k: usize, len: f32| ((i * 5 + k * 3) % 13) as f32 / 13.0 * len - 0.5 * len;
        file.sh1.push(encode::pack_sh1(&std::array::from_fn(|k| sh(k, 1.0))));
        file.sh2.push(encode::pack_sh2(&std::array::from_fn(|k| sh(k, 0.8))));
        file.sh3.push(encode::pack_sh3(&std::array::from_fn(|k| sh(k, 0.6))));
        if child_count[i] == 0 {
            for a in 0..3 {
                file.bounds_min[a] = file.bounds_min[a].min(center[a]);
                file.bounds_max[a] = file.bounds_max[a].max(center[a]);
            }
        }
    }
    file
}

fn expected_json(file: &GsdFile) -> serde_json::Value {
    let nodes: Vec<_> = (0..file.nodes.len())
        .map(|i| {
            let s = encode::unpack_ext_splat(&file.nodes[i]);
            let mut sh = Vec::new();
            sh.extend(encode::unpack_sh1(&file.sh1[i]));
            sh.extend(encode::unpack_sh2(&file.sh2[i]));
            sh.extend(encode::unpack_sh3(&file.sh3[i]));
            json!({
                "center": s.center.to_array(),
                "alpha": s.alpha,
                "rgb": s.rgb.to_array(),
                "scale": s.ln_scales.to_array().map(f32::exp),
                "quat": s.quat.to_array(),
                "sh": sh,
                "childStart": file.child_start[i],
                "childCount": file.child_count[i],
            })
        })
        .collect();
    json!({ "nodeCount": file.nodes.len(), "leafCount": file.leaf_count, "shDegree": file.sh_degree, "nodes": nodes })
}

/// Regenerates the Unity-side fixture: `cargo test --test fixture -- --ignored`.
#[test]
#[ignore]
fn regenerate_fixture() {
    let file = tiny();
    std::fs::create_dir_all(fixture_dir()).unwrap();
    std::fs::write(fixture_dir().join("tiny.gsd"), format::write(&file)).unwrap();
    let json = serde_json::to_string_pretty(&expected_json(&file)).unwrap();
    std::fs::write(fixture_dir().join("tiny.expected.json"), json + "\n").unwrap();
}

/// The committed fixture must be what the current encoder writes; a stale one would have the Unity
/// test checking HLSL against a contract that no longer exists.
#[test]
fn committed_fixture_is_current() {
    let committed = std::fs::read(fixture_dir().join("tiny.gsd")).expect("fixture missing: run `cargo test --test fixture -- --ignored`");
    assert_eq!(committed, format::write(&tiny()), "fixture is stale: run `cargo test --test fixture -- --ignored`");
}

#[test]
fn fixture_passes_the_reader() {
    assert_eq!(format::read(&format::write(&tiny())), Ok(tiny()));
}
