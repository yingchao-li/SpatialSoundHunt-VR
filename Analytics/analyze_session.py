#!/usr/bin/env python3
"""Analyse one SpatialSoundHunt session CSV and write an interactive HTML report.

    python analyze_session.py --input <session.csv> --output <output_directory>

Writes into <output_directory> (normally  <project>/reports/<session_id>/ ):

    report.html                  interactive dashboard (plotly.js embedded, opens from disk)
    summary.json                 every number of the report + definitions, thresholds, N/A reasons
    processed_poses.csv          per-sample speed / angular speed / yaw / pitch / interval status
    events.csv                   parsed Event rows
    heatmap_xz_occupancy.csv     non-empty heatmap cells (seconds per cell)
    source_session.csv           copy of the analysed CSV (provenance; --no-copy-input to skip)

Exit codes: 0 ok | 1 unexpected error | 2 bad input / config | 3 output folder already holds another session.
The LAST line printed on stdout is  REPORT_PATH=<absolute path of report.html>  on success.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))  # sibling modules, independent of the working directory

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from compare_sessions import build_comparison, load_summaries, render_fragment  # noqa: E402
from config import AnalysisConfig  # noqa: E402
from data_loader import DataValidationError, file_sha256, load_session  # noqa: E402
from experiment_metrics import compute_experiment  # noqa: E402
from motion_metrics import compute_motion  # noqa: E402
from report_generator import build_report  # noqa: E402
from spatial_metrics import compute_spatial  # noqa: E402

SCHEMA = "spatial-sound-hunt/session-summary/1"


class OutputConflictError(RuntimeError):
    """The output folder already contains the analysis of a different CSV."""


def json_safe(obj: Any) -> Any:
    """Recursively convert numpy / pandas values; NaN and Inf become null so the JSON is strictly valid."""
    if isinstance(obj, dict):
        return {str(k): json_safe(v) for k, v in obj.items() if not str(k).startswith("_")}
    if isinstance(obj, (list, tuple)):
        return [json_safe(v) for v in obj]
    if isinstance(obj, (np.floating, float)):
        value = float(obj)
        return value if math.isfinite(value) else None
    if isinstance(obj, (np.integer,)):
        return int(obj)
    if isinstance(obj, (np.bool_,)):
        return bool(obj)
    if isinstance(obj, np.ndarray):
        return json_safe(obj.tolist())
    if isinstance(obj, (pd.Timestamp, datetime)):
        return obj.isoformat()
    return obj


def _versions() -> Dict[str, str]:
    import plotly
    return {"python": sys.version.split()[0], "numpy": np.__version__, "pandas": pd.__version__, "plotly": plotly.__version__}


def _atomic_write_text(path: Path, text: str) -> None:
    tmp = path.with_name("." + path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


def analyze(input_csv: str, output_dir: Optional[str] = None, cfg: Optional[AnalysisConfig] = None, force: bool = False,
            copy_input: bool = True, plotlyjs: str = "inline", reports_root: Optional[str] = None) -> Dict[str, Any]:
    """Run the whole pipeline. Returns {'report': Path, 'summary_path': Path, 'summary': dict}."""
    cfg = cfg or AnalysisConfig()
    cfg.validate()
    input_path = Path(input_csv).expanduser().resolve()

    session = load_session(str(input_path), cfg)
    digest = file_sha256(str(input_path))

    if output_dir:
        out_dir = Path(output_dir).expanduser().resolve()
    else:
        out_dir = (Path(__file__).resolve().parent.parent / "reports" / session.session_id).resolve()

    existing = out_dir / "summary.json"
    if existing.is_file() and not force:
        try:
            previous = json.loads(existing.read_text(encoding="utf-8"))
            previous_sha = previous.get("input", {}).get("sha256")
        except (OSError, ValueError):
            previous_sha = None
        if previous_sha and previous_sha != digest:
            raise OutputConflictError(
                "Output folder %s already contains the analysis of a different CSV (session %s). "
                "Choose another --output or pass --force to overwrite." % (out_dir, previous.get("session", {}).get("session_id")))

    motion = compute_motion(session.poses, cfg)
    spatial = compute_spatial(motion.table, cfg)
    experiment = compute_experiment(session.events, session.conditions)

    not_available: Dict[str, str] = {}
    not_available.update(motion.not_available)
    not_available.update(spatial.get("not_available", {}))
    not_available.update(experiment.get("not_available", {}))

    lin, rot = motion.linear, motion.rotational
    kpis = {
        "experiment_duration_s": experiment.get("experiment_duration_s"),
        "total_distance_m": lin.get("path_length_m"),
        "mean_speed_m_s": lin.get("mean_speed_m_s"),
        "total_angular_movement_deg": rot.get("total_angular_movement_deg"),
        "targets_found": experiment.get("targets_found"),
        "targets_expected": experiment.get("targets_expected"),
    }

    warnings: List[str] = list(session.warnings)
    if lin.get("n_discontinuities"):
        warnings.append("%d implausible jump(s) (teleport / tracking loss, > %.1f m/s or > %.0f deg/s) were excluded from "
                        "distance, speed and angular statistics (%.1f m / %.0f deg)."
                        % (lin["n_discontinuities"], cfg.discontinuity_speed_m_s, cfg.discontinuity_angular_deg_s,
                           lin.get("excluded_distance_m", 0.0), rot.get("excluded_angle_deg", 0.0)))
    if lin.get("n_gap_intervals"):
        warnings.append("%d data gap(s) longer than %.1f s (%.1f s in total) were excluded from all rate and length statistics."
                        % (lin["n_gap_intervals"], cfg.max_gap_s, lin.get("gap_total_s", 0.0)))
    if experiment.get("status") != "Completed":
        warnings.append("This session did not reach ExperimentCompleted (status: %s); experiment metrics are partial." % experiment.get("status"))
    if experiment.get("wrong_count_mismatch"):
        warnings.append("Wrong-selection count differs between the ExperimentCompleted event and the TargetSelected events: %s"
                        % json.dumps(experiment["wrong_count_mismatch"]))

    summary: Dict[str, Any] = {
        "schema": SCHEMA,
        "generated_utc": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
        "tool": _versions(),
        "input": {"csv_path": str(input_path), "sha256": digest, "bytes": input_path.stat().st_size},
        "session": {
            "session_id": session.session_id, "started_utc": session.first_timestamp_utc, "ended_utc": session.last_timestamp_utc,
            "condition": experiment.get("condition"), "condition_label": experiment.get("condition_label"),
            "status": experiment.get("status"),
        },
        "kpis": kpis,
        "experiment": {k: v for k, v in experiment.items() if k != "not_available"},
        "linear": lin,
        "rotational": rot,
        "spatial": {k: v for k, v in spatial.items() if k not in ("_heatmap", "not_available")},
        "data_quality": session.quality,
        "config": cfg.to_dict(),
        "not_available": not_available,
        "warnings": warnings,
        "files": {"report": "report.html", "summary": "summary.json", "processed_poses": "processed_poses.csv",
                  "events": "events.csv", "heatmap": "heatmap_xz_occupancy.csv",
                  "source_csv": "source_session.csv" if copy_input else None},
    }
    summary = json_safe(summary)

    # ---- optional cross-session comparison (descriptive only) ---------------------------------------
    root = Path(reports_root).expanduser() if reports_root else out_dir.parent
    others = [s for s in load_summaries(root) if s.get("session", {}).get("session_id") != session.session_id]
    comparison = build_comparison(others + [summary])
    summary["condition_comparison"] = json_safe({k: comparison[k] for k in ("status", "note", "n_sessions_considered")})
    comparison_html = render_fragment(comparison)

    # ---- write (report last, so its existence means the run finished) -------------------------------------
    out_dir.mkdir(parents=True, exist_ok=True)
    motion.table.to_csv(out_dir / "processed_poses.csv", index=False, float_format="%.6f")
    session.events.to_csv(out_dir / "events.csv", index=False)
    hm = spatial["_heatmap"]
    xe, ze, occ = hm["x_edges"], hm["z_edges"], hm["occupancy_s"]
    iz, ix = np.nonzero(occ > 0)
    pd.DataFrame({"x_min": xe[ix], "x_max": xe[ix + 1], "z_min": ze[iz], "z_max": ze[iz + 1], "dwell_s": occ[iz, ix]}).to_csv(
        out_dir / "heatmap_xz_occupancy.csv", index=False, float_format="%.4f")
    if copy_input:
        target_copy = out_dir / "source_session.csv"
        if not (target_copy.exists() and target_copy.samefile(input_path)):
            shutil.copyfile(input_path, target_copy)

    _atomic_write_text(out_dir / "summary.json", json.dumps(summary, indent=2, ensure_ascii=False, allow_nan=False))
    report = build_report(session=session, motion=motion, spatial=spatial, experiment=experiment, summary=summary, cfg=cfg,
                          out_dir=out_dir, comparison_html=comparison_html, plotlyjs=plotlyjs)
    return {"report": report, "summary_path": out_dir / "summary.json", "summary": summary}


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="6-DoF analysis of one Spatial Sound Hunt session CSV -> interactive HTML report.")
    p.add_argument("--input", required=True, help="session CSV written by SessionRecorder")
    p.add_argument("--output", help="output directory (default: <project>/reports/<session_id>)")
    p.add_argument("--config", help="JSON file overriding the defaults in config.py")
    p.add_argument("--reports-root", help="folder with other <session_id>/summary.json files for the condition comparison "
                                          "(default: parent of --output)")
    p.add_argument("--force", action="store_true", help="overwrite the output folder even if it holds a different CSV's analysis")
    p.add_argument("--no-copy-input", action="store_true", help="do not copy the CSV into the output folder")
    p.add_argument("--plotlyjs", choices=["inline", "directory", "cdn"], default="inline",
                   help="inline = fully offline single file (default); directory = plotly.min.js next to the report; cdn = needs internet")
    p.add_argument("--stationary-threshold", type=float, help="m/s below which the head counts as stationary (default 0.10)")
    p.add_argument("--open", action="store_true", help="open the report in the default browser when done")
    return p


def main(argv: Optional[List[str]] = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except Exception:
            pass
    args = build_parser().parse_args(argv)
    try:
        cfg = AnalysisConfig.from_json(args.config) if args.config else AnalysisConfig()
        if args.stationary_threshold is not None:
            cfg.stationary_speed_m_s = args.stationary_threshold
        cfg.validate()
    except (OSError, ValueError, TypeError) as exc:  # unreadable / invalid config file
        print("ERROR: invalid configuration: %s" % exc, file=sys.stderr)
        return 2

    try:
        result = analyze(args.input, args.output, cfg, force=args.force, copy_input=not args.no_copy_input,
                         plotlyjs=args.plotlyjs, reports_root=args.reports_root)
    except DataValidationError as exc:
        print("ERROR: %s" % exc, file=sys.stderr)
        return 2
    except OutputConflictError as exc:
        print("ERROR: %s" % exc, file=sys.stderr)
        return 3
    except Exception as exc:  # noqa: BLE001 - last line of defence: report, never crash silently
        print("ERROR: unexpected failure: %s: %s" % (type(exc).__name__, exc), file=sys.stderr)
        traceback.print_exc(file=sys.stderr)
        return 1

    for w in result["summary"]["warnings"]:
        print("WARNING: %s" % w, file=sys.stderr)
    k = result["summary"]["kpis"]
    def _f(v, d=2):
        return "N/A" if v is None else ("%.*f" % (d, v))
    print("[analyze] session %s: duration=%s s, distance=%s m, mean speed=%s m/s, angular=%s deg, targets=%s/%s" % (
        result["summary"]["session"]["session_id"], _f(k["experiment_duration_s"], 1), _f(k["total_distance_m"]),
        _f(k["mean_speed_m_s"]), _f(k["total_angular_movement_deg"], 1),
        "N/A" if k["targets_found"] is None else k["targets_found"], "?" if k["targets_expected"] is None else k["targets_expected"]))
    print("SUMMARY_PATH=%s" % result["summary_path"])
    print("REPORT_PATH=%s" % result["report"])
    if args.open:
        import webbrowser
        webbrowser.open(result["report"].as_uri())
    return 0


if __name__ == "__main__":
    sys.exit(main())
