"""Load and validate a SessionRecorder CSV.

The column layout is the one written by ``Assets/Scripts/SpatialSoundHunt/SessionRecorder.cs``::

    timestamp_utc,session_time_s,session_id,condition,state,record_type,event,
    current_target,interacted_target,hmd_px,hmd_py,hmd_pz,hmd_qx,hmd_qy,hmd_qz,hmd_qw,details

* ``record_type`` is ``Pose`` (periodic head pose, ~10 Hz) or ``Event`` (SessionStart, TargetActivated, ...).
* Positions are Unity world metres (X east, Y up, Z north); rotations are Unity quaternions
  in X, Y, Z, W order.
* Numbers are written with the invariant culture (dot decimal separator).

Nothing is invented here: unusable rows are dropped and *counted* in ``SessionData.quality``.
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np
import pandas as pd

from config import AnalysisConfig

HEADER = [
    "timestamp_utc", "session_time_s", "session_id", "condition", "state", "record_type", "event",
    "current_target", "interacted_target",
    "hmd_px", "hmd_py", "hmd_pz", "hmd_qx", "hmd_qy", "hmd_qz", "hmd_qw", "details",
]
POSE_COLUMNS = ["hmd_px", "hmd_py", "hmd_pz", "hmd_qx", "hmd_qy", "hmd_qz", "hmd_qw"]
REQUIRED_COLUMNS = ["session_time_s", "record_type", "event"] + POSE_COLUMNS
OPTIONAL_COLUMNS = [c for c in HEADER if c not in REQUIRED_COLUMNS]


class DataValidationError(ValueError):
    """The CSV cannot be analysed (missing file, empty, wrong columns, no usable poses)."""


@dataclass
class SessionData:
    path: str
    session_id: str
    poses: pd.DataFrame            # t, x, y, z, qx, qy, qz, qw  (cleaned, sorted, unique times)
    events: pd.DataFrame           # t, event, current_target, interacted_target, details, condition, state
    conditions: List[str]          # distinct non-empty condition values, first-seen order
    quality: Dict[str, Any] = field(default_factory=dict)
    warnings: List[str] = field(default_factory=list)
    first_timestamp_utc: Optional[str] = None
    last_timestamp_utc: Optional[str] = None


def file_sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _read_raw(path: Path):
    bad_lines: List[Any] = []

    def _on_bad_line(line):  # called for rows with too many fields; returning None skips the row
        bad_lines.append(line)
        return None

    try:
        df = pd.read_csv(
            path, dtype=str, keep_default_na=False, na_values=[], encoding="utf-8-sig",
            engine="python", on_bad_lines=_on_bad_line,
        )
    except pd.errors.EmptyDataError:
        raise DataValidationError("CSV is empty (no header row): %s" % path)
    except UnicodeDecodeError as exc:
        raise DataValidationError("CSV is not valid UTF-8 text: %s" % exc)
    except pd.errors.ParserError as exc:
        raise DataValidationError("CSV could not be parsed: %s" % exc)

    df = df.fillna("")
    df.columns = [str(c).strip() for c in df.columns]
    return df, len(bad_lines)


def _numeric(series: pd.Series) -> pd.Series:
    out = pd.to_numeric(series.astype(str).str.strip(), errors="coerce")
    return out.replace([np.inf, -np.inf], np.nan)


def make_hemisphere_continuous(q: np.ndarray) -> np.ndarray:
    """Flip q -> -q where needed so consecutive quaternions have a non-negative dot product.

    q and -q describe the same orientation. Without this, plotting quaternion components (or taking
    component differences) shows artificial jumps. The metrics themselves never depend on the sign.
    """
    if len(q) < 2:
        return q.copy()
    dots = np.einsum("ij,ij->i", q[:-1], q[1:])
    signs = np.where(dots < 0.0, -1.0, 1.0)
    cumulative = np.concatenate([[1.0], np.cumprod(signs)])
    return q * cumulative[:, None]


def load_session(path: str, cfg: Optional[AnalysisConfig] = None) -> SessionData:
    cfg = cfg or AnalysisConfig()
    p = Path(path)
    if not p.is_file():
        raise DataValidationError("Input CSV not found: %s" % path)
    if p.stat().st_size == 0:
        raise DataValidationError("Input CSV is empty (0 bytes): %s" % path)

    df, bad_line_count = _read_raw(p)

    missing = [c for c in REQUIRED_COLUMNS if c not in df.columns]
    if missing:
        raise DataValidationError(
            "Missing required column(s): %s. Columns found: %s" % (", ".join(missing), ", ".join(df.columns))
        )
    if len(df) == 0:
        raise DataValidationError("CSV has a header but no data rows: %s" % path)

    warnings: List[str] = []
    absent_optional = [c for c in OPTIONAL_COLUMNS if c not in df.columns]
    for column in absent_optional:
        df[column] = ""
    if absent_optional:
        warnings.append("Optional column(s) absent, related metrics will be N/A: " + ", ".join(absent_optional))
    if bad_line_count:
        warnings.append("%d malformed CSV row(s) (too many fields) were skipped." % bad_line_count)

    quality: Dict[str, Any] = {
        "rows_total": int(len(df)),
        "malformed_rows_skipped": int(bad_line_count),
    }

    # --- time axis -----------------------------------------------------------------------
    t_all = _numeric(df["session_time_s"])
    time_source = "session_time_s"
    if t_all.notna().sum() == 0 and (df["timestamp_utc"].astype(str).str.strip() != "").any():
        ts = pd.to_datetime(df["timestamp_utc"], utc=True, errors="coerce", format="ISO8601")
        if ts.notna().any():
            t_all = (ts - ts.min()).dt.total_seconds()
            time_source = "timestamp_utc (session_time_s unusable)"
            warnings.append("session_time_s is unusable; time was derived from timestamp_utc.")
    quality["time_source"] = time_source

    # --- session id & condition ------------------------------------------------------------
    ids = [s for s in df["session_id"].astype(str).str.strip().tolist() if s]
    unique_ids = list(dict.fromkeys(ids))
    if unique_ids:
        session_id = unique_ids[0]
        if len(unique_ids) > 1:
            warnings.append("CSV contains %d different session_id values; using the first (%s)."
                            % (len(unique_ids), session_id))
    else:
        session_id = p.stem
        warnings.append("No session_id in CSV; using the file name '%s'." % session_id)

    cond_values = [s for s in df["condition"].astype(str).str.strip().tolist() if s]
    conditions = list(dict.fromkeys(cond_values))

    ts_clean = [s for s in df["timestamp_utc"].astype(str).str.strip().tolist() if s]
    first_ts = ts_clean[0] if ts_clean else None
    last_ts = ts_clean[-1] if ts_clean else None

    # --- events ----------------------------------------------------------------------------
    ev_mask = df["event"].astype(str).str.strip() != ""
    events = pd.DataFrame({
        "t": t_all[ev_mask].to_numpy(dtype=float),
        "event": df.loc[ev_mask, "event"].astype(str).str.strip().to_numpy(),
        "current_target": df.loc[ev_mask, "current_target"].astype(str).str.strip().to_numpy(),
        "interacted_target": df.loc[ev_mask, "interacted_target"].astype(str).str.strip().to_numpy(),
        "details": df.loc[ev_mask, "details"].astype(str).to_numpy(),
        "condition": df.loc[ev_mask, "condition"].astype(str).str.strip().to_numpy(),
        "state": df.loc[ev_mask, "state"].astype(str).str.strip().to_numpy(),
    })
    n_events_missing_time = int(events["t"].isna().sum())
    if n_events_missing_time:
        warnings.append("%d event row(s) without a usable time were ignored." % n_events_missing_time)
        events = events[events["t"].notna()]
    events = events.sort_values("t", kind="stable").reset_index(drop=True)
    quality["event_rows"] = int(len(events))

    # --- poses -----------------------------------------------------------------------------
    record_type = df["record_type"].astype(str).str.strip().str.lower()
    pose_mask = record_type == "pose"
    if not pose_mask.any():
        pose_mask = pd.Series(True, index=df.index)
        warnings.append("No record_type=Pose rows; every row that carries a head pose is used instead.")
    quality["pose_rows_raw"] = int(pose_mask.sum())

    poses = pd.DataFrame({
        "t": t_all[pose_mask].to_numpy(dtype=float),
        "x": _numeric(df.loc[pose_mask, "hmd_px"]).to_numpy(),
        "y": _numeric(df.loc[pose_mask, "hmd_py"]).to_numpy(),
        "z": _numeric(df.loc[pose_mask, "hmd_pz"]).to_numpy(),
        "qx": _numeric(df.loc[pose_mask, "hmd_qx"]).to_numpy(),
        "qy": _numeric(df.loc[pose_mask, "hmd_qy"]).to_numpy(),
        "qz": _numeric(df.loc[pose_mask, "hmd_qz"]).to_numpy(),
        "qw": _numeric(df.loc[pose_mask, "hmd_qw"]).to_numpy(),
    })

    # rows that have no / unparsable pose (SessionStart before the camera exists, blank cells, NaN ...)
    finite = np.isfinite(poses.to_numpy(dtype=float)).all(axis=1)
    dropped_missing = int((~finite).sum())
    poses = poses[finite].reset_index(drop=True)
    quality["pose_rows_dropped_missing_values"] = dropped_missing

    # invalid quaternions
    q = poses[["qx", "qy", "qz", "qw"]].to_numpy(dtype=float)
    norm = np.linalg.norm(q, axis=1)
    valid_q = norm >= cfg.min_quaternion_norm
    quality["pose_rows_dropped_invalid_quaternion"] = int((~valid_q).sum())
    poses = poses[valid_q].reset_index(drop=True)
    q = q[valid_q]
    norm = norm[valid_q]
    quality["quaternion_norm_out_of_tolerance"] = int((np.abs(norm - 1.0) > cfg.quaternion_norm_tolerance).sum())
    q = q / norm[:, None]

    # ordering and duplicates
    t = poses["t"].to_numpy(dtype=float)
    out_of_order = int((np.diff(t) < 0).sum()) if len(t) > 1 else 0
    if out_of_order:
        order = np.argsort(t, kind="stable")
        poses = poses.iloc[order].reset_index(drop=True)
        q = q[order]
        t = poses["t"].to_numpy(dtype=float)
        warnings.append("%d pose row(s) were out of time order and have been sorted." % out_of_order)
    quality["pose_rows_out_of_order"] = out_of_order

    if len(t) > 1:
        # a row is a duplicate when the NEXT row is (almost) at the same time -> the last of a cluster is kept
        duplicate = np.concatenate([np.diff(t) <= cfg.duplicate_time_tolerance_s, [False]])
    else:
        duplicate = np.zeros(len(t), dtype=bool)
    quality["pose_rows_duplicate_timestamps_removed"] = int(duplicate.sum())
    if duplicate.any():
        warnings.append("%d pose row(s) with duplicate timestamps were removed (last one kept)." % int(duplicate.sum()))
        poses = poses[~duplicate].reset_index(drop=True)
        q = q[~duplicate]

    if len(poses) == 0:
        raise DataValidationError(
            "No usable pose samples: %d Pose row(s) in the file, all unusable (missing/invalid values)."
            % quality["pose_rows_raw"]
        )

    q = make_hemisphere_continuous(q)
    poses[["qx", "qy", "qz", "qw"]] = q

    quality["pose_samples_used"] = int(len(poses))
    if len(poses) > 1:
        dt = np.diff(poses["t"].to_numpy(dtype=float))
        quality["median_dt_s"] = float(np.median(dt))
        quality["estimated_sample_rate_hz"] = float(1.0 / np.median(dt)) if np.median(dt) > 0 else None
    else:
        quality["median_dt_s"] = None
        quality["estimated_sample_rate_hz"] = None

    return SessionData(
        path=str(p), session_id=session_id, poses=poses, events=events, conditions=conditions,
        quality=quality, warnings=warnings, first_timestamp_utc=first_ts, last_timestamp_utc=last_ts,
    )
