"""Linear and rotational head-motion metrics from the pose samples.

Interval i is the step between sample i-1 and sample i. Every interval is classified:

* ``valid``          - used for all statistics
* ``gap``            - longer than ``max_gap_s`` (samples are missing); excluded, reported
* ``discontinuity``  - physically implausible speed (teleport / tracking jump); excluded, reported

Rotation never uses component differences of the quaternions: the angle of the relative rotation
``q0^-1 * q1`` is computed as ``2*atan2(|vec|, |w|)``, which is independent of the q / -q sign and
numerically stable for tiny angles.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, Optional

import numpy as np
import pandas as pd

from config import AnalysisConfig


@dataclass
class MotionResult:
    table: pd.DataFrame
    linear: Dict[str, Any]
    rotational: Dict[str, Any]
    not_available: Dict[str, str]


# ---------------------------------------------------------------------------------------------
# quaternion helpers (Unity order: x, y, z, w)
# ---------------------------------------------------------------------------------------------
def relative_angle_rad(q0: np.ndarray, q1: np.ndarray) -> np.ndarray:
    """Smallest rotation angle (radians, 0..pi) between row-wise unit quaternions q0[i] and q1[i]."""
    x0, y0, z0, w0 = q0[:, 0], q0[:, 1], q0[:, 2], q0[:, 3]
    x1, y1, z1, w1 = q1[:, 0], q1[:, 1], q1[:, 2], q1[:, 3]
    w = w0 * w1 + x0 * x1 + y0 * y1 + z0 * z1
    x = w0 * x1 - x0 * w1 - y0 * z1 + z0 * y1
    y = w0 * y1 - y0 * w1 - z0 * x1 + x0 * z1
    z = w0 * z1 - z0 * w1 - x0 * y1 + y0 * x1
    return 2.0 * np.arctan2(np.sqrt(x * x + y * y + z * z), np.abs(w))


def forward_vector(q: np.ndarray) -> np.ndarray:
    """Unity ``q * Vector3.forward`` for row-wise unit quaternions."""
    x, y, z, w = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    return np.stack([2.0 * (x * z + w * y), 2.0 * (y * z - w * x), 1.0 - 2.0 * (x * x + y * y)], axis=1)


def _wrap_deg(delta: np.ndarray) -> np.ndarray:
    return (delta + 180.0) % 360.0 - 180.0


def _stat(values: np.ndarray, fn) -> Optional[float]:
    return float(fn(values)) if len(values) else None


# ---------------------------------------------------------------------------------------------
def compute_motion(poses: pd.DataFrame, cfg: Optional[AnalysisConfig] = None) -> MotionResult:
    cfg = cfg or AnalysisConfig()
    n = len(poses)
    t = poses["t"].to_numpy(dtype=float)
    pos = poses[["x", "y", "z"]].to_numpy(dtype=float)
    q = poses[["qx", "qy", "qz", "qw"]].to_numpy(dtype=float)

    fwd = forward_vector(q)
    yaw = np.degrees(np.arctan2(fwd[:, 0], fwd[:, 2]))
    pitch = np.degrees(np.arcsin(np.clip(fwd[:, 1], -1.0, 1.0)))

    nan = np.full(n, np.nan)
    dt, step, step_xz, speed = nan.copy(), nan.copy(), nan.copy(), nan.copy()
    angle_deg, ang_speed = nan.copy(), nan.copy()
    status = np.array(["first"] + ["valid"] * (n - 1), dtype=object) if n else np.array([], dtype=object)

    if n >= 2:
        dt[1:] = np.diff(t)
        delta = np.diff(pos, axis=0)
        step[1:] = np.linalg.norm(delta, axis=1)
        step_xz[1:] = np.hypot(delta[:, 0], delta[:, 2])
        with np.errstate(divide="ignore", invalid="ignore"):
            speed[1:] = step[1:] / dt[1:]
        angle_deg[1:] = np.degrees(relative_angle_rad(q[:-1], q[1:]))
        with np.errstate(divide="ignore", invalid="ignore"):
            ang_speed[1:] = angle_deg[1:] / dt[1:]

        gap = dt[1:] > cfg.max_gap_s
        jump = (speed[1:] > cfg.discontinuity_speed_m_s) | (ang_speed[1:] > cfg.discontinuity_angular_deg_s)
        invalid_dt = ~(dt[1:] > 0)
        interval_status = np.where(gap | invalid_dt, "gap", np.where(jump, "discontinuity", "valid"))
        status[1:] = interval_status

    valid = status == "valid"
    # statistics are only defined on valid intervals
    speed_valid = np.where(valid, speed, np.nan)
    ang_speed_valid = np.where(valid, ang_speed, np.nan)

    step_for_cum = np.where(valid, step, 0.0)
    angle_for_cum = np.where(valid, angle_deg, 0.0)
    cum_path = np.cumsum(np.nan_to_num(step_for_cum))
    cum_angle = np.cumsum(np.nan_to_num(angle_for_cum))

    # per-interval yaw / pitch changes (wrapped yaw)
    d_yaw, d_pitch = nan.copy(), nan.copy()
    if n >= 2:
        d_yaw[1:] = _wrap_deg(np.diff(yaw))
        d_pitch[1:] = np.diff(pitch)

    table = pd.DataFrame({
        "t": t, "x": pos[:, 0], "y": pos[:, 1], "z": pos[:, 2],
        "qx": q[:, 0], "qy": q[:, 1], "qz": q[:, 2], "qw": q[:, 3],
        "dt": dt, "step_m": step, "speed_m_s": speed_valid,
        "angle_deg": angle_deg, "angular_speed_deg_s": ang_speed_valid,
        "yaw_deg": yaw, "pitch_deg": pitch,
        "interval_status": status,
        "cum_path_m": cum_path, "cum_angle_deg": cum_angle,
        "moving": np.where(valid & (speed >= cfg.stationary_speed_m_s), True, False),
    })

    na: Dict[str, str] = {}
    linear: Dict[str, Any] = {
        "stationary_speed_threshold_m_s": cfg.stationary_speed_m_s,
        "max_gap_s": cfg.max_gap_s,
        "discontinuity_speed_threshold_m_s": cfg.discontinuity_speed_m_s,
        "n_samples": int(n),
    }
    rot: Dict[str, Any] = {
        "discontinuity_angular_threshold_deg_s": cfg.discontinuity_angular_deg_s,
    }

    if n < 2:
        reason = "needs at least 2 valid pose samples (found %d)" % n
        for key in ("path_length_m", "path_length_xz_m", "net_displacement_m", "valid_time_s", "mean_speed_m_s",
                    "median_speed_m_s", "p95_speed_m_s", "max_speed_m_s", "movement_duration_s",
                    "stationary_duration_s", "stationary_fraction"):
            linear[key] = None
            na["linear." + key] = reason
        for key in ("total_angular_movement_deg", "net_angular_displacement_deg", "mean_angular_speed_deg_s",
                    "median_angular_speed_deg_s", "p95_angular_speed_deg_s", "max_angular_speed_deg_s",
                    "total_yaw_movement_deg", "total_pitch_movement_deg"):
            rot[key] = None
            na["rotational." + key] = reason
        linear.update({"n_intervals": 0, "n_valid_intervals": 0, "n_gap_intervals": 0,
                       "gap_total_s": 0.0, "n_discontinuities": 0,
                       "excluded_distance_m": 0.0, "span_s": 0.0})
        rot.update({"excluded_angle_deg": 0.0})
        return MotionResult(table=table, linear=linear, rotational=rot, not_available=na)

    sv = speed[valid]
    av = ang_speed[valid]
    dtv = dt[valid]
    valid_time = float(dtv.sum())
    path = float(step[valid].sum())
    path_xz = float(step_xz[valid].sum())

    gap_mask = status == "gap"
    jump_mask = status == "discontinuity"

    linear.update({
        "span_s": float(t[-1] - t[0]),
        "n_intervals": int(n - 1),
        "n_valid_intervals": int(valid.sum()),
        "n_gap_intervals": int(gap_mask.sum()),
        "gap_total_s": float(np.nansum(dt[gap_mask])) if gap_mask.any() else 0.0,
        "n_discontinuities": int(jump_mask.sum()),
        "excluded_distance_m": float(np.nansum(step[gap_mask | jump_mask])) if (gap_mask | jump_mask).any() else 0.0,
        "valid_time_s": valid_time,
        "path_length_m": path,
        "path_length_xz_m": path_xz,
        "net_displacement_m": float(np.linalg.norm(pos[-1] - pos[0])),
    })

    if valid_time > 0:
        moving = sv >= cfg.stationary_speed_m_s
        linear.update({
            "mean_speed_m_s": path / valid_time,
            "median_speed_m_s": _stat(sv, np.median),
            "p95_speed_m_s": _stat(sv, lambda a: np.percentile(a, 95)),
            "max_speed_m_s": _stat(sv, np.max),
            "max_speed_at_t_s": float(t[valid][int(np.argmax(sv))]) if len(sv) else None,
            "movement_duration_s": float(dtv[moving].sum()),
            "stationary_duration_s": float(dtv[~moving].sum()),
            "stationary_fraction": float(dtv[~moving].sum() / valid_time),
        })
    else:
        reason = "no valid intervals (all %d interval(s) are gaps or discontinuities)" % (n - 1)
        for key in ("mean_speed_m_s", "median_speed_m_s", "p95_speed_m_s", "max_speed_m_s",
                    "movement_duration_s", "stationary_duration_s", "stationary_fraction"):
            linear[key] = None
            na["linear." + key] = reason

    # --- rotation --------------------------------------------------------------------------
    net_angle = float(np.degrees(relative_angle_rad(q[:1], q[-1:]))[0])
    rot.update({
        "net_angular_displacement_deg": net_angle,
        "total_angular_movement_deg": float(angle_deg[valid].sum()),
        "total_yaw_movement_deg": float(np.abs(d_yaw[valid]).sum()),
        "total_pitch_movement_deg": float(np.abs(d_pitch[valid]).sum()),
        "excluded_angle_deg": float(np.nansum(angle_deg[gap_mask | jump_mask])) if (gap_mask | jump_mask).any() else 0.0,
    })
    if valid_time > 0:
        rot.update({
            "mean_angular_speed_deg_s": float(angle_deg[valid].sum() / valid_time),
            "median_angular_speed_deg_s": _stat(av, np.median),
            "p95_angular_speed_deg_s": _stat(av, lambda a: np.percentile(a, 95)),
            "max_angular_speed_deg_s": _stat(av, np.max),
            "max_angular_speed_at_t_s": float(t[valid][int(np.argmax(av))]) if len(av) else None,
        })
    else:
        for key in ("mean_angular_speed_deg_s", "median_angular_speed_deg_s", "p95_angular_speed_deg_s",
                    "max_angular_speed_deg_s"):
            rot[key] = None
            na["rotational." + key] = "no valid intervals"

    return MotionResult(table=table, linear=linear, rotational=rot, not_available=na)
