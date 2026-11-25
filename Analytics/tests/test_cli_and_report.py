"""CLI + report tests: exit codes, failure handling, output isolation between sessions (SYNTHETIC data)."""
import hashlib
import json
import subprocess
import sys
from pathlib import Path

import pytest

import analyze_session
from data_loader import HEADER
from synthetic import SyntheticSession, standard_session, walk

SCRIPT = Path(analyze_session.__file__)


def cli(*args):
    return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)], capture_output=True, text=True, timeout=180)


def strict_json(path):
    def reject(constant):
        raise ValueError("non-finite number in JSON: " + constant)
    return json.loads(Path(path).read_text(encoding="utf-8"), parse_constant=reject)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def test_cli_end_to_end_creates_all_outputs(standard_csv, tmp_path):
    out = tmp_path / "reports" / "SYNTH-0001"
    r = cli("--input", standard_csv, "--output", out)
    assert r.returncode == 0, r.stderr
    last = r.stdout.strip().splitlines()[-1]
    assert last == "REPORT_PATH=%s" % (out.resolve() / "report.html")
    for name in ("report.html", "summary.json", "processed_poses.csv", "events.csv", "heatmap_xz_occupancy.csv", "source_session.csv"):
        assert (out / name).is_file(), name

    summary = strict_json(out / "summary.json")                      # strict: NaN / Infinity would raise
    assert summary["schema"].startswith("spatial-sound-hunt/session-summary")
    assert summary["kpis"]["targets_found"] == 3
    assert summary["kpis"]["total_distance_m"] == pytest.approx(15.0, abs=0.01)
    assert summary["kpis"]["total_angular_movement_deg"] == pytest.approx(90.0, abs=0.05)
    assert summary["experiment"]["completion_time_s"] == pytest.approx(30.0)
    assert summary["input"]["sha256"] == sha(standard_csv)

    page = (out / "report.html").read_text(encoding="utf-8")
    assert "Spatial Sound Hunt — 6-DoF Exploration Analytics" in page
    for label in ("Experiment Duration", "Total Distance", "Mean Speed", "Total Angular Movement", "Targets Found",
                  "3D head trajectory", "Head speed vs time", "Angular velocity vs time", "XZ spatial heatmap",
                  "Target completion timeline"):
        assert label in page, label
    assert page.count("plotly-graph-div") >= 6                           # every chart is present
    assert '<script src="http' not in page and '<link' not in page          # offline: no external script/stylesheet
    assert "Plotly.newPlot" in page


def test_empty_csv_fails_cleanly_without_report(tmp_path):
    empty = tmp_path / "empty.csv"
    empty.write_text("")
    out = tmp_path / "out"
    r = cli("--input", empty, "--output", out)
    assert r.returncode == 2
    assert "ERROR" in r.stderr and "empty" in r.stderr.lower()
    assert not (out / "report.html").exists()


def test_missing_columns_fail_cleanly(tmp_path):
    bad = tmp_path / "bad.csv"
    bad.write_text("a,b,c\n1,2,3\n")
    r = cli("--input", bad, "--output", tmp_path / "out")
    assert r.returncode == 2 and "Missing required column" in r.stderr
    assert not (tmp_path / "out" / "report.html").exists()


def test_missing_input_file_fails_cleanly(tmp_path):
    r = cli("--input", tmp_path / "nope.csv", "--output", tmp_path / "out")
    assert r.returncode == 2 and "not found" in r.stderr


def test_invalid_config_fails_cleanly(standard_csv, tmp_path):
    cfg = tmp_path / "cfg.json"
    cfg.write_text(json.dumps({"no_such_option": 1}))
    r = cli("--input", standard_csv, "--output", tmp_path / "out", "--config", cfg)
    assert r.returncode == 2 and "invalid configuration" in r.stderr


def test_unexpected_analysis_error_gives_exit_1_and_no_report(standard_csv, tmp_path, monkeypatch, capsys):
    def boom(*a, **k):
        raise RuntimeError("simulated analysis failure")
    monkeypatch.setattr(analyze_session, "compute_motion", boom)
    out = tmp_path / "out"
    code = analyze_session.main(["--input", str(standard_csv), "--output", str(out)])
    err = capsys.readouterr().err
    assert code == 1
    assert "simulated analysis failure" in err and "Traceback" in err
    assert not (out / "report.html").exists()
    assert standard_csv.is_file()                                         # the data is never touched


def test_single_sample_still_produces_a_report_with_na(tmp_path):
    s = SyntheticSession("SINGLE")
    s.pose(0.0)
    csv = s.write(tmp_path / "single.csv")
    out = tmp_path / "out"
    r = cli("--input", csv, "--output", out)
    assert r.returncode == 0, r.stderr
    summary = strict_json(out / "summary.json")
    assert summary["kpis"]["total_distance_m"] is None
    assert "linear.path_length_m" in summary["not_available"]
    assert "N/A" in (out / "report.html").read_text(encoding="utf-8")


def test_multiple_sessions_never_overwrite_each_other(tmp_path):
    reports = tmp_path / "reports"
    csv_a = standard_session("SESSION-A", "AudioOnly").write(tmp_path / "in" / "a.csv")
    csv_b = standard_session("SESSION-B", "AudioVisual").write(tmp_path / "in" / "b.csv")
    assert cli("--input", csv_a, "--output", reports / "SESSION-A").returncode == 0
    before = {n: sha(reports / "SESSION-A" / n) for n in ("summary.json", "processed_poses.csv", "source_session.csv")}
    assert cli("--input", csv_b, "--output", reports / "SESSION-B").returncode == 0
    after = {n: sha(reports / "SESSION-A" / n) for n in before}
    assert before == after                                                # session A untouched by the run for B
    a, b = strict_json(reports / "SESSION-A" / "summary.json"), strict_json(reports / "SESSION-B" / "summary.json")
    assert a["session"]["session_id"] == "SESSION-A" and b["session"]["session_id"] == "SESSION-B"
    assert "SESSION-A" in (reports / "SESSION-A" / "report.html").read_text(encoding="utf-8")
    assert "SESSION-B" in (reports / "SESSION-B" / "report.html").read_text(encoding="utf-8")


def test_same_output_dir_for_a_different_csv_is_refused_unless_forced(tmp_path):
    out = tmp_path / "reports" / "X"
    csv_a = standard_session("SAME-ID", "AudioOnly").write(tmp_path / "a.csv")
    csv_b = standard_session("SAME-ID", "AudioVisual").write(tmp_path / "b.csv")
    assert cli("--input", csv_a, "--output", out).returncode == 0
    first = sha(out / "summary.json")
    r = cli("--input", csv_b, "--output", out)
    assert r.returncode == 3 and "different CSV" in r.stderr
    assert sha(out / "summary.json") == first
    assert cli("--input", csv_b, "--output", out, "--force").returncode == 0
    assert sha(out / "summary.json") != first


def test_reanalysing_the_same_csv_is_idempotent(standard_csv, tmp_path):
    out = tmp_path / "reports" / "S"
    assert cli("--input", standard_csv, "--output", out).returncode == 0
    assert cli("--input", standard_csv, "--output", out).returncode == 0
    assert (out / "report.html").is_file()


def test_paths_with_spaces_and_unicode(tmp_path):
    src = standard_session("SPACE-1").write(tmp_path / "Application Support" / "Default Company" / "séssion one.csv")
    out = tmp_path / "my reports" / "SPACE-1"
    r = cli("--input", src, "--output", out)
    assert r.returncode == 0, r.stderr
    assert (out / "report.html").is_file()


def test_condition_comparison_is_descriptive_only(tmp_path):
    reports = tmp_path / "reports"
    for sid, cond in (("S1", "AudioOnly"), ("S2", "AudioVisual")):
        csv = standard_session(sid, cond).write(tmp_path / "in" / (sid + ".csv"))
        assert cli("--input", csv, "--output", reports / sid).returncode == 0
    page = (reports / "S2" / "report.html").read_text(encoding="utf-8")
    assert "both conditions present" in page
    assert "Descriptive only" in page
    assert "p-value" not in page.lower() and "p &lt;" not in page and "statistically significant" not in page.lower()
    summary = strict_json(reports / "S2" / "summary.json")
    assert "both conditions" in summary["condition_comparison"]["status"]


def test_comparison_with_one_session_says_no_comparison_possible(standard_csv, tmp_path):
    out = tmp_path / "reports" / "ONLY"
    assert cli("--input", standard_csv, "--output", out).returncode == 0
    summary = strict_json(out / "summary.json")
    assert "single condition only" in summary["condition_comparison"]["status"]


def test_compare_sessions_cli(tmp_path):
    reports = tmp_path / "reports"
    for sid, cond in (("S1", "AudioOnly"), ("S2", "AudioOnly"), ("S3", "AudioVisual")):
        csv = standard_session(sid, cond).write(tmp_path / "in" / (sid + ".csv"))
        assert cli("--input", csv, "--output", reports / sid).returncode == 0
    r = subprocess.run([sys.executable, str(SCRIPT.parent / "compare_sessions.py"), "--reports-dir", str(reports)],
                       capture_output=True, text=True)
    assert r.returncode == 0, r.stderr
    comp = strict_json(reports / "condition_comparison.json")
    assert comp["groups"]["AudioOnly"]["n_sessions"] == 2 and comp["groups"]["AudioVisual"]["n_sessions"] == 1
    assert comp["groups"]["AudioOnly"]["metrics"]["path_length_m"]["sd"] is not None       # n = 2 -> SD defined
    assert comp["groups"]["AudioVisual"]["metrics"]["path_length_m"]["sd"] is None          # n = 1 -> SD not defined


def test_untrusted_strings_are_escaped_in_html(tmp_path):
    s = standard_session('<script>alert(1)</script>', "AudioOnly")
    csv = s.write(tmp_path / "xss.csv")
    out = tmp_path / "out"
    assert cli("--input", csv, "--output", out).returncode == 0
    page = (out / "report.html").read_text(encoding="utf-8")
    assert "<script>alert(1)</script>" not in page
    assert "&lt;script&gt;alert(1)&lt;/script&gt;" in page
