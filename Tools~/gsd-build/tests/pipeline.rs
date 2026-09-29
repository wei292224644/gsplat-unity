mod common;

use glam::{Quat, Vec3};
use gsd_build::encode::{unpack_ext_splat, unpack_sh1};
use gsd_build::format;
use gsd_build::frame::SourceFrame;
use gsd_build::pipeline::{build, tree_levels, BuildOptions};

fn options(source: SourceFrame) -> BuildOptions {
    BuildOptions { source, max_sh: None, quick: false }
}

#[test]
fn builds_a_valid_tree_over_every_input_splat() {
    let (ply, _) = common::synthetic_ply(4);
    let (file, stats) = build(&ply, "synthetic.ply", &options(SourceFrame::Ruf)).unwrap();
    assert_eq!((stats.leaves, file.leaf_count), (64, 64));
    assert!(file.nodes.len() > 64, "the LoD tree must add interior nodes");
    assert!(file.child_count[0] > 0, "node 0 is the root");
    assert_eq!(file.sh_degree, 3);
    assert_eq!(stats.levels, tree_levels(&file.child_start, &file.child_count));
    assert!(stats.levels > 1, "an LoD tree over 64 splats has more than one level");
    assert_eq!(format::read(&format::write(&file)), Ok(file));
}

#[test]
fn levels_count_the_root_as_one() {
    // The fixture's shape: root 0 → [1, 2]; node 1 → [3, 4, 5]; node 2 → [6, 7].
    assert_eq!(tree_levels(&[1, 3, 6, 0, 0, 0, 0, 0], &[2, 3, 2, 0, 0, 0, 0, 0]), 3);
    assert_eq!(tree_levels(&[0], &[0]), 1);
}

/// Pins frame.rs's RUB rules end to end — real PLY decoder, tree build and encoder — on one splat
/// whose rotation and SH1 are known: RUB → RUF flips z, so the quaternion's x and y change sign
/// (each imaginary part takes the product of the other two axis signs) and SH1's z coefficient
/// (k = 1, the odd one) changes sign; w, SH1 k = 0 (y) and k = 2 (x) are untouched.
#[test]
fn rub_rotation_and_sh1_come_out_in_the_unity_frame() {
    let q = Quat::from_axis_angle(Vec3::new(1.0, 2.0, 3.0).normalize(), 1.0);
    // On the 1/63 grid of the sint7 SH1 encoding so the round trip is exact; all distinct, all non-zero.
    let sh1: [f32; 9] = std::array::from_fn(|j| (4 * (j as i32 + 1)) as f32 * if j % 2 == 0 { 1.0 } else { -1.0 } / 63.0);
    let (file, stats) = build(&common::one_splat_ply(&sh1, q), "one.ply", &options(SourceFrame::Rub)).unwrap();
    assert_eq!((stats.leaves, file.nodes.len(), file.sh_degree), (1, 1, 1));

    let got = unpack_ext_splat(&file.nodes[0]).quat;
    let expected = Quat::from_xyzw(-q.x, -q.y, q.z, q.w);
    for (g, e) in got.to_array().into_iter().zip(expected.to_array()) {
        assert!((g - e).abs() < 2e-3, "quaternion {got:?}, expected {expected:?}");
    }

    let signs = [1.0, -1.0, 1.0];
    let expected_sh1: [f32; 9] = std::array::from_fn(|j| sh1[j] * signs[j / 3]);
    assert_eq!(unpack_sh1(&file.sh1[0]), expected_sh1);
}

#[test]
fn antisplat_input_decodes() {
    let (file, stats) = build(&common::synthetic_splat(3), "synthetic.splat", &options(SourceFrame::Ruf)).unwrap();
    assert_eq!((stats.leaves, file.leaf_count, file.sh_degree), (27, 27, 0));
}

#[test]
fn leaves_land_in_the_unity_frame() {
    let (ply, centers) = common::synthetic_ply(4);
    let (file, _) = build(&ply, "synthetic.ply", &options(SourceFrame::Rub)).unwrap();
    let key = |c: &[f32; 3]| c.map(f32::to_bits);
    let mut leaves: Vec<[f32; 3]> = file
        .nodes
        .iter()
        .zip(&file.child_count)
        .filter(|(_, &count)| count == 0)
        .map(|(w, _)| [f32::from_bits(w[0]), f32::from_bits(w[1]), f32::from_bits(w[2])])
        .collect();
    let mut expected: Vec<[f32; 3]> = centers.iter().map(|c| [c[0], c[1], -c[2]]).collect();
    leaves.sort_by_key(key);
    expected.sort_by_key(key);
    assert_eq!(leaves, expected);
}

#[test]
fn every_node_carries_an_encodable_alpha() {
    let (ply, _) = common::synthetic_ply(4);
    let (file, _) = build(&ply, "synthetic.ply", &options(SourceFrame::Ruf)).unwrap();
    for (i, w) in file.nodes.iter().enumerate() {
        let alpha = gsd_build::encode::unpack_ext_splat(w).alpha;
        assert!(alpha > 0.0 && alpha <= 5.0, "node {i} alpha {alpha}");
    }
}

#[test]
fn max_sh_clamps_the_degree() {
    let (ply, _) = common::synthetic_ply(3);
    let (file, _) = build(&ply, "synthetic.ply", &BuildOptions { max_sh: Some(1), ..options(SourceFrame::Ruf) }).unwrap();
    assert_eq!(file.sh_degree, 1);
    assert!(file.sh2.is_empty() && file.sh3.is_empty());
}

#[test]
fn quick_method_also_yields_a_valid_tree() {
    let (ply, _) = common::synthetic_ply(3);
    let (file, _) = build(&ply, "synthetic.ply", &BuildOptions { quick: true, ..options(SourceFrame::Ruf) }).unwrap();
    assert_eq!(file.leaf_count, 27);
}

#[test]
fn non_finite_input_is_refused() {
    let (mut ply, _) = common::synthetic_ply(3);
    common::poison_first_x(&mut ply);
    let err = build(&ply, "synthetic.ply", &options(SourceFrame::Ruf)).unwrap_err();
    assert!(format!("{err:#}").contains("non-finite"), "{err:#}");
}
