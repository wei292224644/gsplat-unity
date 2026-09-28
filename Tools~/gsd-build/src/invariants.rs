//! The `.gsd` tree invariants ① – ④ (spec §4). `GsdReader.CheckTree` in C# enforces the same list
//! with the same wording, so a file rejected on one side is rejected on the other.

use std::fmt;

#[derive(Debug, PartialEq, Eq)]
pub enum TreeError {
    Empty,
    ChildStartNotAfterParent { node: u32, child_start: u32 },
    ChildRangeOutOfBounds { node: u32, end: u64, node_count: u32 },
    ClaimedTwice { child: u32, second_parent: u32 },
    Orphan { node: u32 },
    LeafCountMismatch { declared: u32, actual: u32 },
}

impl fmt::Display for TreeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        use TreeError::*;
        match self {
            Empty => write!(f, "gsd: empty (nodeCount 0)"),
            ChildStartNotAfterParent { node, child_start } => write!(f, "gsd invariant 2: node {node} childStart {child_start} is not after its parent"),
            ChildRangeOutOfBounds { node, end, node_count } => write!(f, "gsd invariant 2: node {node} child range ends at {end}, past nodeCount {node_count}"),
            ClaimedTwice { child, second_parent } => write!(f, "gsd invariant 3: node {child} is claimed by more than one parent (second: node {second_parent})"),
            Orphan { node } => write!(f, "gsd invariant 3: node {node} has no parent"),
            LeafCountMismatch { declared, actual } => write!(f, "gsd invariant 4: leafCount declares {declared} but the tree has {actual} leaves"),
        }
    }
}

impl std::error::Error for TreeError {}

pub fn check_tree(child_start: &[u32], child_count: &[u16], leaf_count: u32) -> Result<(), TreeError> {
    use TreeError::*;
    let n = child_start.len();
    assert_eq!(n, child_count.len(), "child_start and child_count must have the same length");
    if n == 0 {
        return Err(Empty);
    }
    let mut has_parent = vec![false; n];
    has_parent[0] = true; // ①: node 0 is the root
    let mut leaves = 0u32;
    for i in 0..n {
        let count = u64::from(child_count[i]);
        if count == 0 {
            leaves += 1;
            continue;
        }
        let start = child_start[i];
        if start as usize <= i {
            return Err(ChildStartNotAfterParent { node: i as u32, child_start: start });
        }
        let end = u64::from(start) + count;
        if end > n as u64 {
            return Err(ChildRangeOutOfBounds { node: i as u32, end, node_count: n as u32 });
        }
        for c in start as usize..end as usize {
            if has_parent[c] {
                return Err(ClaimedTwice { child: c as u32, second_parent: i as u32 });
            }
            has_parent[c] = true;
        }
    }
    if let Some(orphan) = has_parent.iter().position(|&p| !p) {
        return Err(Orphan { node: orphan as u32 });
    }
    if leaves != leaf_count {
        return Err(LeafCountMismatch { declared: leaf_count, actual: leaves });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use TreeError::*;

    // root 0 → [1, 2]; node 1 → [3, 4]; leaves 2, 3, 4.
    const START: [u32; 5] = [1, 3, 0, 0, 0];
    const COUNT: [u16; 5] = [2, 2, 0, 0, 0];

    #[test]
    fn accepts_a_well_formed_tree() {
        assert_eq!(check_tree(&START, &COUNT, 3), Ok(()));
    }

    #[test]
    fn rejects_each_invariant() {
        assert_eq!(check_tree(&[], &[], 0), Err(Empty));
        assert_eq!(check_tree(&[1, 1, 0, 0, 0], &COUNT, 3), Err(ChildStartNotAfterParent { node: 1, child_start: 1 }));
        assert_eq!(check_tree(&[1, 4, 0, 0, 0], &COUNT, 3), Err(ChildRangeOutOfBounds { node: 1, end: 6, node_count: 5 }));
        assert_eq!(check_tree(&[1, 2, 0, 0, 0], &COUNT, 3), Err(ClaimedTwice { child: 2, second_parent: 1 }));
        assert_eq!(check_tree(&[1, 3, 0, 0, 0], &[1, 2, 0, 0, 0], 3), Err(Orphan { node: 2 }));
        assert_eq!(check_tree(&START, &COUNT, 4), Err(LeafCountMismatch { declared: 4, actual: 3 }));
    }
}
