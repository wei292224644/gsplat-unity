//! ply/spz → LoD tree → `.gsd` (spec §5). Decoding, LoD construction and the tree reorder come
//! from Spark's `spark-lib`; this module owns validation, the frame conversion and the encoding.

use anyhow::{bail, Context, Result};
use glam::{Quat, Vec3};
use spark_lib::decoder::{ChunkReceiver, MultiDecoder};
use spark_lib::gsplat::GsplatArray;
use spark_lib::tsplat::{Tsplat, TsplatArray};

use crate::encode::{pack_ext_splat, pack_sh1, pack_sh2, pack_sh3, ExtSplat};
use crate::format::GsdFile;
use crate::frame::{convert_center, convert_quat, convert_sh, SourceFrame};
use crate::invariants::check_tree;

/// Spark's `--quality` preset (bhatt-lod).
pub const QUALITY_LOD_BASE: f32 = 1.75;
/// Spark's `--quick` preset (tiny-lod).
pub const QUICK_LOD_BASE: f32 = 1.5;

#[derive(Clone, Copy, Debug)]
pub struct BuildOptions {
    pub source: SourceFrame,
    pub max_sh: Option<u8>,
    pub quick: bool,
}

#[derive(Clone, Debug, Default)]
pub struct BuildStats {
    pub input_splats: usize,
    pub dropped_empty: usize,
    pub leaves: usize,
    pub nodes: usize,
    pub sh_degree: u8,
    pub lod_seconds: f64,
}

pub fn build(bytes: &[u8], file_name: &str, options: &BuildOptions) -> Result<(GsdFile, BuildStats)> {
    let mut splats = decode(bytes, file_name)?;
    let mut stats = BuildStats { input_splats: splats.len(), ..Default::default() };
    reject_non_finite(&splats)?;
    // Same filter as Spark's build-lod: zero opacity, zero scale or a zero quaternion cannot be
    // merged meaningfully, and log(0) scales would poison the encoding.
    splats.retain(|s| s.opacity() > 0.0 && s.max_scale() > 0.0 && s.quaternion().length() > 0.0);
    stats.dropped_empty = stats.input_splats - splats.len();
    if splats.len() == 0 {
        bail!("{file_name}: no splats left after dropping empty ones");
    }
    if let Some(max_sh) = options.max_sh {
        splats.clamp_sh_degree(usize::from(max_sh));
    }
    stats.leaves = splats.len();

    let started = std::time::Instant::now();
    if options.quick {
        spark_lib::tiny_lod::compute_lod_tree(&mut splats, QUICK_LOD_BASE, false, |_| {});
    } else {
        spark_lib::bhatt_lod::compute_lod_tree(&mut splats, QUALITY_LOD_BASE, |_| {});
    }
    spark_lib::chunk_tree::chunk_tree(&mut splats, 0, |_| {});
    stats.lod_seconds = started.elapsed().as_secs_f64();

    let file = gather(&splats, options.source, stats.leaves as u32)?;
    stats.nodes = file.nodes.len();
    stats.sh_degree = file.sh_degree;
    Ok((file, stats))
}

fn decode(bytes: &[u8], file_name: &str) -> Result<GsplatArray> {
    let mut decoder = MultiDecoder::new(GsplatArray::new(), None, Some(file_name));
    decoder.push(bytes).with_context(|| format!("decoding {file_name}"))?;
    decoder.finish().with_context(|| format!("decoding {file_name}"))?;
    Ok(decoder.into_splats())
}

fn reject_non_finite(splats: &GsplatArray) -> Result<()> {
    let bad = (0..splats.len())
        .filter(|&i| {
            let s = splats.get(i);
            !(s.center().is_finite() && s.scales().is_finite() && s.quaternion().is_finite() && s.opacity().is_finite() && s.rgb().is_finite())
        })
        .count();
    if bad > 0 {
        bail!("{bad} splats have non-finite attributes; refusing to build a tree over them");
    }
    Ok(())
}

/// Encodes the reordered tree. The frame conversion happens here, after the tree is built, so the
/// tree is the one Spark would build from the same file (spec D6).
fn gather(splats: &GsplatArray, source: SourceFrame, leaf_count: u32) -> Result<GsdFile> {
    let n = splats.len();
    let sh_degree = TsplatArray::max_sh_degree(splats) as u8;
    let mut file = GsdFile {
        leaf_count,
        sh_degree,
        bounds_min: [f32::MAX; 3],
        bounds_max: [f32::MIN; 3],
        nodes: Vec::with_capacity(n),
        sh1: Vec::new(),
        sh2: Vec::new(),
        sh3: Vec::new(),
        child_start: Vec::with_capacity(n),
        child_count: Vec::with_capacity(n),
    };
    for i in 0..n {
        let g = &splats.splats[i];
        let s = splats.get(i);
        let center = convert_center(source, Vec3::from(s.center()));
        let opacity = s.opacity();
        let (count, start) = splats.get_child_count_start(i);
        let count = u16::try_from(count).with_context(|| format!("node {i} has {count} children; the format stores at most 65535"))?;
        file.nodes.push(pack_ext_splat(&ExtSplat {
            center,
            alpha: if opacity > 1.0 { s.lod_opacity().min(5.0) } else { opacity },
            rgb: Vec3::from(s.rgb()),
            ln_scales: Vec3::new(g.ln_scales[0].to_f32(), g.ln_scales[1].to_f32(), g.ln_scales[2].to_f32()),
            quat: convert_quat(source, Quat::from_array(g.quaternion.map(|v| v.to_f32()))),
        }));
        if sh_degree >= 1 {
            let mut v = TsplatArray::get_sh1(splats, i);
            convert_sh(source, 1, &mut v);
            file.sh1.push(pack_sh1(&v));
        }
        if sh_degree >= 2 {
            let mut v = TsplatArray::get_sh2(splats, i);
            convert_sh(source, 2, &mut v);
            file.sh2.push(pack_sh2(&v));
        }
        if sh_degree >= 3 {
            let mut v = TsplatArray::get_sh3(splats, i);
            convert_sh(source, 3, &mut v);
            file.sh3.push(pack_sh3(&v));
        }
        file.child_start.push(if count == 0 { 0 } else { start as u32 });
        file.child_count.push(count);
        if count == 0 {
            for a in 0..3 {
                file.bounds_min[a] = file.bounds_min[a].min(center[a]);
                file.bounds_max[a] = file.bounds_max[a].max(center[a]);
            }
        }
    }
    check_tree(&file.child_start, &file.child_count, leaf_count)?;
    Ok(file)
}
