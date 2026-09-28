//! Source-frame conversion into Unity's RUF frame: a port of `GsplatUtils.AxisSigns`/`ShSign` and
//! of the quaternion sign rule in `GsplatAssetSpark.LoadFromPlyStream`, so a `.gsd` lands in the
//! same frame the `.ply` importer produces.

use glam::{Quat, Vec3};
use std::str::FromStr;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SourceFrame {
    Ldb,
    Rdb,
    Lub,
    Rub,
    Ldf,
    Rdf,
    Luf,
    Ruf,
}

impl FromStr for SourceFrame {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        use SourceFrame::*;
        Ok(match s.to_ascii_uppercase().as_str() {
            "LDB" => Ldb,
            "RDB" => Rdb,
            "LUB" => Lub,
            "RUB" => Rub,
            "LDF" => Ldf,
            "RDF" => Rdf,
            "LUF" => Luf,
            "RUF" => Ruf,
            _ => return Err(format!("unknown source frame '{s}' (expected LDB RDB LUB RUB LDF RDF LUF RUF)")),
        })
    }
}

/// −1 on each axis the source frame points opposite to Unity (Left, Down, Back).
pub fn axis_signs(frame: SourceFrame) -> Vec3 {
    use SourceFrame::*;
    let left = matches!(frame, Ldb | Lub | Ldf | Luf);
    let down = matches!(frame, Ldb | Rdb | Ldf | Rdf);
    let back = matches!(frame, Ldb | Rdb | Lub | Rub);
    let sign = |flip: bool| if flip { -1.0 } else { 1.0 };
    Vec3::new(sign(left), sign(down), sign(back))
}

pub fn convert_center(frame: SourceFrame, center: Vec3) -> Vec3 {
    center * axis_signs(frame)
}

/// Each imaginary component takes the product of the other two axis signs, as the C# importer
/// does (`rotXSign = posYSign * posZSign`, …); the real part is untouched.
pub fn convert_quat(frame: SourceFrame, q: Quat) -> Quat {
    let s = axis_signs(frame);
    Quat::from_xyzw(q.x * s.y * s.z, q.y * s.x * s.z, q.z * s.x * s.y, q.w)
}

/// Sign for SH band `band`, band-local coefficient `k` (0..2·band+1).
pub fn sh_sign(frame: SourceFrame, band: usize, k: usize) -> f32 {
    let s = axis_signs(frame);
    let mut sign = 1.0;
    if s.x < 0.0 {
        sign *= sh_sign_x(band, k);
    }
    if s.y < 0.0 {
        sign *= if k < band { -1.0 } else { 1.0 };
    }
    if s.z < 0.0 {
        sign *= if k & 1 == 1 { -1.0 } else { 1.0 };
    }
    sign
}

// Real SH under an X flip (φ → π−φ): m > 0 → (−1)^(k−l), m = 0 → 1, m < 0 → (−1)^(l−k+1).
fn sh_sign_x(l: usize, k: usize) -> f32 {
    if k == l {
        return 1.0;
    }
    let power = if k > l { k - l } else { l - k + 1 };
    if power & 1 == 1 { -1.0 } else { 1.0 }
}

/// Applies `sh_sign` to one band stored coefficient-major, channel-minor (`values[k*3 + c]`).
pub fn convert_sh(frame: SourceFrame, band: usize, values: &mut [f32]) {
    for k in 0..(2 * band + 1) {
        let sign = sh_sign(frame, band, k);
        for c in 0..3 {
            values[k * 3 + c] *= sign;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_case_insensitively_and_rejects_unknown_frames() {
        assert_eq!("rub".parse::<SourceFrame>(), Ok(SourceFrame::Rub));
        assert!("xyz".parse::<SourceFrame>().is_err());
    }

    #[test]
    fn rub_flips_z_only() {
        assert_eq!(convert_center(SourceFrame::Rub, Vec3::new(1.0, 2.0, 3.0)), Vec3::new(1.0, 2.0, -3.0));
        assert_eq!(axis_signs(SourceFrame::Ruf), Vec3::ONE);
    }

    #[test]
    fn rub_quat_negates_x_and_y() {
        let q = convert_quat(SourceFrame::Rub, Quat::from_xyzw(0.1, 0.2, 0.3, 0.9));
        assert_eq!(q, Quat::from_xyzw(-0.1, -0.2, 0.3, 0.9));
    }

    #[test]
    fn rub_negates_odd_coefficients_of_every_band() {
        let signs = |band| (0..2 * band + 1).map(|k| sh_sign(SourceFrame::Rub, band, k)).collect::<Vec<_>>();
        assert_eq!(signs(1), [1.0, -1.0, 1.0]);
        assert_eq!(signs(2), [1.0, -1.0, 1.0, -1.0, 1.0]);
    }

    #[test]
    fn full_inversion_negates_odd_bands_only() {
        // LDB flips all three axes: point inversion. Real SH of degree l pick up (−1)^l.
        for (band, expected) in [(1, -1.0), (2, 1.0), (3, -1.0)] {
            for k in 0..2 * band + 1 {
                assert_eq!(sh_sign(SourceFrame::Ldb, band, k), expected, "band {band} k {k}");
            }
        }
    }

    #[test]
    fn ruf_leaves_sh_untouched() {
        let mut v: Vec<f32> = (0..21).map(|i| i as f32).collect();
        let before = v.clone();
        convert_sh(SourceFrame::Ruf, 3, &mut v);
        assert_eq!(v, before);
    }
}
