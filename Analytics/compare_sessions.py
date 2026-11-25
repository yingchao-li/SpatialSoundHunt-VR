"""Descriptive comparison of Audio Only vs Audio + Visual across the sessions found in a reports folder.

Reads every ``<reports-dir>/<session_id>/summary.json`` written by analyze_session.py. Only sessions
that were COMPLETED with a single condition take part. The output is purely descriptive: no p-values,
no "significant" wording. Sessions are not known to be independent repeats (the CSV has no
participant id), and the numbers per condition are usually tiny, so inferential statistics would be
unjustified.

Usage:
    python compare_sessions.py --reports-dir ../reports [--output ../reports]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np

CONDITIONS = ["AudioOnly", "AudioVisual"]
LABELS = {"AudioOnly": "Audio Only", "AudioVisual": "Audio + Visual"}

# (key, label, unit, digits, path into summary)
METRICS = [
    ("completion_time_s", "Completion time", "s", 1, ("experiment", "completion_time_s")),
    ("total_search_time_s", "Total search time", "s", 1, ("experiment", "total_search_time_s")),
    ("path_length_m", "Path length", "m", 1, ("linear", "path_length_m")),
    ("mean_speed_m_s", "Mean speed", "m/s", 2, ("linear", "mean_speed_m_s")),
    ("total_angular_movement_deg", "Total angular movement", "°", 0, ("rotational", "total_angular_movement_deg")),
    ("wrong_interactions", "Wrong selections", "", 0, ("experiment", "wrong_interactions")),
]

NOTE = ("Descriptive only. No significance test is computed: the CSV carries no participant identifier, so repeated "
        "sessions cannot be assumed independent, and the number of sessions per condition is small.")


def _get(summary: Dict[str, Any], path) -> Optional[float]:
    node: Any = summary
    for key in path:
        if not isinstance(node, dict) or key not in node:
            return None
        node = node[key]
    return float(node) if isinstance(node, (int, float)) and not isinstance(node, bool) and np.isfinite(node) else None


def load_summaries(reports_dir: Path) -> List[Dict[str, Any]]:
    out = []
    if not reports_dir.is_dir():
        return out
    for path in sorted(reports_dir.glob("*/summary.json")):
        try:
            with open(path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            continue
        if isinstance(data, dict) and str(data.get("schema", "")).startswith("spatial-sound-hunt/session-summary") \
                and "session" in data and "experiment" in data:
            out.append(data)
    return out


def build_comparison(summaries: List[Dict[str, Any]]) -> Dict[str, Any]:
    eligible, excluded = [], []
    for s in summaries:
        exp = s.get("experiment", {})
        sid = s.get("session", {}).get("session_id")
        if exp.get("status") != "Completed":
            excluded.append({"session_id": sid, "reason": "not completed (status: %s)" % exp.get("status")})
        elif exp.get("condition") not in CONDITIONS:
            excluded.append({"session_id": sid, "reason": "condition is %s" % exp.get("condition")})
        else:
            eligible.append(s)

    groups: Dict[str, Dict[str, Any]] = {}
    for cond in CONDITIONS:
        members = [s for s in eligible if s["experiment"]["condition"] == cond]
        stats: Dict[str, Any] = {}
        for key, _label, _unit, _digits, path in METRICS:
            values = [(s["session"]["session_id"], _get(s, path)) for s in members]
            present = [v for _, v in values if v is not None]
            stats[key] = {
                "n": len(present),
                "values": [{"session_id": sid, "value": v} for sid, v in values],
                "mean": float(np.mean(present)) if present else None,
                "sd": float(np.std(present, ddof=1)) if len(present) >= 2 else None,
                "min": float(np.min(present)) if present else None,
                "max": float(np.max(present)) if present else None,
            }
        groups[cond] = {"n_sessions": len(members), "session_ids": [s["session"]["session_id"] for s in members], "metrics": stats}

    n_ao, n_av = groups["AudioOnly"]["n_sessions"], groups["AudioVisual"]["n_sessions"]
    if n_ao == 0 and n_av == 0:
        status = "no completed sessions"
    elif n_ao == 0 or n_av == 0:
        only = LABELS["AudioOnly" if n_ao else "AudioVisual"]
        status = "single condition only (%s): descriptive statistics, no between-condition comparison possible" % only
    else:
        status = "both conditions present: descriptive comparison (%d vs %d sessions)" % (n_ao, n_av)

    difference: Dict[str, Optional[float]] = {}
    if n_ao and n_av:
        for key, *_ in METRICS:
            a, b = groups["AudioOnly"]["metrics"][key]["mean"], groups["AudioVisual"]["metrics"][key]["mean"]
            difference[key] = (b - a) if (a is not None and b is not None) else None

    return {"status": status, "note": NOTE, "groups": groups,
            "difference_of_means_audio_visual_minus_audio_only": difference,
            "n_sessions_considered": len(summaries), "excluded_sessions": excluded}


def render_fragment(comp: Dict[str, Any]) -> str:
    import html as _html
    esc = lambda v: _html.escape("" if v is None else str(v), quote=True)  # noqa: E731

    def fmt(v, d):
        return "N/A" if v is None else "{:,.{d}f}".format(v, d=d)

    parts = ['<p class="note">%s</p>' % esc(comp["status"])]
    if comp["groups"]["AudioOnly"]["n_sessions"] + comp["groups"]["AudioVisual"]["n_sessions"] > 0:
        head = "<tr><th>Metric</th>"
        for cond in CONDITIONS:
            head += "<th>%s (n = %d)</th>" % (esc(LABELS[cond]), comp["groups"][cond]["n_sessions"])
        head += "<th>Difference of means (A+V − A)</th></tr>"
        body = ""
        for key, label, unit, digits, _path in METRICS:
            row = "<tr><td>%s%s</td>" % (esc(label), esc(" (%s)" % unit if unit else ""))
            for cond in CONDITIONS:
                m = comp["groups"][cond]["metrics"][key]
                if m["n"] == 0:
                    cell = "N/A"
                elif m["n"] == 1:
                    cell = fmt(m["mean"], digits)
                else:
                    cell = "%s ± %s (min %s, max %s)" % (fmt(m["mean"], digits), fmt(m["sd"], digits), fmt(m["min"], digits), fmt(m["max"], digits))
                row += '<td class="%s">%s</td>' % ("na" if cell == "N/A" else "", esc(cell))
            diff = comp["difference_of_means_audio_visual_minus_audio_only"].get(key)
            row += '<td class="%s">%s</td></tr>' % ("na" if diff is None else "", esc("N/A" if diff is None else fmt(diff, digits)))
            body += row
        parts.append('<table class="tbl"><thead>%s</thead><tbody>%s</tbody></table>' % (head, body))
        ids = []
        for cond in CONDITIONS:
            if comp["groups"][cond]["session_ids"]:
                ids.append("%s: %s" % (LABELS[cond], ", ".join(comp["groups"][cond]["session_ids"])))
        parts.append('<p class="note">Sessions included — %s. Mean ± SD is shown only for n ≥ 2.</p>' % esc(" | ".join(ids)))
    parts.append('<p class="note">%s</p>' % esc(comp["note"]))
    if comp["excluded_sessions"]:
        parts.append('<p class="note">Not included: %s.</p>' % esc("; ".join("%s (%s)" % (e["session_id"], e["reason"]) for e in comp["excluded_sessions"])))
    return "".join(parts)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Descriptive Audio Only vs Audio+Visual comparison over analysed sessions.")
    parser.add_argument("--reports-dir", required=True, help="folder that contains <session_id>/summary.json")
    parser.add_argument("--output", help="folder for condition_comparison.json/html (default: the reports dir)")
    args = parser.parse_args(argv)

    reports = Path(args.reports_dir)
    out_dir = Path(args.output) if args.output else reports
    comp = build_comparison(load_summaries(reports))
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "condition_comparison.json").write_text(json.dumps(comp, indent=2, ensure_ascii=False), encoding="utf-8")

    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from report_generator import CSS, TITLE
    page = ('<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>%s — condition comparison</title>'
            "<style>%s</style></head><body><div class=\"wrap\"><header><h1>%s</h1></header>"
            '<div class="card full" style="margin-top:18px"><h2>Condition comparison</h2>%s</div></div></body></html>'
            % (TITLE, CSS, TITLE, render_fragment(comp)))
    (out_dir / "condition_comparison.html").write_text(page, encoding="utf-8")
    print("COMPARISON_PATH=%s" % (out_dir / "condition_comparison.html").resolve())
    return 0


if __name__ == "__main__":
    sys.exit(main())
