"""SYNTHETIC session CSVs for the unit tests.

!! Everything produced here is made up by scripted trajectories. It is used ONLY to test the analysis code
!! against known answers. Results computed from these files are never real experiment results.

The files use exactly the column layout and number formats of SessionRecorder.cs (header, F3 time,
F4 position, F5 quaternion, invariant culture), so the tests exercise the real parsing path.
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

HEADER = ("timestamp_utc,session_time_s,session_id,condition,state,record_type,event,current_target,interacted_target,"
          "hmd_px,hmd_py,hmd_pz,hmd_qx,hmd_qy,hmd_qz,hmd_qw,details")

BASE = datetime(2026, 11, 20, 12, 0, 0, tzinfo=timezone.utc)


def quat_yaw(deg: float) -> Tuple[float, float, float, float]:
    """Unity quaternion (x, y, z, w) for a rotation of `deg` about the Y axis."""
    half = math.radians(deg) / 2.0
    return (0.0, math.sin(half), 0.0, math.cos(half))


class SyntheticSession:
    def __init__(self, session_id: str = "SYNTH-0001", condition: str = "AudioOnly") -> None:
        self.session_id = session_id
        self.condition = condition
        self.rows: List[str] = []

    # -- low level -------------------------------------------------------------------------
    def _row(self, t: float, record_type: str, event: str = "", current: str = "", interacted: str = "", details: str = "",
             pos: Optional[Sequence[float]] = (0.0, 1.6, 0.0), quat: Optional[Sequence[float]] = (0.0, 0.0, 0.0, 1.0),
             state: str = "Exploring", condition: Optional[str] = None, raw_pose: Optional[Sequence[str]] = None) -> None:
        ts = (BASE + timedelta(seconds=t)).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"  # 7 fractional digits like DateTime "o"
        if raw_pose is not None:
            pose = ",".join(raw_pose)
        elif pos is None or quat is None:
            pose = ",,,,,,"
        else:
            pose = "%.4f,%.4f,%.4f,%.5f,%.5f,%.5f,%.5f" % (tuple(pos) + tuple(quat))
        if "," in details or '"' in details:
            details = '"' + details.replace('"', '""') + '"'
        self.rows.append("%s,%.3f,%s,%s,%s,%s,%s,%s,%s,%s,%s" % (
            ts, t, self.session_id, condition or self.condition, state, record_type, event, current, interacted, pose, details))

    def pose(self, t: float, pos=(0.0, 1.6, 0.0), quat=(0.0, 0.0, 0.0, 1.0), **kw) -> None:
        self._row(t, "Pose", pos=pos, quat=quat, **kw)

    def event(self, t: float, name: str, interacted: str = "", details: str = "", current: str = "", **kw) -> None:
        self._row(t, "Event", event=name, interacted=interacted, details=details, current=current, **kw)

    def write(self, path: Path, header: Optional[str] = None) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text((header or HEADER) + "\n" + "\n".join(self.rows) + "\n", encoding="utf-8")
        return path


def walk(sess: SyntheticSession, t0: float, t1: float, hz: float = 10.0, start=(0.0, 1.6, -7.2), velocity=(0.0, 0.0, 0.5),
         yaw0: float = 0.0, yaw_rate: float = 0.0) -> None:
    """Constant-velocity walk with constant yaw rate; one Pose row every 1/hz seconds (t0 .. t1 inclusive)."""
    n = int(round((t1 - t0) * hz))
    for i in range(n + 1):
        t = t0 + i / hz
        dt = t - t0
        pos = (start[0] + velocity[0] * dt, start[1] + velocity[1] * dt, start[2] + velocity[2] * dt)
        sess.pose(t, pos=pos, quat=quat_yaw(yaw0 + yaw_rate * dt))


def standard_session(session_id: str = "SYNTH-0001", condition: str = "AudioOnly", completed: bool = True) -> SyntheticSession:
    """30 s walk along +Z at 0.5 m/s (15 m) while turning 3 deg/s (90 deg), 3 targets, 1 wrong selection."""
    s = SyntheticSession(session_id, condition)
    s.event(0.0, "SessionStart", details="order=A>B>C", state="Ready", pos=(0.0, 1.6, -7.2))
    s.event(0.0, "StateChanged", details="Ready->Exploring")
    s.event(0.0, "TargetActivated", interacted="A", current="A", details="index=1/3")
    walk(s, 0.0, 30.0, yaw_rate=3.0)
    s.event(10.0, "TargetSelected", interacted="A", current="A", details="result=Correct;input=Controller;search_time_s=10.00")
    s.event(12.6, "TargetActivated", interacted="B", current="B", details="index=2/3")
    s.event(15.0, "TargetSelected", interacted="C", current="B", details="result=Wrong;input=Controller;expected=B")
    s.event(20.0, "TargetSelected", interacted="B", current="B", details="result=Correct;input=Controller;search_time_s=7.40")
    s.event(22.6, "TargetActivated", interacted="C", current="C", details="index=3/3")
    s.event(28.0, "TargetSelected", interacted="C", current="C", details="result=Correct;input=Controller;search_time_s=5.40")
    if completed:
        s.event(30.0, "ExperimentCompleted", details="total_time_s=30.00;wrong_selections=1", state="Completed")
        s.event(30.0, "SessionEnd", details="reason=Completed", state="Completed")
    else:
        s.event(30.0, "ExperimentAborted", details="state=Exploring")
        s.event(30.0, "SessionEnd", details="reason=Aborted")
    return s
