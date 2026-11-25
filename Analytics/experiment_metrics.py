"""Experiment-performance metrics derived ONLY from the logged Event rows.

Events written by ExperimentManager / SessionRecorder (Assets/Scripts/SpatialSoundHunt):

    SessionStart       details "order=A>B>C"
    StateChanged       details "<previous>-><next>"
    TargetActivated    interacted_target = target id, details "index=n/3"
    TargetSelected     interacted_target = target id,
                       details "result=Correct;input=<source>;search_time_s=<s>"
                           or  "result=Wrong;input=<source>;expected=<id>"
    ConditionChanged   details = new condition
    ExperimentCompleted details "total_time_s=<s>;wrong_selections=<n>"
    ExperimentAborted  details "state=<state>"
    SessionEnd         details "reason=Completed|Aborted|Restarted|Interrupted"

Anything that cannot be derived from these rows is reported as N/A together with the reason; nothing is
estimated or invented.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

import numpy as np
import pandas as pd

CONDITION_LABELS = {"AudioOnly": "Audio Only", "AudioVisual": "Audio + Visual"}


def condition_label(value: Optional[str]) -> str:
    if value is None:
        return "N/A"
    return CONDITION_LABELS.get(value, value)


def parse_details(details: str) -> Dict[str, str]:
    """'a=1;b=x' -> {'a': '1', 'b': 'x'}. Items without '=' are ignored."""
    out: Dict[str, str] = {}
    for item in str(details).split(";"):
        if "=" in item:
            key, _, value = item.partition("=")
            out[key.strip()] = value.strip()
    return out


def _float(value: Optional[str]) -> Optional[float]:
    try:
        number = float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return None
    return number if np.isfinite(number) else None


def compute_experiment(events: pd.DataFrame, conditions: List[str]) -> Dict[str, Any]:
    na: Dict[str, str] = {}
    out: Dict[str, Any] = {}

    # ---- condition -------------------------------------------------------------------------
    if len(conditions) == 1:
        out["condition"] = conditions[0]
    elif len(conditions) > 1:
        out["condition"] = "Mixed"
    else:
        out["condition"] = None
        na["experiment.condition"] = "no condition values in the CSV"
    out["condition_label"] = "Mixed (changed during the session)" if out["condition"] == "Mixed" else condition_label(out["condition"])
    out["conditions_seen"] = list(conditions)

    if events is None or len(events) == 0:
        reason = "the CSV contains no Event rows"
        out.update({"status": "Unknown", "end_reason": None, "completed": None, "targets": [], "wrong_list": [],
                    "event_counts": {}, "condition_changes": [], "target_order": None})
        for key in ("targets_found", "targets_expected", "completion_time_s", "completion_time_source",
                    "experiment_duration_s", "total_search_time_s", "mean_search_time_s", "wrong_interactions"):
            out[key] = None
            na["experiment." + key] = reason
        out["not_available"] = na
        return out

    ev = events
    out["event_counts"] = {k: int(v) for k, v in ev["event"].value_counts().items()}

    def rows(name: str) -> pd.DataFrame:
        return ev[ev["event"] == name]

    start_rows, end_rows = rows("SessionStart"), rows("SessionEnd")
    completed_rows = rows("ExperimentCompleted")

    t_start = float(start_rows["t"].iloc[0]) if len(start_rows) else None
    t_end = float(end_rows["t"].iloc[-1]) if len(end_rows) else None
    if t_start is None:
        na["experiment.session_start"] = "no SessionStart event"
    if t_end is None:
        na["experiment.session_end"] = "no SessionEnd event (file was not closed cleanly)"

    # ---- status -----------------------------------------------------------------------------
    end_reason = None
    if len(end_rows):
        end_reason = parse_details(end_rows["details"].iloc[-1]).get("reason")
    completed = bool(len(completed_rows))
    if completed:
        status = "Completed"
    elif end_reason:
        status = end_reason
    else:
        status = "Unknown"
    out.update({"status": status, "end_reason": end_reason, "completed": completed})

    order_text = parse_details(start_rows["details"].iloc[0]).get("order") if len(start_rows) else None
    out["target_order"] = order_text.split(">") if order_text else None
    if out["target_order"] is None:
        na["experiment.target_order"] = "no SessionStart event with an 'order=' detail"

    out["condition_changes"] = [
        {"t_s": float(r.t), "to": r.details.strip()} for r in rows("ConditionChanged").itertuples()
    ]

    # ---- targets ----------------------------------------------------------------------------
    activations = rows("TargetActivated")
    selections = rows("TargetSelected")
    expected_total: Optional[int] = None
    targets: List[Dict[str, Any]] = []
    index_by_id: Dict[str, int] = {}

    for r in activations.itertuples():
        tid = r.interacted_target or r.current_target
        if not tid or tid in index_by_id:
            continue
        info = parse_details(r.details)
        idx_text = info.get("index", "")
        if "/" in idx_text:
            total = _float(idx_text.split("/")[1])
            if total:
                expected_total = int(total)
        index_by_id[tid] = len(targets)
        targets.append({
            "id": tid, "order_index": len(targets) + 1, "activated_t_s": float(r.t), "found_t_s": None,
            "search_time_s": None, "search_time_source": None, "wrong_selections_before_found": 0,
            "condition_at_activation": r.condition or None, "found": False,
        })

    wrong_list: List[Dict[str, Any]] = []
    for r in selections.itertuples():
        info = parse_details(r.details)
        result = info.get("result")
        if result == "Correct":
            tid = r.interacted_target
            if tid not in index_by_id:
                index_by_id[tid] = len(targets)
                targets.append({
                    "id": tid, "order_index": len(targets) + 1, "activated_t_s": None, "found_t_s": None,
                    "search_time_s": None, "search_time_source": None, "wrong_selections_before_found": 0,
                    "condition_at_activation": None, "found": False,
                })
            tgt = targets[index_by_id[tid]]
            if tgt["found"]:
                continue
            tgt["found"] = True
            tgt["found_t_s"] = float(r.t)
            logged = _float(info.get("search_time_s"))
            if logged is not None:
                tgt["search_time_s"] = logged
                tgt["search_time_source"] = "logged search_time_s"
            elif tgt["activated_t_s"] is not None:
                tgt["search_time_s"] = tgt["found_t_s"] - tgt["activated_t_s"]
                tgt["search_time_source"] = "found_t - activated_t (event timestamps)"
        elif result == "Wrong":
            expected = info.get("expected") or r.current_target or None
            wrong_list.append({
                "t_s": float(r.t), "selected": r.interacted_target or None, "expected": expected,
                "input": info.get("input"),
            })
            if expected in index_by_id and not targets[index_by_id[expected]]["found"]:
                targets[index_by_id[expected]]["wrong_selections_before_found"] += 1

    for tgt in targets:
        if tgt["found"] and tgt["activated_t_s"] is None:
            na["experiment.target_%s.activated_t_s" % tgt["id"]] = "no TargetActivated event for this target"
        if tgt["found"] and tgt["search_time_s"] is None:
            na["experiment.target_%s.search_time_s" % tgt["id"]] = "neither search_time_s detail nor TargetActivated event"

    out["targets"] = targets
    out["wrong_list"] = wrong_list
    out["targets_found"] = int(sum(1 for t in targets if t["found"]))
    out["targets_expected"] = expected_total
    if expected_total is None:
        na["experiment.targets_expected"] = "no TargetActivated event with an 'index=n/total' detail"

    if len(selections) or len(activations):
        out["wrong_interactions"] = len(wrong_list)
    else:
        out["wrong_interactions"] = None
        na["experiment.wrong_interactions"] = "no TargetSelected / TargetActivated events logged"

    # ---- times ------------------------------------------------------------------------------
    completion_time = None
    completion_source = None
    if completed:
        info = parse_details(completed_rows["details"].iloc[0])
        completion_time = _float(info.get("total_time_s"))
        if completion_time is not None:
            completion_source = "ExperimentCompleted.total_time_s (logged by Unity)"
        elif t_start is not None:
            completion_time = float(completed_rows["t"].iloc[0]) - t_start
            completion_source = "t(ExperimentCompleted) - t(SessionStart)"
        wrong_logged = _float(info.get("wrong_selections"))
        if wrong_logged is not None and out["wrong_interactions"] is not None and int(wrong_logged) != out["wrong_interactions"]:
            out["wrong_count_mismatch"] = {"logged_by_unity": int(wrong_logged), "counted_from_events": out["wrong_interactions"]}
    out["completion_time_s"] = completion_time
    out["completion_time_source"] = completion_source
    if not completed:
        na["experiment.completion_time_s"] = "the session did not reach ExperimentCompleted (status: %s)" % status
        na["experiment.completion_time_source"] = na["experiment.completion_time_s"]

    if completion_time is not None:
        out["experiment_duration_s"] = completion_time
        out["experiment_duration_basis"] = "start to completion"
    elif t_start is not None and t_end is not None:
        out["experiment_duration_s"] = t_end - t_start
        out["experiment_duration_basis"] = "start to SessionEnd (session NOT completed)"
    else:
        out["experiment_duration_s"] = None
        out["experiment_duration_basis"] = None
        na["experiment.experiment_duration_s"] = "no SessionStart/SessionEnd pair and no completion event"

    searched = [t["search_time_s"] for t in targets if t["search_time_s"] is not None]
    out["total_search_time_s"] = float(sum(searched)) if searched else None
    out["mean_search_time_s"] = float(np.mean(searched)) if searched else None
    if not searched:
        na["experiment.total_search_time_s"] = "no target was found, or no search time could be derived"
        na["experiment.mean_search_time_s"] = na["experiment.total_search_time_s"]

    out["not_available"] = na
    return out
