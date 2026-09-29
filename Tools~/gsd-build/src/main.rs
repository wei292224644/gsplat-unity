use anyhow::{anyhow, bail, Context, Result};
use gsd_build::format;
use gsd_build::frame::SourceFrame;
use gsd_build::pipeline::{build, BuildOptions};
use std::path::PathBuf;
use std::process::ExitCode;

const USAGE: &str = "usage: gsd-build <input.ply|.spz|.splat|.ksplat> [-o <output.gsd>] [--source RUB] [--max-sh 0..3] [--quick]";

fn main() -> ExitCode {
    match run(std::env::args().skip(1).collect()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("gsd-build: {e:#}");
            ExitCode::FAILURE
        }
    }
}

fn run(args: Vec<String>) -> Result<()> {
    let mut input: Option<PathBuf> = None;
    let mut output: Option<PathBuf> = None;
    let mut options = BuildOptions { source: SourceFrame::Rub, max_sh: None, quick: false };
    let mut it = args.into_iter();
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "-o" => output = Some(it.next().ok_or_else(|| anyhow!("-o needs a path\n{USAGE}"))?.into()),
            "--source" => {
                let frame = it.next().ok_or_else(|| anyhow!("--source needs a frame\n{USAGE}"))?;
                options.source = frame.parse().map_err(|e: String| anyhow!(e))?;
            }
            "--max-sh" => {
                let degree: u8 = it.next().ok_or_else(|| anyhow!("--max-sh needs 0..3\n{USAGE}"))?.parse()?;
                if degree > 3 {
                    bail!("--max-sh must be 0..3");
                }
                options.max_sh = Some(degree);
            }
            "--quick" => options.quick = true,
            "-h" | "--help" => {
                println!("{USAGE}");
                return Ok(());
            }
            other if other.starts_with('-') => bail!("unknown option {other}\n{USAGE}"),
            other => {
                if input.replace(other.into()).is_some() {
                    bail!("exactly one input file\n{USAGE}");
                }
            }
        }
    }
    let input = input.ok_or_else(|| anyhow!(USAGE))?;
    let output = output.unwrap_or_else(|| input.with_extension("gsd"));
    let bytes = std::fs::read(&input).with_context(|| format!("reading {}", input.display()))?;
    let name = input.file_name().and_then(|n| n.to_str()).unwrap_or("input");

    let (file, stats) = build(&bytes, name, &options)?;
    let encoded = format::write(&file);
    // The writer's own output must pass the reader's checks before it reaches disk.
    format::read(&encoded)?;
    std::fs::write(&output, &encoded).with_context(|| format!("writing {}", output.display()))?;

    println!(
        "{} -> {}: {} input, {} empty dropped, {} leaves, {} nodes, {} levels, SH {}, LoD {:.1}s, {:.1} MB",
        input.display(),
        output.display(),
        stats.input_splats,
        stats.dropped_empty,
        stats.leaves,
        stats.nodes,
        stats.levels,
        stats.sh_degree,
        stats.lod_seconds,
        encoded.len() as f64 / 1_048_576.0
    );
    Ok(())
}
