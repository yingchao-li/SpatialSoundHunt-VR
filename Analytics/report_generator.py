"""Self-contained interactive HTML dashboard (Plotly, dark research style).

The page needs no web server: plotly.js is embedded (default) so ``report.html`` opens from disk
with a double click / ``file://`` URL and works offline.

Colour roles follow the validated dataviz palette (dark mode): slot 1 blue / slot 2 orange for
series, one-hue blue ramp for magnitude (heatmap, time), status colours only together with an icon
and a label.
"""
from __future__ import annotations

import html
import os
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np
import pandas as pd
import plotly.graph_objects as go
import plotly.offline

from config import AnalysisConfig

TITLE = "Spatial Sound Hunt — 6-DoF Exploration Analytics"

C = {
    "page": "#0b0b0b", "surface": "#191919", "ink": "#efefef", "ink2": "#9c9c9c",
    "grid": "#303030", "axis": "#474747", "s1": "#338ced", "s2": "#e76624", "s3": "#00a888",
    "good": "#0aac23", "warning": "#fbb315", "serious": "#f18a59", "critical": "#dc3c3d",
}
FONT = 'system-ui, -apple-system, "Segoe UI", sans-serif'
# one-hue (blue) ramp, dark-mode orientation: low value recedes toward the surface, high value is light
SEQ = ["#0e3d7e", "#0f4a96", "#1757ac", "#1b64be", "#2370cd", "#257de0", "#338ced", "#519ced",
       "#6aabf1", "#84b9f3", "#9dc8f7", "#b6d5f8", "#cce3fd"]
HEAT_SCALE = [[i / (len(SEQ) - 1), c] for i, c in enumerate(SEQ)]
TIME_SCALE = [[i / (len(SEQ) - 4), c] for i, c in enumerate(SEQ[3:])]  # lines need >= 3:1 on the surface

WRONG_ROW = "Wrong selections"
PLOT_CONFIG = {"displaylogo": False, "responsive": True, "modeBarButtonsToRemove": ["lasso2d", "select2d"]}


def esc(value: Any) -> str:
    return html.escape("" if value is None else str(value), quote=True)


# ---------------------------------------------------------------------------------------------
# formatting helpers
# ---------------------------------------------------------------------------------------------
def fnum(value: Optional[float], digits: int = 2, unit: str = "") -> str:
    if value is None or (isinstance(value, float) and not np.isfinite(value)):
        return "N/A"
    text = "{:,.{d}f}".format(float(value), d=digits)
    return text + (" " + unit if unit else "")


def fclock(seconds: Optional[float]) -> str:
    if seconds is None:
        return "N/A"
    seconds = float(seconds)
    return "%d:%04.1f" % (int(seconds // 60), seconds % 60)


# ---------------------------------------------------------------------------------------------
# figures
# ---------------------------------------------------------------------------------------------
def _base_layout(height: int = 380, **extra: Any) -> Dict[str, Any]:
    axis = dict(gridcolor=C["grid"], linecolor=C["axis"], zerolinecolor=C["axis"], tickfont=dict(color=C["ink2"], size=11),
                title=dict(font=dict(color=C["ink2"], size=12)), showline=True, mirror=False)
    layout = dict(
        height=height, margin=dict(l=62, r=18, t=66, b=52),
        paper_bgcolor=C["surface"], plot_bgcolor=C["surface"],
        font=dict(family=FONT, color=C["ink"], size=12),
        hoverlabel=dict(bgcolor=C["page"], bordercolor=C["axis"], font=dict(color=C["ink"], family=FONT)),
        legend=dict(orientation="h", y=1.0, yanchor="bottom", x=0, xanchor="left", font=dict(color=C["ink2"], size=11), bgcolor="rgba(0,0,0,0)"),
        xaxis=dict(axis), yaxis=dict(axis),
    )
    layout.update(extra)
    return layout


def _stride(table: pd.DataFrame, max_points: int) -> pd.DataFrame:
    n = len(table)
    if n <= max_points:
        return table
    step = int(np.ceil(n / max_points))
    idx = np.unique(np.concatenate([np.arange(0, n, step), [n - 1]]))
    return table.iloc[idx]


def _breaks(table: pd.DataFrame, *columns: str) -> List[np.ndarray]:
    """Columns as float arrays with a NaN row inserted before every excluded interval (broken line)."""
    status = table["interval_status"].to_numpy(dtype=object)
    idx = np.where((status == "gap") | (status == "discontinuity"))[0]
    return [np.insert(table[c].to_numpy(dtype=float), idx, np.nan) for c in columns]


def _na_figure(message: str, height: int = 300) -> go.Figure:
    fig = go.Figure()
    fig.update_layout(**_base_layout(height=height, xaxis=dict(visible=False), yaxis=dict(visible=False)))
    fig.add_annotation(text=esc(message), showarrow=False, font=dict(color=C["ink2"], size=14), x=0.5, y=0.5,
                       xref="paper", yref="paper")
    return fig


def fig_trajectory_3d(table: pd.DataFrame, cfg: AnalysisConfig) -> go.Figure:
    tb = _stride(table, cfg.max_plot_points)
    t, x, y, z = _breaks(tb, "t", "x", "y", "z")
    fig = go.Figure()
    fig.add_trace(go.Scatter3d(
        x=x, y=z, z=y, mode="lines", name="Head path (colour = time)",
        line=dict(color=t, colorscale=TIME_SCALE, width=5,
                  colorbar=dict(title=dict(text="time (s)", side="right", font=dict(color=C["ink2"])), tickfont=dict(color=C["ink2"]),
                                thickness=10, len=0.6, y=0.45, yanchor="middle", outlinewidth=0)),
        customdata=t, hovertemplate="t = %{customdata:.1f} s<br>X %{x:.2f} m<br>Z %{y:.2f} m<br>height %{z:.2f} m<extra></extra>",
    ))
    first, last = tb.iloc[0], tb.iloc[-1]
    fig.add_trace(go.Scatter3d(
        x=[first.x], y=[first.z], z=[first.y], mode="markers+text", name="Start", text=["Start"], textposition="top center",
        textfont=dict(color=C["ink"]), marker=dict(size=7, color=C["ink"], symbol="circle"),
        hovertemplate="Start<br>t = %.1f s<extra></extra>" % first.t))
    fig.add_trace(go.Scatter3d(
        x=[last.x], y=[last.z], z=[last.y], mode="markers+text", name="End", text=["End"], textposition="top center",
        textfont=dict(color=C["ink"]), marker=dict(size=7, color=C["ink"], symbol="diamond"),
        hovertemplate="End<br>t = %.1f s<extra></extra>" % last.t))
    if cfg.targets:
        fig.add_trace(go.Scatter3d(
            x=[tg.x for tg in cfg.targets], y=[tg.z for tg in cfg.targets], z=[tg.y for tg in cfg.targets],
            mode="markers+text", name="Targets (lab layout from config)", text=[tg.id for tg in cfg.targets],
            textposition="top center", textfont=dict(color=C["ink2"]),
            marker=dict(size=6, color=C["ink2"], symbol="square-open"),
            hovertemplate="Target %{text}<extra></extra>"))
    if cfg.arena is not None:
        a = cfg.arena
        fig.add_trace(go.Scatter3d(
            x=[a.x_min, a.x_max, a.x_max, a.x_min, a.x_min], y=[a.z_min, a.z_min, a.z_max, a.z_max, a.z_min], z=[0] * 5,
            mode="lines", name="Arena floor (config)", line=dict(color=C["axis"], width=2), hoverinfo="skip"))
    axis3 = dict(backgroundcolor=C["surface"], gridcolor=C["grid"], zerolinecolor=C["axis"], showbackground=True,
                 tickfont=dict(color=C["ink2"], size=10), title=dict(font=dict(color=C["ink2"], size=12)))
    fig.update_layout(**_base_layout(
        height=520, margin=dict(l=0, r=0, t=10, b=0),
        scene=dict(xaxis=dict(axis3, title=dict(text="X east (m)", font=dict(color=C["ink2"]))),
                   yaxis=dict(axis3, title=dict(text="Z north (m)", font=dict(color=C["ink2"]))),
                   zaxis=dict(axis3, title=dict(text="height Y (m)", font=dict(color=C["ink2"]))),
                   aspectmode="data", bgcolor=C["surface"]),
        xaxis=None, yaxis=None))
    fig.update_layout(xaxis=None, yaxis=None)
    return fig


def _time_series_figure(table: pd.DataFrame, column: str, name: str, ytitle: str, unit: str, cfg: AnalysisConfig,
                        experiment: Dict[str, Any], threshold: Optional[float] = None,
                        threshold_label: str = "") -> go.Figure:
    tb = _stride(table, cfg.max_plot_points)
    t, v = _breaks(tb, "t", column)
    fig = go.Figure()
    fig.add_trace(go.Scatter(x=t, y=v, mode="lines", name=name, line=dict(color=C["s1"], width=2),
                             hovertemplate="%{y:.2f} " + unit + "<extra>" + name + "</extra>"))
    status = table["interval_status"].to_numpy(dtype=object)
    tt = table["t"].to_numpy(dtype=float)
    bad = np.where((status == "gap") | (status == "discontinuity"))[0][:150]
    for i in bad:
        fig.add_vrect(x0=tt[i - 1], x1=tt[i], fillcolor="rgba(239,239,239,0.10)", line_width=0, layer="below")
    if threshold is not None:
        fig.add_hline(y=threshold, line=dict(color=C["ink2"], width=1, dash="dash"),
                      annotation_text=threshold_label, annotation_position="top right",
                      annotation_font=dict(color=C["ink2"], size=11))
    for tgt in experiment.get("targets", []):
        if tgt.get("found_t_s") is not None:
            fig.add_vline(x=tgt["found_t_s"], line=dict(color=C["ink2"], width=1, dash="dot"),
                          annotation_text="%s found" % tgt["id"], annotation_position="top",
                          annotation_font=dict(color=C["ink2"], size=11))
    fig.update_layout(**_base_layout(height=340, hovermode="x unified", showlegend=False, margin=dict(l=62, r=18, t=46, b=52)))
    fig.update_xaxes(title=dict(text="session time (s)"))
    fig.update_yaxes(title=dict(text=ytitle), rangemode="tozero")
    return fig


def fig_speed(table, cfg, experiment) -> go.Figure:
    return _time_series_figure(table, "speed_m_s", "Head speed", "head speed (m/s)", "m/s", cfg, experiment,
                               threshold=cfg.stationary_speed_m_s,
                               threshold_label="stationary < %.2f m/s" % cfg.stationary_speed_m_s)


def fig_angular(table, cfg, experiment) -> go.Figure:
    return _time_series_figure(table, "angular_speed_deg_s", "Angular speed", "angular speed (°/s)", "°/s", cfg, experiment)


def fig_orientation(table: pd.DataFrame, cfg: AnalysisConfig) -> go.Figure:
    tb = _stride(table, cfg.max_plot_points)
    t, yaw, pitch = _breaks(tb, "t", "yaw_deg", "pitch_deg")
    # break the yaw line where it wraps around +-180 degrees instead of drawing a vertical stroke
    jump = np.where(np.abs(np.diff(np.nan_to_num(yaw, nan=0.0))) > 180.0)[0] + 1
    yaw = np.insert(yaw, jump, np.nan)
    t_yaw = np.insert(t, jump, np.nan)
    fig = go.Figure()
    fig.add_trace(go.Scatter(x=t_yaw, y=yaw, mode="lines", name="Yaw (0° = +Z)", line=dict(color=C["s1"], width=2),
                             hovertemplate="%{y:.1f}°<extra>Yaw</extra>"))
    fig.add_trace(go.Scatter(x=t, y=pitch, mode="lines", name="Pitch (+ up)", line=dict(color=C["s2"], width=2),
                             hovertemplate="%{y:.1f}°<extra>Pitch</extra>"))
    fig.update_layout(**_base_layout(height=340, hovermode="x unified"))
    fig.update_xaxes(title=dict(text="session time (s)"))
    fig.update_yaxes(title=dict(text="angle (°)"))
    return fig


def fig_heatmap(table: pd.DataFrame, spatial: Dict[str, Any], cfg: AnalysisConfig) -> go.Figure:
    hm = spatial["_heatmap"]
    xe, ze, occ = hm["x_edges"], hm["z_edges"], hm["occupancy_s"].copy()
    xc, zc = (xe[:-1] + xe[1:]) / 2.0, (ze[:-1] + ze[1:]) / 2.0
    occ[occ <= 0] = np.nan
    fig = go.Figure()
    fig.add_trace(go.Heatmap(
        x=xc, y=zc, z=occ, colorscale=HEAT_SCALE, zmin=0, hoverongaps=False, name="Dwell time",
        colorbar=dict(title=dict(text="dwell (s)", side="right", font=dict(color=C["ink2"])), tickfont=dict(color=C["ink2"]),
                      thickness=10, outlinewidth=0, len=0.8, y=0.45, yanchor="middle"),
        hovertemplate="X %{x:.2f} m, Z %{y:.2f} m<br>dwell %{z:.1f} s<extra></extra>"))
    tb = _stride(table, cfg.max_plot_points)
    px, pz = _breaks(tb, "x", "z")
    fig.add_trace(go.Scatter(x=px, y=pz, mode="lines", name="Head path", line=dict(color="rgba(239,239,239,0.35)", width=1),
                             hoverinfo="skip"))
    for zone in spatial.get("zones", []):
        r = zone["radius_m"]
        fig.add_shape(type="circle", x0=zone["x"] - r, x1=zone["x"] + r, y0=zone["z"] - r, y1=zone["z"] + r,
                      line=dict(color=C["ink2"], width=1, dash="dot"), layer="above")
        label = zone["name"].replace(" proximity", "").replace("Target ", "")
        fig.add_annotation(x=zone["x"], y=zone["z"], text=esc(label), showarrow=False, font=dict(color=C["ink"], size=12))
    arena = spatial.get("arena")
    if arena:
        fig.add_shape(type="rect", x0=arena["x_min"], x1=arena["x_max"], y0=arena["z_min"], y1=arena["z_max"],
                      line=dict(color=C["axis"], width=2), layer="above")
    fig.update_layout(**_base_layout(height=520, showlegend=False, margin=dict(l=62, r=18, t=24, b=52)))
    fig.update_xaxes(title=dict(text="X east (m)"), constrain="domain", zeroline=False)
    fig.update_yaxes(title=dict(text="Z north (m)"), scaleanchor="x", scaleratio=1, constrain="domain", zeroline=False)
    return fig


def fig_timeline(experiment: Dict[str, Any], t_end: Optional[float]) -> go.Figure:
    targets = [t for t in experiment.get("targets", []) if t.get("activated_t_s") is not None]
    if not targets:
        return _na_figure("N/A — the CSV has no TargetActivated events, so no target timeline can be derived.")
    labels = ["Target %s" % t["id"] for t in targets]
    has_wrong = any(w.get("expected") is not None for w in experiment.get("wrong_list", []))
    fig = go.Figure()
    found_x, found_y, found_text = [], [], []
    for tgt, label in zip(targets, labels):
        if tgt["found"] and tgt["found_t_s"] is not None:
            width = tgt["found_t_s"] - tgt["activated_t_s"]
            fig.add_trace(go.Bar(
                y=[label], x=[width], base=[tgt["activated_t_s"]], orientation="h", name="Search (activated → found)",
                legendgroup="search", showlegend=not found_x, marker=dict(color=C["s1"], line=dict(width=0)),
                text=["%.1f s" % (tgt["search_time_s"] if tgt["search_time_s"] is not None else width)],
                textposition="inside", insidetextanchor="middle", textfont=dict(color="#ffffff", size=12),
                hovertemplate=("Target %s<br>activated %.1f s → found %.1f s<br>search time %s s<extra></extra>"
                               % (esc(tgt["id"]), tgt["activated_t_s"], tgt["found_t_s"], fnum(tgt["search_time_s"], 2)))))
            found_x.append(tgt["found_t_s"]); found_y.append(label); found_text.append(tgt["id"])
        elif t_end is not None:
            fig.add_trace(go.Bar(
                y=[label], x=[max(0.0, t_end - tgt["activated_t_s"])], base=[tgt["activated_t_s"]], orientation="h",
                name="Not found before the session ended", legendgroup="nf", showlegend=True,
                marker=dict(color="rgba(0,0,0,0)", line=dict(color=C["ink2"], width=1), pattern=dict(shape="/", fgcolor=C["ink2"])),
                hovertemplate="Target %s<br>activated %.1f s, NOT found<extra></extra>" % (esc(tgt["id"]), tgt["activated_t_s"])))
    if found_x:
        fig.add_trace(go.Scatter(x=found_x, y=found_y, mode="markers", name="Target found",
                                 marker=dict(symbol="diamond", size=11, color=C["ink"], line=dict(color=C["surface"], width=2)),
                                 hovertemplate="found at %{x:.1f} s<extra></extra>"))
    wrong = [w for w in experiment.get("wrong_list", []) if w.get("expected") is not None]
    if wrong:
        fig.add_trace(go.Scatter(
            x=[w["t_s"] for w in wrong], y=[WRONG_ROW] * len(wrong), mode="markers",
            name="Wrong selection", marker=dict(symbol="x", size=11, color=C["critical"], line=dict(width=2, color=C["critical"])),
            customdata=[[w.get("selected") or "?", w.get("expected") or "?", w.get("input") or "?"] for w in wrong],
            hovertemplate=("wrong selection at %{x:.1f} s<br>selected target %{customdata[0]}, active target was %{customdata[1]}"
                           "<br>input: %{customdata[2]}<extra></extra>")))
    fig.update_layout(**_base_layout(height=340, barmode="overlay"))
    fig.update_xaxes(title=dict(text="session time (s)"), rangemode="tozero")
    fig.update_yaxes(autorange="reversed", categoryorder="array", categoryarray=labels + ([WRONG_ROW] if has_wrong else []), title=dict(text=""))
    return fig


# ---------------------------------------------------------------------------------------------
# HTML assembly
# ---------------------------------------------------------------------------------------------
CSS = """
:root{color-scheme:dark;--page:#0b0b0b;--surface:#191919;--ink:#efefef;--ink2:#9c9c9c;--grid:#303030;--axis:#474747;
--s1:#338ced;--s2:#e76624;--good:#0aac23;--warning:#fbb315;--critical:#dc3c3d;--ring:rgba(255,255,255,.10)}
*{box-sizing:border-box}
body{margin:0;background:var(--page);color:var(--ink);font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
.wrap{max-width:1320px;margin:0 auto;padding:28px 24px 48px}
header h1{font-size:26px;font-weight:600;margin:0 0 6px;letter-spacing:-.01em}
header .meta{color:var(--ink2);display:flex;flex-wrap:wrap;gap:8px 18px;align-items:center}
.chip{display:inline-flex;gap:6px;align-items:center;padding:2px 10px;border:1px solid var(--ring);border-radius:999px;
background:var(--surface);color:var(--ink);font-size:12.5px}
.chip b{font-weight:600}
.kpis{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:14px;margin:22px 0}
.kpi{background:var(--surface);border:1px solid var(--ring);border-radius:12px;padding:16px 18px}
.kpi .label{color:var(--ink2);font-size:12px;text-transform:uppercase;letter-spacing:.06em}
.kpi .value{font-size:30px;font-weight:600;margin:6px 0 2px;letter-spacing:-.02em}
.kpi .unit{font-size:15px;font-weight:500;color:var(--ink2);margin-left:4px}
.kpi .sub{color:var(--ink2);font-size:12px}
.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}
.card{background:var(--surface);border:1px solid var(--ring);border-radius:12px;padding:16px 16px 10px;min-width:0}
.card.full{grid-column:1/-1}
.card h2{font-size:15px;font-weight:600;margin:0 0 2px}
.card .sub{color:var(--ink2);font-size:12.5px;margin:0 0 8px}
table.tbl{border-collapse:collapse;width:100%;font-size:13px}
.tbl th{color:var(--ink2);font-weight:500;text-align:left;border-bottom:1px solid var(--axis);padding:6px 10px}
.tbl td{border-bottom:1px solid var(--grid);padding:6px 10px;font-variant-numeric:tabular-nums}
.tbl td.na{color:var(--ink2)}
.note{color:var(--ink2);font-size:12.5px}
.warn{border-left:3px solid var(--warning);padding:6px 12px;margin:8px 0;background:rgba(251,179,21,.06)}
details summary{cursor:pointer;color:var(--ink)}
code{background:#0b0b0b;border:1px solid var(--ring);border-radius:4px;padding:1px 5px;font-size:12px}
footer{color:var(--ink2);font-size:12px;margin-top:26px}
@media(max-width:1000px){.kpis{grid-template-columns:repeat(2,minmax(0,1fr))}.grid{grid-template-columns:1fr}}
"""


def _kpi(label: str, value: Optional[str], unit: str, sub: str, na_reason: Optional[str] = None) -> str:
    shown = value if value is not None else "N/A"
    title = ' title="%s"' % esc(na_reason) if (value is None and na_reason) else ""
    unit_html = '<span class="unit">%s</span>' % esc(unit) if (value is not None and unit) else ""
    return ('<div class="kpi"%s><div class="label">%s</div><div class="value">%s%s</div><div class="sub">%s</div></div>'
            % (title, esc(label), esc(shown), unit_html, esc(sub)))


def _table(headers: List[str], rows: List[List[str]], empty: str = "None") -> str:
    if not rows:
        return '<p class="note">%s</p>' % esc(empty)
    head = "".join("<th>%s</th>" % esc(h) for h in headers)
    body = ""
    for row in rows:
        cells = "".join('<td class="%s">%s</td>' % ("na" if c == "N/A" else "", esc(c)) for c in row)
        body += "<tr>%s</tr>" % cells
    return '<table class="tbl"><thead><tr>%s</tr></thead><tbody>%s</tbody></table>' % (head, body)


def _status_chip(status: str) -> str:
    if status == "Completed":
        icon, color = "✔", C["good"]
    elif status in ("Aborted", "Restarted", "Interrupted"):
        icon, color = "▲", C["warning"]
    else:
        icon, color = "?", C["ink2"]
    return '<span class="chip"><span style="color:%s">%s</span> <b>%s</b></span>' % (color, icon, esc(status))


def _plot_html(fig: go.Figure, div_id: str) -> str:
    return fig.to_html(full_html=False, include_plotlyjs=False, div_id=div_id, config=PLOT_CONFIG)


def build_report(*, session, motion, spatial: Dict[str, Any], experiment: Dict[str, Any], summary: Dict[str, Any],
                 cfg: AnalysisConfig, out_dir: Path, comparison_html: str = "", plotlyjs: str = "inline") -> Path:
    table = motion.table
    lin, rot = motion.linear, motion.rotational
    kp = summary["kpis"]
    na = summary["not_available"]

    # ---- KPIs --------------------------------------------------------------------------------
    dur = kp.get("experiment_duration_s")
    dist = kp.get("total_distance_m")
    speed = kp.get("mean_speed_m_s")
    ang = kp.get("total_angular_movement_deg")
    found, expected = kp.get("targets_found"), kp.get("targets_expected")
    excluded_m = lin.get("excluded_distance_m") or 0.0
    kpis = "".join([
        _kpi("Experiment Duration", fnum(dur, 1) if dur is not None else None, "s",
             (fclock(dur) + " min:s · " + (experiment.get("experiment_duration_basis") or "")) if dur is not None else "",
             na.get("experiment.experiment_duration_s")),
        _kpi("Total Distance", fnum(dist, 2) if dist is not None else None, "m",
             "valid intervals only" + (" (%.1f m excluded)" % excluded_m if excluded_m > 0 else ""),
             na.get("linear.path_length_m")),
        _kpi("Mean Speed", fnum(speed, 2) if speed is not None else None, "m/s", "path length / valid time",
             na.get("linear.mean_speed_m_s")),
        _kpi("Total Angular Movement", fnum(ang, 0) if ang is not None else None, "°",
             ("= %.1f full turns, cumulative" % (ang / 360.0)) if ang is not None else "", na.get("rotational.total_angular_movement_deg")),
        _kpi("Targets Found", ("%d / %s" % (found, expected if expected is not None else "?")) if found is not None else None, "",
             "from TargetSelected events" if expected is not None else "total not logged", na.get("experiment.targets_found")),
    ])

    # ---- figures -------------------------------------------------------------------------------
    t_end = float(table["t"].iloc[-1])
    figs = {
        "traj3d": fig_trajectory_3d(table, cfg),
        "heat": fig_heatmap(table, spatial, cfg),
        "speed": fig_speed(table, cfg, experiment),
        "ang": fig_angular(table, cfg, experiment),
        "orient": fig_orientation(table, cfg),
        "timeline": fig_timeline(experiment, t_end),
    }
    if len(table) < 2:
        for key in ("speed", "ang"):
            figs[key] = _na_figure("N/A — at least 2 valid pose samples are needed for rates.")
    div = {k: _plot_html(f, "plot-" + k) for k, f in figs.items()}

    # ---- tables --------------------------------------------------------------------------------
    target_rows = []
    for tgt in experiment.get("targets", []):
        target_rows.append([
            tgt["id"], str(tgt["order_index"]), fnum(tgt["activated_t_s"], 1), fnum(tgt["found_t_s"], 1),
            fnum(tgt["search_time_s"], 2), tgt["search_time_source"] or "N/A",
            str(tgt["wrong_selections_before_found"]), tgt.get("condition_at_activation") or "N/A",
        ])
    wrong_rows = [[fnum(w["t_s"], 1), w.get("selected") or "N/A", w.get("expected") or "N/A", w.get("input") or "N/A"]
                  for w in experiment.get("wrong_list", [])]
    zone_rows = [[z["name"], fnum(z["dwell_time_s"], 1), fnum(100 * z["fraction_of_valid_time"], 1) if z["fraction_of_valid_time"] is not None else "N/A",
                  str(z["entries"]), fnum(z["first_entry_t_s"], 1)] for z in spatial.get("zones", [])]

    def row(label, value, digits=2, unit=""):
        return [label, fnum(value, digits, unit)]

    motion_rows = [
        row("Path length (3-D)", lin.get("path_length_m"), 2, "m"), row("Path length (floor plane)", lin.get("path_length_xz_m"), 2, "m"),
        row("Net displacement start → end", lin.get("net_displacement_m"), 2, "m"),
        row("Mean speed", lin.get("mean_speed_m_s"), 3, "m/s"), row("Median speed", lin.get("median_speed_m_s"), 3, "m/s"),
        row("95th percentile speed", lin.get("p95_speed_m_s"), 3, "m/s"), row("Max speed", lin.get("max_speed_m_s"), 3, "m/s"),
        row("Movement duration (speed ≥ %.2f m/s)" % cfg.stationary_speed_m_s, lin.get("movement_duration_s"), 1, "s"),
        row("Stationary duration (speed < %.2f m/s)" % cfg.stationary_speed_m_s, lin.get("stationary_duration_s"), 1, "s"),
        row("Total angular movement", rot.get("total_angular_movement_deg"), 1, "°"),
        row("Net angular displacement start → end", rot.get("net_angular_displacement_deg"), 1, "°"),
        row("Total yaw movement (|Δyaw|)", rot.get("total_yaw_movement_deg"), 1, "°"),
        row("Total pitch movement (|Δpitch|)", rot.get("total_pitch_movement_deg"), 1, "°"),
        row("Mean angular speed", rot.get("mean_angular_speed_deg_s"), 1, "°/s"),
        row("95th percentile angular speed", rot.get("p95_angular_speed_deg_s"), 1, "°/s"),
        row("Max angular speed", rot.get("max_angular_speed_deg_s"), 1, "°/s"),
    ]
    cov = spatial.get("coverage")
    motion_rows.append(["Arena coverage (%.2f m cells)" % cfg.grid_cell_m,
                        ("%.1f %% (%d / %d cells)" % (100 * cov["fraction"], cov["visited_cells"], cov["total_cells"])) if cov else "N/A"])

    q = summary["data_quality"]
    quality_rows = [
        ["Rows in CSV", str(q.get("rows_total"))], ["Pose samples used", str(q.get("pose_samples_used"))],
        ["Event rows", str(q.get("event_rows"))],
        ["Estimated sample rate", fnum(q.get("estimated_sample_rate_hz"), 1, "Hz")],
        ["Pose rows dropped (missing/NaN values)", str(q.get("pose_rows_dropped_missing_values"))],
        ["Pose rows dropped (invalid quaternion)", str(q.get("pose_rows_dropped_invalid_quaternion"))],
        ["Duplicate timestamps removed", str(q.get("pose_rows_duplicate_timestamps_removed"))],
        ["Rows out of time order (sorted)", str(q.get("pose_rows_out_of_order"))],
        ["Data gaps (> %.1f s)" % cfg.max_gap_s, "%d (%.1f s total)" % (lin.get("n_gap_intervals", 0), lin.get("gap_total_s", 0.0))],
        ["Discontinuities (teleport / tracking jump)", "%d (%.1f m, %.0f° excluded)" % (lin.get("n_discontinuities", 0), lin.get("excluded_distance_m", 0.0), rot.get("excluded_angle_deg", 0.0))],
        ["Quaternions with |q| off by > %.0f %%" % (100 * cfg.quaternion_norm_tolerance), str(q.get("quaternion_norm_out_of_tolerance"))],
    ]

    warn_html = ""
    for w in summary.get("warnings", []):
        warn_html += '<div class="warn">%s</div>' % esc(w)
    na_items = "".join("<li><code>%s</code> — %s</li>" % (esc(k), esc(v)) for k, v in na.items())
    na_html = ('<details><summary>%d metric(s) reported as N/A (with reasons)</summary><ul class="note">%s</ul></details>' % (len(na), na_items)) if na else ""

    definitions = """
<ul class="note">
<li><b>Samples:</b> periodic head poses (<code>record_type=Pose</code>, world space, Unity X east / Y up / Z north). Rotation uses the quaternion angle of the relative rotation between consecutive samples, which is independent of the q / −q sign.</li>
<li><b>Valid intervals:</b> a sample-to-sample step is excluded from every length / rate / time statistic when it is a data gap (&gt; %(gap).1f s) or physically implausible (&gt; %(vj).1f m/s or &gt; %(aj).0f°/s, e.g. a teleport). Excluded steps are counted above and shaded in the time-series charts.</li>
<li><b>Mean speed</b> = path length / valid time. <b>Path length</b> is the summed head displacement and includes tracking jitter (no filtering is applied).</li>
<li><b>Stationary</b> = speed below %(st).2f m/s (configurable). <b>Total angular movement</b> = sum of the angle between consecutive orientations.</li>
<li><b>Heatmap:</b> seconds spent per %(cell).2f m floor cell. <b>Coverage</b> is the share of arena-rectangle cells touched by the path; obstacles are not subtracted.</li>
<li><b>Experiment duration / completion time</b> is the value Unity logged in <code>ExperimentCompleted</code> and includes the found-animation and transition pauses between targets; <b>search time</b> (per target) is the time from target activation to the correct selection.</li>
<li>Target markers and zone circles come from the lab layout in the analysis config, not from the CSV.</li>
</ul>""" % {"gap": cfg.max_gap_s, "vj": cfg.discontinuity_speed_m_s, "aj": cfg.discontinuity_angular_deg_s,
           "st": cfg.stationary_speed_m_s, "cell": cfg.grid_cell_m}

    if plotlyjs == "inline":
        js_tag = "<script>%s</script>" % plotly.offline.get_plotlyjs()
    elif plotlyjs == "directory":
        (out_dir / "plotly.min.js").write_text(plotly.offline.get_plotlyjs(), encoding="utf-8")
        js_tag = '<script src="plotly.min.js"></script>'
    elif plotlyjs == "cdn":
        js_tag = '<script src="https://cdn.plot.ly/plotly-%s.min.js"></script>' % plotly.offline.get_plotlyjs_version()
    else:
        raise ValueError("plotlyjs must be inline, directory or cdn")

    meta = summary["session"]
    page = """<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>%(title)s</title><style>%(css)s</style>%(js)s</head>
<body><div class="wrap">
<header><h1>%(title)s</h1>
<div class="meta"><span class="chip">Session <b>%(sid)s</b></span><span class="chip">Condition <b>%(cond)s</b></span>%(status)s
<span class="chip">Started <b>%(start)s</b></span></div></header>
%(warn)s
<section class="kpis">%(kpis)s</section>
<section class="grid">
<div class="card"><h2>3D head trajectory</h2><p class="sub">Drag to rotate, scroll to zoom. Colour = time; circle = start, diamond = end.</p>%(traj3d)s</div>
<div class="card"><h2>XZ spatial heatmap</h2><p class="sub">Dwell time per %(cell).2f m floor cell, head path overlaid; dotted circles = start pad and target proximity zones.</p>%(heat)s</div>
<div class="card"><h2>Head speed vs time</h2><p class="sub">Dotted lines mark the moment each target was found; shaded bands are excluded intervals.</p>%(speed)s</div>
<div class="card"><h2>Angular velocity vs time</h2><p class="sub">Magnitude of head rotation rate from the quaternion samples.</p>%(ang)s</div>
<div class="card"><h2>Head orientation over time</h2><p class="sub">Yaw (heading) and pitch derived from the head forward vector.</p>%(orient)s</div>
<div class="card"><h2>Target completion timeline</h2><p class="sub">Each bar runs from target activation to the correct selection.</p>%(timeline)s</div>
<div class="card"><h2>Targets</h2>%(target_tbl)s<h2 style="margin-top:14px">Wrong selections</h2>%(wrong_tbl)s</div>
<div class="card"><h2>Zone dwell</h2><p class="sub">Time inside each configured zone (valid intervals).</p>%(zone_tbl)s</div>
<div class="card full"><h2>Motion summary</h2>%(motion_tbl)s</div>
<div class="card full"><h2>Condition comparison</h2>%(comparison)s</div>
<div class="card"><h2>Data quality</h2>%(quality_tbl)s</div>
<div class="card"><h2>Definitions &amp; caveats</h2>%(definitions)s%(na)s</div>
</section>
<footer>Generated %(generated)s UTC · input <code>%(input_name)s</code> (sha256 %(sha)s) · numbers are computed from this recording only.</footer>
</div></body></html>""" % {
        "title": esc(TITLE), "css": CSS, "js": js_tag, "sid": esc(meta["session_id"]), "cond": esc(experiment.get("condition_label")),
        "status": _status_chip(experiment.get("status", "Unknown")), "start": esc((meta.get("started_utc") or "N/A")[:19].replace("T", " ") + " UTC" if meta.get("started_utc") else "N/A"),
        "warn": warn_html, "kpis": kpis, "cell": cfg.grid_cell_m,
        "traj3d": div["traj3d"], "heat": div["heat"], "speed": div["speed"], "ang": div["ang"], "orient": div["orient"],
        "timeline": div["timeline"],
        "target_tbl": _table(["Target", "Order", "Activated (s)", "Found (s)", "Search time (s)", "Source", "Wrong before found", "Condition"],
                             target_rows, "N/A — no target events in this CSV."),
        "wrong_tbl": _table(["Time (s)", "Selected", "Expected", "Input"], wrong_rows, "No wrong selections logged."),
        "zone_tbl": _table(["Zone", "Dwell (s)", "% of valid time", "Entries", "First entry (s)"], zone_rows, "No zones configured."),
        "motion_tbl": _table(["Metric", "Value"], motion_rows),
        "comparison": comparison_html or '<p class="note">No condition comparison available.</p>',
        "quality_tbl": _table(["Item", "Value"], quality_rows), "definitions": definitions, "na": na_html,
        "generated": esc(summary["generated_utc"]), "input_name": esc(Path(summary["input"]["csv_path"]).name),
        "sha": esc(summary["input"]["sha256"][:12]),
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / "report.html"
    fd, tmp_name = tempfile.mkstemp(prefix=".report-", suffix=".html", dir=str(out_dir))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(page)
        os.chmod(tmp_name, 0o644)
        os.replace(tmp_name, target)
    finally:
        if os.path.exists(tmp_name):
            os.unlink(tmp_name)
    return target
