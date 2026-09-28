//! `.gsd` container (spec §4): a 48-byte header, then fixed sections, each starting 16-byte
//! aligned so it can be uploaded to a GraphicsBuffer as is. `GsdReader.cs` computes the same layout.

use crate::invariants::{check_tree, TreeError};
use std::fmt;

pub const MAGIC: [u8; 4] = *b"GSD\0";
pub const VERSION: u32 = 1;
pub const HEADER_SIZE: usize = 48;

#[derive(Clone, Debug, PartialEq)]
pub struct GsdFile {
    pub leaf_count: u32,
    pub sh_degree: u8,
    pub bounds_min: [f32; 3],
    pub bounds_max: [f32; 3],
    pub nodes: Vec<[u32; 8]>,
    pub sh1: Vec<[u32; 2]>,
    pub sh2: Vec<[u32; 4]>,
    pub sh3: Vec<[u32; 4]>,
    pub child_start: Vec<u32>,
    pub child_count: Vec<u16>,
}

#[derive(Debug, PartialEq)]
pub enum FormatError {
    BadMagic,
    UnsupportedVersion(u32),
    ShDegreeOutOfRange(u8),
    Truncated { expected: u64, actual: u64 },
    TrailingBytes { expected: u64, actual: u64 },
    Tree(TreeError),
}

impl fmt::Display for FormatError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        use FormatError::*;
        match self {
            BadMagic => write!(f, "gsd: bad magic"),
            UnsupportedVersion(v) => write!(f, "gsd: unsupported version {v}"),
            ShDegreeOutOfRange(d) => write!(f, "gsd: sh degree {d} out of range 0..3"),
            Truncated { expected, actual } => write!(f, "gsd: truncated (expected {expected} bytes, got {actual})"),
            TrailingBytes { expected, actual } => write!(f, "gsd: trailing bytes (expected {expected} bytes, got {actual})"),
            Tree(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for FormatError {}

impl From<TreeError> for FormatError {
    fn from(e: TreeError) -> Self {
        FormatError::Tree(e)
    }
}

const fn align16(n: u64) -> u64 {
    (n + 15) & !15
}

/// Byte offset of every section. An absent SH band has offset 0.
#[derive(Debug, PartialEq, Eq)]
pub struct Layout {
    pub nodes: u64,
    pub sh1: u64,
    pub sh2: u64,
    pub sh3: u64,
    pub child_start: u64,
    pub child_count: u64,
    pub total: u64,
}

pub fn layout(node_count: u32, sh_degree: u8) -> Layout {
    let n = u64::from(node_count);
    let mut pos = HEADER_SIZE as u64;
    let nodes = pos;
    pos += n * 32;
    let mut sh = [0u64; 3];
    for (band, bytes_per_node) in [8u64, 16, 16].into_iter().enumerate() {
        if usize::from(sh_degree) > band {
            pos = align16(pos);
            sh[band] = pos;
            pos += n * bytes_per_node;
        }
    }
    pos = align16(pos);
    let child_start = pos;
    pos += n * 4;
    pos = align16(pos);
    let child_count = pos;
    pos += n * 2;
    Layout { nodes, sh1: sh[0], sh2: sh[1], sh3: sh[2], child_start, child_count, total: align16(pos) }
}

pub fn write(file: &GsdFile) -> Vec<u8> {
    let n = file.nodes.len() as u32;
    let l = layout(n, file.sh_degree);
    let mut out = vec![0u8; l.total as usize];
    out[0..4].copy_from_slice(&MAGIC);
    put_u32(&mut out, 4, VERSION);
    put_u32(&mut out, 8, n);
    put_u32(&mut out, 12, file.leaf_count);
    out[16] = file.sh_degree;
    for a in 0..3 {
        put_u32(&mut out, 20 + a * 4, file.bounds_min[a].to_bits());
        put_u32(&mut out, 32 + a * 4, file.bounds_max[a].to_bits());
    }
    put_words(&mut out, l.nodes, file.nodes.iter().flatten());
    if file.sh_degree >= 1 {
        put_words(&mut out, l.sh1, file.sh1.iter().flatten());
    }
    if file.sh_degree >= 2 {
        put_words(&mut out, l.sh2, file.sh2.iter().flatten());
    }
    if file.sh_degree >= 3 {
        put_words(&mut out, l.sh3, file.sh3.iter().flatten());
    }
    put_words(&mut out, l.child_start, file.child_start.iter());
    for (i, c) in file.child_count.iter().enumerate() {
        let at = l.child_count as usize + i * 2;
        out[at..at + 2].copy_from_slice(&c.to_le_bytes());
    }
    out
}

pub fn read(bytes: &[u8]) -> Result<GsdFile, FormatError> {
    use FormatError::*;
    let actual = bytes.len() as u64;
    if bytes.len() < HEADER_SIZE {
        return Err(Truncated { expected: HEADER_SIZE as u64, actual });
    }
    if bytes[0..4] != MAGIC {
        return Err(BadMagic);
    }
    let version = get_u32(bytes, 4);
    if version != VERSION {
        return Err(UnsupportedVersion(version));
    }
    let n = get_u32(bytes, 8);
    let leaf_count = get_u32(bytes, 12);
    let sh_degree = bytes[16];
    if sh_degree > 3 {
        return Err(ShDegreeOutOfRange(sh_degree));
    }
    let l = layout(n, sh_degree);
    if actual < l.total {
        return Err(Truncated { expected: l.total, actual });
    }
    if actual > l.total {
        return Err(TrailingBytes { expected: l.total, actual });
    }
    let n = n as usize;
    let words = |at: u64, i: usize, w: usize| -> Vec<u32> { (0..w).map(|j| get_u32(bytes, at as usize + (i * w + j) * 4)).collect() };
    let file = GsdFile {
        leaf_count,
        sh_degree,
        bounds_min: std::array::from_fn(|a| f32::from_bits(get_u32(bytes, 20 + a * 4))),
        bounds_max: std::array::from_fn(|a| f32::from_bits(get_u32(bytes, 32 + a * 4))),
        nodes: (0..n).map(|i| words(l.nodes, i, 8).try_into().unwrap()).collect(),
        sh1: if sh_degree >= 1 { (0..n).map(|i| words(l.sh1, i, 2).try_into().unwrap()).collect() } else { Vec::new() },
        sh2: if sh_degree >= 2 { (0..n).map(|i| words(l.sh2, i, 4).try_into().unwrap()).collect() } else { Vec::new() },
        sh3: if sh_degree >= 3 { (0..n).map(|i| words(l.sh3, i, 4).try_into().unwrap()).collect() } else { Vec::new() },
        child_start: (0..n).map(|i| get_u32(bytes, l.child_start as usize + i * 4)).collect(),
        child_count: (0..n)
            .map(|i| {
                let at = l.child_count as usize + i * 2;
                u16::from_le_bytes([bytes[at], bytes[at + 1]])
            })
            .collect(),
    };
    check_tree(&file.child_start, &file.child_count, file.leaf_count)?;
    Ok(file)
}

fn put_u32(out: &mut [u8], at: usize, v: u32) {
    out[at..at + 4].copy_from_slice(&v.to_le_bytes());
}

fn put_words<'a>(out: &mut [u8], at: u64, words: impl Iterator<Item = &'a u32>) {
    for (i, w) in words.enumerate() {
        put_u32(out, at as usize + i * 4, *w);
    }
}

fn get_u32(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// root 0 → [1, 2], SH degree 1: an odd node count so the SH1 section ends unaligned.
    fn three_nodes() -> GsdFile {
        GsdFile {
            leaf_count: 2,
            sh_degree: 1,
            bounds_min: [-1.0, -2.0, -3.0],
            bounds_max: [1.0, 2.0, 3.0],
            nodes: vec![[1, 2, 3, 4, 5, 6, 7, 8], [9; 8], [10; 8]],
            sh1: vec![[11, 12], [13, 14], [15, 16]],
            sh2: Vec::new(),
            sh3: Vec::new(),
            child_start: vec![1, 0, 0],
            child_count: vec![2, 0, 0],
        }
    }

    #[test]
    fn layout_aligns_every_section() {
        assert_eq!(
            layout(3, 1),
            Layout { nodes: 48, sh1: 144, sh2: 0, sh3: 0, child_start: 176, child_count: 192, total: 208 }
        );
    }

    #[test]
    fn write_then_read_round_trips() {
        let file = three_nodes();
        assert_eq!(read(&write(&file)), Ok(file));
    }

    #[test]
    fn read_rejects_malformed_containers() {
        let good = write(&three_nodes());
        let mut bad = good.clone();
        bad[0] = b'X';
        assert_eq!(read(&bad), Err(FormatError::BadMagic));
        let mut bad = good.clone();
        bad[4] = 2;
        assert_eq!(read(&bad), Err(FormatError::UnsupportedVersion(2)));
        let mut bad = good.clone();
        bad[16] = 4;
        assert_eq!(read(&bad), Err(FormatError::ShDegreeOutOfRange(4)));
        assert_eq!(read(&good[..good.len() - 1]), Err(FormatError::Truncated { expected: 208, actual: 207 }));
        let mut bad = good.clone();
        bad.extend_from_slice(&[0; 16]);
        assert_eq!(read(&bad), Err(FormatError::TrailingBytes { expected: 208, actual: 224 }));
    }

    #[test]
    fn read_rejects_a_broken_tree() {
        let mut file = three_nodes();
        file.child_start[0] = 0;
        assert!(matches!(read(&write(&file)), Err(FormatError::Tree(TreeError::ChildStartNotAfterParent { .. }))));
    }
}
