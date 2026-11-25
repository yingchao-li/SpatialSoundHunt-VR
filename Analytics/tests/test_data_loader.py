"""Loader tests: empty / malformed input, missing columns, duplicate timestamps, missing values, ordering.
All CSVs are SYNTHETIC (tests/synthetic.py)."""
import numpy as np
import pytest

from data_loader import DataValidationError, HEADER, load_session
from synthetic import SyntheticSession, standard_session, walk


def test_normal_csv_loads(standard_csv):
    s = load_session(str(standard_csv))
    assert s.session_id == "SYNTH-0001"
    assert s.conditions == ["AudioOnly"]
    assert len(s.poses) == 301                       # 0 .. 30 s at 10 Hz
    assert s.quality["pose_rows_dropped_missing_values"] == 0
    assert s.quality["pose_rows_duplicate_timestamps_removed"] == 0
    assert abs(s.quality["estimated_sample_rate_hz"] - 10.0) < 0.2
    assert set(s.events["event"]) >= {"SessionStart", "TargetActivated", "TargetSelected", "ExperimentCompleted", "SessionEnd"}
    norms = np.linalg.norm(s.poses[["qx", "qy", "qz", "qw"]].to_numpy(), axis=1)
    assert np.allclose(norms, 1.0)


def test_empty_file_is_rejected(tmp_path):
    p = tmp_path / "empty.csv"
    p.write_text("")
    with pytest.raises(DataValidationError, match="empty"):
        load_session(str(p))


def test_header_only_is_rejected(tmp_path):
    p = tmp_path / "header_only.csv"
    p.write_text(",".join(HEADER) + "\n")
    with pytest.raises(DataValidationError, match="no data rows"):
        load_session(str(p))


def test_missing_file_is_rejected(tmp_path):
    with pytest.raises(DataValidationError, match="not found"):
        load_session(str(tmp_path / "nope.csv"))


def test_missing_required_columns_are_named(tmp_path):
    p = tmp_path / "bad.csv"
    p.write_text("timestamp_utc,session_id,hmd_px,hmd_py,hmd_pz\n2026-01-01T00:00:00Z,x,0,0,0\n")
    with pytest.raises(DataValidationError) as err:
        load_session(str(p))
    message = str(err.value)
    for column in ("session_time_s", "record_type", "event", "hmd_qx", "hmd_qw"):
        assert column in message


def test_duplicate_timestamps_are_removed_and_counted(tmp_path):
    s = SyntheticSession("DUP")
    walk(s, 0.0, 10.0)
    # duplicate every 10th pose row (same time, slightly different position -> the last one must win)
    extra = []
    for line in s.rows[::10]:
        parts = line.split(",")
        parts[9] = "9.9999"
        extra.append(",".join(parts))
    s.rows.extend(extra)
    p = s.write(tmp_path / "dup.csv")
    loaded = load_session(str(p))
    assert loaded.quality["pose_rows_duplicate_timestamps_removed"] == len(extra)
    assert len(loaded.poses) == 101
    assert loaded.poses["t"].is_unique
    assert any("duplicate" in w for w in loaded.warnings)


def test_missing_values_are_dropped_not_interpolated(tmp_path):
    s = SyntheticSession("NAN")
    walk(s, 0.0, 10.0)
    s.rows[5] = s.rows[5].replace(s.rows[5].split(",")[9], "")           # blank hmd_px
    s.rows[6] = s.rows[6].replace(s.rows[6].split(",")[10], "nan")        # 'nan' text
    s.rows[7] = s.rows[7].replace(s.rows[7].split(",")[11], "abc")        # garbage
    s.rows[8] = s.rows[8].replace(s.rows[8].split(",")[12], "inf")        # infinity
    p = s.write(tmp_path / "nan.csv")
    loaded = load_session(str(p))
    assert loaded.quality["pose_rows_dropped_missing_values"] == 4
    assert len(loaded.poses) == 101 - 4
    assert np.isfinite(loaded.poses.to_numpy(dtype=float)).all()


def test_all_poses_unusable_is_rejected(tmp_path):
    s = SyntheticSession("ALLBAD")
    for i in range(10):
        s.pose(i * 0.1, pos=None, quat=None)
    with pytest.raises(DataValidationError, match="No usable pose"):
        load_session(str(s.write(tmp_path / "allbad.csv")))


def test_out_of_order_rows_are_sorted(tmp_path):
    s = SyntheticSession("ORDER")
    walk(s, 0.0, 5.0)
    s.rows[10], s.rows[20] = s.rows[20], s.rows[10]
    loaded = load_session(str(s.write(tmp_path / "order.csv")))
    assert loaded.quality["pose_rows_out_of_order"] > 0
    assert np.all(np.diff(loaded.poses["t"].to_numpy()) > 0)


def test_zero_quaternion_rows_are_dropped(tmp_path):
    s = SyntheticSession("Q0")
    walk(s, 0.0, 2.0)
    s.pose(2.05, quat=(0.0, 0.0, 0.0, 0.0))
    loaded = load_session(str(s.write(tmp_path / "q0.csv")))
    assert loaded.quality["pose_rows_dropped_invalid_quaternion"] == 1


def test_bom_and_extra_columns_are_tolerated(tmp_path):
    s = standard_session()
    p = tmp_path / "bom.csv"
    text = (",".join(HEADER) + ",extra_col\n") + "\n".join(r + ",x" for r in s.rows) + "\n"
    p.write_text("﻿" + text, encoding="utf-8")
    loaded = load_session(str(p))
    assert len(loaded.poses) == 301


def test_timestamp_with_seven_fraction_digits_fallback(tmp_path):
    s = SyntheticSession("TS")
    walk(s, 0.0, 3.0)
    lines = [r.split(",") for r in s.rows]
    for parts in lines:
        parts[1] = ""                      # session_time_s unusable -> derived from timestamp_utc
    p = tmp_path / "ts.csv"
    p.write_text(",".join(HEADER) + "\n" + "\n".join(",".join(x) for x in lines) + "\n")
    loaded = load_session(str(p))
    assert len(loaded.poses) == 31
    assert abs(loaded.poses["t"].iloc[-1] - 3.0) < 1e-3
    assert "timestamp_utc" in loaded.quality["time_source"]
