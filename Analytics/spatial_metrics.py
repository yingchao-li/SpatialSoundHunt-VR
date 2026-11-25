"""Spatial exploration metrics on the Unity X/Z (floor) plane.

* Occupancy / heatmap: seconds spent per grid cell. Each *valid* interval contributes its duration at
  the interval's midpoint (gaps and teleports contribute nothing).
* Coverage: fraction of arena cells touched by the head path (samples + densified valid segments).
  It is a fraction of the arena RECTANGLE - furniture/partitions are not subtracted, and it is only
  computed when the arena bounds are known (config). Without bounds it is reported as N/A instead of
  passing the trajectory's bounding box off as the environment.
* Zone dwell: seconds inside circular zones (start pad, target proximity radius, ...).
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple

import numpy as np
import pandas as pd

from config import AnalysisConfig, Arena


def _grid_edges(table: pd.DataFrame, cfg: AnalysisConfig) -> Tuple[np.ndarray, np.ndarray, bool]:
    cell = cfg.grid_cell_m
    if cfg.arena is not None:
        a: Arena = cfg.arena
        nx = max(1, int(round(a.width / cell)))
        nz = max(1, int(round(a.depth / cell)))
        return (a.x_min + cell * np.arange(nx + 1), a.z_min + cell * np.arange(nz + 1), True)

    x = table["x"].to_numpy(dtype=float)
    z = table["z"].to_numpy(dtype=float)
    x0, x1 = np.floor(x.min() / cell) * cell - cell, np.ceil(x.max() / cell) * cell + cell
    z0, z1 = np.floor(z.min() / cell) * cell - cell, np.ceil(z.max() / cell) * cell + cell
    return (np.arange(x0, x1 + cell * 0.5, cell), np.arange(z0, z1 + cell * 0.5, cell), False)


def _densify(x0, z0, x1, z1, step: float) -> Tuple[np.ndarray, np.ndarray]:
    """Points along each segment (end points included, start points excluded), spacing <= step."""
    length = np.hypot(x1 - x0, z1 - z0)
    n = np.maximum(1, np.ceil(length / step)).astype(int)
    seg = np.repeat(np.arange(len(n)), n)
    k = np.arange(n.sum()) - np.repeat(np.cumsum(n) - n, n)
    frac = (k + 1) / n[seg]
    return x0[seg] + (x1[seg] - x0[seg]) * frac, z0[seg] + (z1[seg] - z0[seg]) * frac


def compute_spatial(table: pd.DataFrame, cfg: Optional[AnalysisConfig] = None) -> Dict[str, Any]:
    cfg = cfg or AnalysisConfig()
    n = len(table)
    x = table["x"].to_numpy(dtype=float)
    z = table["z"].to_numpy(dtype=float)
    t = table["t"].to_numpy(dtype=float)
    status = table["interval_status"].to_numpy(dtype=object)
    dt = table["dt"].to_numpy(dtype=float)

    x_edges, z_edges, arena_known = _grid_edges(table, cfg)
    nx, nz = len(x_edges) - 1, len(z_edges) - 1
    cell = cfg.grid_cell_m

    not_available: Dict[str, str] = {}
    result: Dict[str, Any] = {
        "cell_size_m": cell,
        "arena_known": arena_known,
        "arena": ({"x_min": cfg.arena.x_min, "x_max": cfg.arena.x_max, "z_min": cfg.arena.z_min, "z_max": cfg.arena.z_max}
                  if cfg.arena is not None else None),
        "grid_shape_z_x": [nz, nx],
    }

    # ---- occupancy ------------------------------------------------------------------------
    occupancy = np.zeros((nz, nx), dtype=float)
    valid = (status == "valid") if n else np.zeros(0, dtype=bool)
    outside_s = 0.0
    if n >= 2 and valid.any():
        xm = (x[1:] + x[:-1]) / 2.0
        zm = (z[1:] + z[:-1]) / 2.0
        use = valid[1:]
        w = dt[1:][use]
        occupancy, _, _ = np.histogram2d(zm[use], xm[use], bins=[z_edges, x_edges], weights=w)
        outside_s = float(w.sum() - occupancy.sum())
    result["occupancy_total_s"] = float(occupancy.sum())
    result["occupancy_outside_arena_s"] = max(0.0, outside_s)

    # ---- coverage -------------------------------------------------------------------------
    if arena_known:
        px, pz = x.copy(), z.copy()
        if n >= 2 and valid[1:].any():
            seg = valid[1:]
            dx, dz = _densify(x[:-1][seg], z[:-1][seg], x[1:][seg], z[1:][seg], cell / 2.0)
            px = np.concatenate([px, dx])
            pz = np.concatenate([pz, dz])
        ix = np.floor((px - x_edges[0]) / cell).astype(int)
        iz = np.floor((pz - z_edges[0]) / cell).astype(int)
        inside = (ix >= 0) & (ix < nx) & (iz >= 0) & (iz < nz)
        visited = np.unique(iz[inside] * nx + ix[inside])
        total = nx * nz
        result["coverage"] = {
            "visited_cells": int(len(visited)),
            "total_cells": int(total),
            "fraction": float(len(visited) / total),
            "definition": ("share of %.2f m grid cells of the arena rectangle touched by the head path; "
                           "obstacles are not subtracted, so 100%% is not reachable" % cell),
        }
        result["samples_outside_arena"] = int((~inside[:n]).sum())
    else:
        result["coverage"] = None
        not_available["spatial.coverage"] = (
            "arena bounds are not configured (config 'arena' is null); the trajectory's bounding box is not "
            "the environment, so a coverage fraction would be meaningless")

    # ---- zones ----------------------------------------------------------------------------
    zones: List[Dict[str, Any]] = []
    valid_time = float(dt[valid].sum()) if n >= 2 and valid.any() else 0.0
    for zone in cfg.zones:
        inside_sample = np.hypot(x - zone.x, z - zone.z) <= zone.radius
        entries = int(np.sum(inside_sample[1:] & ~inside_sample[:-1])) + (1 if n and inside_sample[0] else 0)
        first_entry = float(t[np.argmax(inside_sample)]) if inside_sample.any() else None
        dwell = 0.0
        if n >= 2 and valid[1:].any():
            xm = (x[1:] + x[:-1]) / 2.0
            zm = (z[1:] + z[:-1]) / 2.0
            in_mid = np.hypot(xm - zone.x, zm - zone.z) <= zone.radius
            dwell = float(dt[1:][valid[1:] & in_mid].sum())
        zones.append({
            "name": zone.name, "kind": zone.kind, "x": zone.x, "z": zone.z, "radius_m": zone.radius,
            "dwell_time_s": dwell,
            "fraction_of_valid_time": (dwell / valid_time) if valid_time > 0 else None,
            "entries": entries,
            "first_entry_t_s": first_entry,
        })
    result["zones"] = zones

    result["_heatmap"] = {  # large arrays; written to a data file, not to summary.json
        "x_edges": x_edges, "z_edges": z_edges, "occupancy_s": occupancy,
    }
    result["not_available"] = not_available
    return result
