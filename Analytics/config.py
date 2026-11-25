"""Analysis configuration for the Spatial Sound Hunt 6-DoF analytics.

Every threshold that influences a number in the report lives here, is written into
summary.json (``config`` block) and can be overridden with ``--config my_config.json``.

The default lab layout (arena, start pad, target positions) is copied from the constants of
``Assets/Editor/SpatialSoundHuntBuilder.cs`` (room 20 x 18 m centred on the origin, start position
(0, 0, -7.2), targets A/B/C). The CSV itself does not contain the environment, so a different
scene needs a different config (or ``"arena": null`` to disable the arena-dependent metrics).
"""
from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional


@dataclass
class Arena:
    """Axis-aligned walkable rectangle in Unity world X/Z (metres). Obstacles are NOT subtracted."""
    x_min: float = -10.0
    x_max: float = 10.0
    z_min: float = -9.0
    z_max: float = 9.0

    @property
    def width(self) -> float:
        return self.x_max - self.x_min

    @property
    def depth(self) -> float:
        return self.z_max - self.z_min


@dataclass
class Zone:
    """Circular zone on the XZ plane (metres)."""
    name: str
    x: float
    z: float
    radius: float
    kind: str = "zone"  # "start" | "target" | "zone" (only used for labelling)


@dataclass
class TargetSpec:
    """Static position of a sound target, used only to draw markers (never to derive metrics)."""
    id: str
    x: float
    y: float
    z: float


def _default_zones() -> List[Zone]:
    proximity = 2.5  # SoundTarget.proximityRadius
    return [
        Zone("Start pad", 0.0, -7.2, 1.45, "start"),  # StartPadRing diameter 2.9 m
        Zone("Target A proximity", -7.4, 3.0, proximity, "target"),
        Zone("Target B proximity", 0.0, 7.2, proximity, "target"),
        Zone("Target C proximity", 7.6, 2.5, proximity, "target"),
    ]


def _default_targets() -> List[TargetSpec]:
    return [
        TargetSpec("A", -7.4, 1.0, 3.0),
        TargetSpec("B", 0.0, 0.9, 7.2),
        TargetSpec("C", 7.6, 1.9, 2.5),  # stands on the 1 m dais
    ]


@dataclass
class AnalysisConfig:
    # --- linear motion -------------------------------------------------------------------
    stationary_speed_m_s: float = 0.10
    """An interval counts as 'stationary' below this 3-D head speed."""

    max_gap_s: float = 1.0
    """Intervals longer than this are data gaps: excluded from every rate/length statistic."""

    discontinuity_speed_m_s: float = 8.0
    """Faster than any walking participant => teleport / tracking jump: excluded (and counted)."""

    discontinuity_angular_deg_s: float = 1000.0
    """Same for rotation (e.g. the debug 'Place Player' action turns the rig instantly)."""

    duplicate_time_tolerance_s: float = 1e-3
    """Pose rows closer in time than this are duplicates (the last one is kept)."""

    min_quaternion_norm: float = 1e-3
    quaternion_norm_tolerance: float = 0.01

    # --- spatial ---------------------------------------------------------------------------
    grid_cell_m: float = 0.5
    arena: Optional[Arena] = field(default_factory=Arena)
    zones: List[Zone] = field(default_factory=_default_zones)
    targets: List[TargetSpec] = field(default_factory=_default_targets)

    # --- report ----------------------------------------------------------------------------
    max_plot_points: int = 20000

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @staticmethod
    def from_dict(data: Dict[str, Any]) -> "AnalysisConfig":
        cfg = AnalysisConfig()
        scalar_keys = {
            "stationary_speed_m_s", "max_gap_s", "discontinuity_speed_m_s", "discontinuity_angular_deg_s",
            "duplicate_time_tolerance_s", "min_quaternion_norm", "quaternion_norm_tolerance",
            "grid_cell_m", "max_plot_points",
        }
        unknown = [k for k in data if k not in scalar_keys and k not in ("arena", "zones", "targets")]
        if unknown:
            raise ValueError("Unknown config key(s): " + ", ".join(sorted(unknown)))
        for key in scalar_keys:
            if key in data:
                setattr(cfg, key, type(getattr(cfg, key))(data[key]))
        if "arena" in data:
            cfg.arena = None if data["arena"] is None else Arena(**data["arena"])
        if "zones" in data:
            cfg.zones = [Zone(**z) for z in data["zones"]]
        if "targets" in data:
            cfg.targets = [TargetSpec(**t) for t in data["targets"]]
        cfg.validate()
        return cfg

    @staticmethod
    def from_json(path: str) -> "AnalysisConfig":
        with open(path, "r", encoding="utf-8") as handle:
            return AnalysisConfig.from_dict(json.load(handle))

    def validate(self) -> None:
        if self.grid_cell_m <= 0:
            raise ValueError("grid_cell_m must be > 0")
        if self.max_gap_s <= 0:
            raise ValueError("max_gap_s must be > 0")
        if self.stationary_speed_m_s < 0:
            raise ValueError("stationary_speed_m_s must be >= 0")
        if self.arena is not None and (self.arena.width <= 0 or self.arena.depth <= 0):
            raise ValueError("arena must have positive width and depth")


def default_config_path() -> Path:
    """Where a user-editable copy of the defaults would live (not required to exist)."""
    return Path(__file__).resolve().parent / "analysis_config.json"
