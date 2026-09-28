mod common;

use gsd_build::format;
use gsd_build::frame::SourceFrame;
use gsd_build::pipeline::{build, BuildOptions};

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
    assert_eq!(format::read(&format::write(&file)), Ok(file));
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
