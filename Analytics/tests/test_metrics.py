"""Motion / spatial / experiment metric tests against known answers (SYNTHETIC data)."""
import numpy as np
import pandas as pd
import pytest

from config import AnalysisConfig
from data_loader import load_session
from experiment_metrics import compute_experiment, parse_details
from motion_metrics import compute_motion, relative_angle_rad
from spatial_metrics import compute_spatial
from synthetic import SyntheticSession, quat_yaw, standard_session, walk


def run(path, cfg=None):
    cfg = cfg or AnalysisConfig()
    session = load_session(str(path), cfg)
    motion = compute_motion(session.poses, cfg)
    return session, motion, compute_spatial(motion.table, cfg), cfg


def test_straight_walk_known_values(standard_csv):
    session, motion, spatial, cfg = run(standard_csv)
    lin, rot = motion.linear, motion.rotational
    assert lin["path_length_m"] == pytest.approx(15.0, abs=0.01)
    assert lin["mean_speed_m_s"] == pytest.approx(0.5, abs=0.005)
    assert lin["max_speed_m_s"] == pytest.approx(0.5, abs=0.02)
    assert lin["movement_duration_s"] == pytest.approx(30.0, abs=0.01)
    assert lin["stationary_duration_s"] == pytest.approx(0.0, abs=1e-9)
    assert lin["n_gap_intervals"] == 0 and lin["n_discontinuities"] == 0
    assert lin["net_displacement_m"] == pytest.approx(15.0, abs=0.01)
    assert rot["total_angular_movement_deg"] == pytest.approx(90.0, abs=0.05)
    assert rot["net_angular_displacement_deg"] == pytest.approx(90.0, abs=0.05)
    assert rot["mean_angular_speed_deg_s"] == pytest.approx(3.0, abs=0.01)
    assert rot["total_yaw_movement_deg"] == pytest.approx(90.0, abs=0.05)
    assert rot["total_pitch_movement_deg"] == pytest.approx(0.0, abs=0.01)
    assert motion.not_available == {}


def test_stationary_trajectory(tmp_path):
    s = SyntheticSession("STILL")
    walk(s, 0.0, 20.0, start=(0.0, 1.6, -7.2), velocity=(0.0, 0.0, 0.0))
    _, motion, spatial, _ = run(s.write(tmp_path / "still.csv"))
    lin, rot = motion.linear, motion.rotational
    assert lin["path_length_m"] == pytest.approx(0.0, abs=1e-9)
    assert lin["mean_speed_m_s"] == pytest.approx(0.0, abs=1e-9)
    assert lin["movement_duration_s"] == pytest.approx(0.0)
    assert lin["stationary_duration_s"] == pytest.approx(20.0, abs=0.01)
    assert lin["stationary_fraction"] == pytest.approx(1.0)
    assert rot["total_angular_movement_deg"] == pytest.approx(0.0, abs=1e-6)
    # a stationary head at the start pad spends the whole time there, in exactly one grid cell
    pad = [z for z in spatial["zones"] if z["name"] == "Start pad"][0]
    assert pad["dwell_time_s"] == pytest.approx(20.0, abs=0.01)
    assert spatial["coverage"]["visited_cells"] == 1


def test_stationary_threshold_is_configurable(tmp_path):
    s = SyntheticSession("SLOW")
    walk(s, 0.0, 20.0, velocity=(0.0, 0.0, 0.05))      # 0.05 m/s
    path = s.write(tmp_path / "slow.csv")
    _, default, _, _ = run(path)                                          # threshold 0.10 -> stationary
    _, strict, _, _ = run(path, AnalysisConfig(stationary_speed_m_s=0.01))  # threshold 0.01 -> moving
    assert default.linear["stationary_duration_s"] == pytest.approx(20.0, abs=0.01)
    assert strict.linear["movement_duration_s"] == pytest.approx(20.0, abs=0.01)


def test_quaternion_sign_flip_does_not_change_rotation():
    """q and -q are the same rotation: the angle must not depend on the sign (formula tested directly)."""
    base = np.array([quat_yaw(a) for a in np.linspace(0, 90, 50)])
    flipped = base.copy()
    flipped[1::2] *= -1.0
    for q in (base, flipped):
        t = np.arange(len(q)) * 0.1
        poses = pd.DataFrame({"t": t, "x": 0.0, "y": 1.6, "z": 0.0,
                              "qx": q[:, 0], "qy": q[:, 1], "qz": q[:, 2], "qw": q[:, 3]})
        rot = compute_motion(poses).rotational
        assert rot["total_angular_movement_deg"] == pytest.approx(90.0, abs=1e-6)
        assert rot["net_angular_displacement_deg"] == pytest.approx(90.0, abs=1e-6)
    assert relative_angle_rad(base[:1], -base[:1])[0] == pytest.approx(0.0, abs=1e-12)


def test_quaternion_sign_flip_through_the_csv(tmp_path):
    s = SyntheticSession("FLIP")
    for i in range(101):
        q = quat_yaw(40.0)                                     # constant orientation ...
        s.pose(i * 0.1, pos=(0.0, 1.6, 0.0), quat=tuple(-c if i % 2 else c for c in q))   # ... alternating sign
    _, motion, _, _ = run(s.write(tmp_path / "flip.csv"))
    assert motion.rotational["total_angular_movement_deg"] == pytest.approx(0.0, abs=1e-3)
    assert motion.rotational["max_angular_speed_deg_s"] == pytest.approx(0.0, abs=1e-2)


def test_small_angles_are_numerically_stable():
    q0 = np.array([quat_yaw(0.0)])
    q1 = np.array([quat_yaw(0.001)])
    assert np.degrees(relative_angle_rad(q0, q1))[0] == pytest.approx(0.001, rel=1e-6)


def test_single_sample_gives_na_not_errors(tmp_path):
    s = SyntheticSession("ONE")
    s.pose(0.0)
    session, motion, spatial, _ = run(s.write(tmp_path / "one.csv"))
    assert len(session.poses) == 1
    assert motion.linear["path_length_m"] is None
    assert motion.linear["mean_speed_m_s"] is None
    assert motion.rotational["total_angular_movement_deg"] is None
    assert "linear.path_length_m" in motion.not_available
    assert "at least 2" in motion.not_available["linear.path_length_m"]
    assert spatial["coverage"]["visited_cells"] == 1


def test_data_gap_is_excluded(tmp_path):
    s = SyntheticSession("GAP")
    walk(s, 0.0, 15.0)
    s.rows = [r for r in s.rows if float(r.split(",")[1]) <= 5.0 or float(r.split(",")[1]) >= 10.0]   # 5 s gap
    _, motion, _, _ = run(s.write(tmp_path / "gap.csv"))
    lin = motion.linear
    assert lin["n_gap_intervals"] == 1
    assert lin["gap_total_s"] == pytest.approx(5.0, abs=0.01)
    assert lin["path_length_m"] == pytest.approx(5.0, abs=0.02)           # 2.5 m before + 2.5 m after the gap
    assert lin["excluded_distance_m"] == pytest.approx(2.5, abs=0.02)     # the straight line across the gap
    assert lin["valid_time_s"] == pytest.approx(10.0, abs=0.02)
    assert lin["mean_speed_m_s"] == pytest.approx(0.5, abs=0.01)          # speed is NOT diluted by the gap


def test_teleport_is_a_discontinuity_not_motion(tmp_path):
    s = SyntheticSession("TELE")
    walk(s, 0.0, 5.0, start=(0.0, 1.6, -7.2))
    walk(s, 5.1, 10.0, start=(5.0, 1.6, 2.0), yaw0=170.0)                 # 8.4 m jump and a 170 deg turn within 0.1 s
    _, motion, _, _ = run(s.write(tmp_path / "tele.csv"))
    lin, rot = motion.linear, motion.rotational
    assert lin["n_discontinuities"] == 1
    assert lin["excluded_distance_m"] > 8.0
    assert lin["path_length_m"] == pytest.approx(2.5 + 2.45, abs=0.05)
    assert lin["max_speed_m_s"] < 1.0                                     # the ~84 m/s jump is not the "max speed"
    assert rot["excluded_angle_deg"] > 150.0
    assert rot["total_angular_movement_deg"] < 1.0                        # the 170 deg step is not head movement


def test_irregular_sampling_uses_real_dt(tmp_path):
    s = SyntheticSession("IRR")
    t = 0.0
    rng = np.random.default_rng(1)
    while t <= 20.0:
        s.pose(round(t, 3), pos=(0.0, 1.6, -7.2 + 1.0 * t))               # exactly 1 m/s regardless of dt
        t += float(rng.uniform(0.05, 0.4))
    _, motion, _, _ = run(s.write(tmp_path / "irr.csv"))
    assert motion.linear["mean_speed_m_s"] == pytest.approx(1.0, abs=0.01)
    assert motion.linear["max_speed_m_s"] == pytest.approx(1.0, abs=0.02)


def test_heatmap_total_equals_valid_time(standard_csv):
    _, motion, spatial, _ = run(standard_csv)
    assert spatial["occupancy_total_s"] == pytest.approx(motion.linear["valid_time_s"], abs=1e-6)
    assert spatial["occupancy_outside_arena_s"] == pytest.approx(0.0, abs=1e-9)


def test_coverage_of_a_known_line(standard_csv):
    _, _, spatial, _ = run(standard_csv)
    cov = spatial["coverage"]
    assert cov["total_cells"] == 40 * 36                                   # 20 x 18 m arena, 0.5 m cells
    assert 28 <= cov["visited_cells"] <= 34                                # a 15 m line is ~30 cells long
    assert cov["fraction"] == pytest.approx(cov["visited_cells"] / cov["total_cells"])


def test_coverage_is_na_without_arena(standard_csv):
    _, _, spatial, _ = run(standard_csv, AnalysisConfig(arena=None))
    assert spatial["coverage"] is None
    assert "spatial.coverage" in spatial["not_available"]


# ---- experiment metrics ---------------------------------------------------------------------------
def test_parse_details():
    assert parse_details("result=Correct;input=Controller;search_time_s=7.40") == {
        "result": "Correct", "input": "Controller", "search_time_s": "7.40"}
    assert parse_details("Ready->Exploring") == {}


def test_experiment_metrics_from_events(standard_csv):
    session = load_session(str(standard_csv))
    exp = compute_experiment(session.events, session.conditions)
    assert exp["status"] == "Completed" and exp["completed"] is True
    assert exp["condition"] == "AudioOnly"
    assert exp["targets_found"] == 3 and exp["targets_expected"] == 3
    assert exp["wrong_interactions"] == 1
    assert exp["wrong_list"][0]["selected"] == "C" and exp["wrong_list"][0]["expected"] == "B"
    assert exp["completion_time_s"] == pytest.approx(30.0)
    assert "logged" in exp["completion_time_source"]
    assert exp["total_search_time_s"] == pytest.approx(10.0 + 7.4 + 5.4)
    by_id = {t["id"]: t for t in exp["targets"]}
    assert by_id["B"]["activated_t_s"] == pytest.approx(12.6) and by_id["B"]["found_t_s"] == pytest.approx(20.0)
    assert by_id["B"]["wrong_selections_before_found"] == 1
    assert exp["target_order"] == ["A", "B", "C"]
    assert exp["not_available"] == {}


def test_aborted_session_reports_na_with_reason(tmp_path):
    session = load_session(str(standard_session(completed=False).write(tmp_path / "abort.csv")))
    exp = compute_experiment(session.events, session.conditions)
    assert exp["status"] == "Aborted"
    assert exp["completion_time_s"] is None
    assert "experiment.completion_time_s" in exp["not_available"]
    assert "did not reach" in exp["not_available"]["experiment.completion_time_s"]
    assert exp["experiment_duration_s"] == pytest.approx(30.0)
    assert "NOT completed" in exp["experiment_duration_basis"]


def test_poses_only_csv_has_no_invented_events(tmp_path):
    s = SyntheticSession("NOEV")
    walk(s, 0.0, 5.0)
    session = load_session(str(s.write(tmp_path / "noev.csv")))
    exp = compute_experiment(session.events, session.conditions)
    for key in ("targets_found", "completion_time_s", "wrong_interactions", "experiment_duration_s"):
        assert exp[key] is None
        assert "experiment." + key in exp["not_available"]


def test_mixed_condition_session_is_flagged(tmp_path):
    s = standard_session()
    s.event(14.0, "ConditionChanged", details="AudioVisual", condition="AudioVisual")
    session = load_session(str(s.write(tmp_path / "mixed.csv")))
    exp = compute_experiment(session.events, session.conditions)
    assert exp["condition"] == "Mixed"
    assert exp["condition_changes"] and exp["condition_changes"][0]["to"] == "AudioVisual"
