#!/usr/bin/env python3
"""Render an S1 run as a pass/fail table against the criteria in docs/20_SPIKES.md.

S1 is measured twice (docs/20 §S1). This reads either leg, or both:

  leg A  the collector alone      spikes/S1.EtwBudget --hours 48 --out s1.csv   ->  --csv s1.csv
  leg B  the Agent that ships     AppLedger.Agent.exe --console                 ->  --db  %LOCALAPPDATA%\\AppLedgerData\\appledger.db

Leg B carries no measurement-only code path: the Agent writes one health_minutes row per minute because
docs/15 §Agent self-watch says it must, and this script reads that table back. Measuring through the mechanism
that ships is the point - a separate measuring path can be right about a build that is wrong.

Usage:
  python tools/s1-report.py --db "%LOCALAPPDATA%\\AppLedgerData\\appledger.db"
  python tools/s1-report.py --csv s1.csv
  python tools/s1-report.py --db ... --csv ... --idle 03:00-03:10

Stdlib only, so it runs on a clean box with no restore. Read-only: it never writes to the database, and it is
safe to run against a live Agent (WAL).
"""

import argparse
import csv
import datetime as dt
import json
import os
import shutil
import sqlite3
import sys
import tempfile

# docs/20_SPIKES.md §S1 "Pass". These are the whole contract; nothing below invents a threshold.
IDLE_CPU_PCT = 1.0
LOAD_CPU_PCT = 3.0
MAX_WS_MB = 100.0
WS_CHECKPOINT_HOURS = (24, 48)

# The procedure asks for "one 10-min idle window" inside each run. With no flag saying which one it was, the
# quietest contiguous ten minutes is that window by construction - so it is what the <1% criterion is read
# against. Named a proxy in the output rather than reported as if the run had been labelled.
IDLE_WINDOW_MINUTES = 10
ROLLING_MINUTES = 5


class Row:
    """One minute of a run, from either leg."""

    __slots__ = ("minute", "cpu_pct", "ws_mb", "events_lost", "sensors")

    def __init__(self, minute, cpu_pct, ws_mb, events_lost, sensors):
        self.minute = minute            # minutes since the run started
        self.cpu_pct = cpu_pct
        self.ws_mb = ws_mb
        self.events_lost = events_lost  # cumulative within one session
        self.sensors = sensors          # {name: state} or None


QUERY = ("SELECT ts, agent_cpu_pct, agent_ws, events_lost, sensors_json "
         "FROM health_minutes ORDER BY ts")


def query_health(path):
    """Read the table without disturbing a running Agent, and without needing it to be stopped.

    Read-only first, because the intended use is against a live 48-hour run. That fails when the database
    carries a WAL that needs recovering and nothing may write, so the fallback copies the database and its
    sidecars aside and reads the copy. Opening the original read-write instead would let this script recover
    someone else's journal, which is not a thing a reporting tool should ever do.
    """
    uri = "file:{}?mode=ro".format(path.replace("?", "%3f").replace("#", "%23"))
    try:
        with sqlite3.connect(uri, uri=True) as db:
            return db.execute(QUERY).fetchall()
    except sqlite3.OperationalError as error:
        if "no such table" in str(error):
            raise SystemExit(
                "leg B: {} has no health_minutes table - is that the Agent's database?".format(path))

        print("  read-only open failed ({}); reading a copy instead.".format(error))

    scratch = tempfile.mkdtemp(prefix="s1-report-")
    try:
        copy = os.path.join(scratch, "appledger.db")
        for suffix in ("", "-wal", "-shm"):
            if os.path.exists(path + suffix):
                shutil.copyfile(path + suffix, copy + suffix)
        with sqlite3.connect(copy) as db:
            return db.execute(QUERY).fetchall()
    finally:
        shutil.rmtree(scratch, ignore_errors=True)


def read_health_minutes(path):
    """Leg B. health_minutes, oldest first."""
    raw = query_health(path)

    if not raw:
        return [], None

    first_ts = raw[0][0]
    rows = []
    for ts, cpu, ws, lost, sensors_json in raw:
        try:
            sensors = json.loads(sensors_json) if sensors_json else None
        except ValueError:
            sensors = None
        rows.append(Row(
            minute=(ts - first_ts) / 60.0,
            cpu_pct=float(cpu or 0.0),
            ws_mb=float(ws or 0) / (1024.0 * 1024.0),
            events_lost=int(lost or 0),
            sensors=sensors,
        ))
    return rows, first_ts


def read_csv(path):
    """Leg A. The spike's 10-second CSV, folded to one row per minute so both legs compare like with like."""
    with open(path, newline="", encoding="utf-8") as handle:
        samples = list(csv.DictReader(handle))

    if not samples:
        return [], None

    buckets = {}
    for sample in samples:
        minute = int(float(sample["elapsed_s"]) // 60)
        buckets.setdefault(minute, []).append(sample)

    rows = []
    for minute in sorted(buckets):
        group = buckets[minute]
        rows.append(Row(
            minute=float(minute),
            cpu_pct=sum(float(s["cpu_pct"]) for s in group) / len(group),
            ws_mb=max(float(s["private_ws_mb"]) for s in group),
            events_lost=max(int(s["events_lost"]) for s in group),
            sensors=None,
        ))

    # Leg A's CSV carries two things health_minutes has no column for. Kept aside rather than folded into Row,
    # because only one leg can ever report them and a shared shape would imply otherwise.
    extras = {
        "handler_errors": max(int(s["handler_errors"]) for s in samples),
        "late_samples": max(int(s["late_samples"]) for s in samples),
        "idle_hours": sum(1 for s in samples if s["idle"] == "1") * 10 / 3600.0,
        "rows_written": max(int(s["rows_written"]) for s in samples),
        "db_mb": max(float(s["db_mb"]) for s in samples),
        "unattributed_events": max(int(s["unattributed_events"]) for s in samples),
    }
    return rows, extras


def rolling(rows, window):
    """(minute, mean cpu) over a trailing window, in the shape the budget is stated in."""
    out = []
    for i in range(len(rows)):
        chunk = rows[max(0, i - window + 1):i + 1]
        out.append((rows[i].minute, sum(r.cpu_pct for r in chunk) / len(chunk)))
    return out


def quietest_window(rows, width):
    """The lowest-mean contiguous stretch: the procedure's idle window, found rather than declared."""
    if len(rows) < width:
        return None
    best = None
    for i in range(len(rows) - width + 1):
        chunk = rows[i:i + width]
        mean = sum(r.cpu_pct for r in chunk) / width
        if best is None or mean < best[0]:
            best = (mean, chunk[0].minute, chunk[-1].minute)
    return best


def lost_total(rows):
    """Sum of positive deltas.

    EventsLost is cumulative per session and a sensor that restarts brings its own counter back to zero
    (docs/24_ADR.md, Findings). Taking last-minus-first would report a restart as negative loss, and taking the
    maximum would miss everything lost before it.
    """
    total, previous = 0, None
    for row in rows:
        if previous is not None and row.events_lost > previous:
            total += row.events_lost - previous
        previous = row.events_lost
    return total


def gaps(rows, tolerance=2.0):
    """Minutes with no row: a restart, a sleep, or an Agent that stopped writing. All three are worth seeing."""
    found = []
    for a, b in zip(rows, rows[1:]):
        if b.minute - a.minute > tolerance:
            found.append((a.minute, b.minute))
    return found


def sensor_trouble(rows):
    """Sensors seen in any state other than Running, with how many minutes they spent there."""
    trouble = {}
    for row in rows:
        for name, state in (row.sensors or {}).items():
            if state != "Running":
                trouble[(name, state)] = trouble.get((name, state), 0) + 1
    return trouble


def ws_at(rows, hours):
    """Working set at an hour mark, or None when the run did not reach it."""
    target = hours * 60.0
    reached = [r for r in rows if r.minute >= target]
    return reached[0].ws_mb if reached else None


def parse_idle(spec, first_ts):
    """--idle HH:MM-HH:MM, local time, for a run whose idle window is known rather than inferred."""
    if not spec or first_ts is None:
        return None
    try:
        start_s, end_s = spec.split("-")
        start = dt.datetime.strptime(start_s.strip(), "%H:%M").time()
        end = dt.datetime.strptime(end_s.strip(), "%H:%M").time()
    except ValueError:
        print("  --idle wants HH:MM-HH:MM; ignoring '{}'".format(spec))
        return None
    return start, end


def in_idle_window(row, window, first_ts):
    start, end = window
    stamp = dt.datetime.fromtimestamp(first_ts + row.minute * 60).time()
    return start <= stamp <= end if start <= end else (stamp >= start or stamp <= end)


def verdict(ok):
    return "PASS" if ok else "FAIL"


def report(label, rows, first_ts, extras, idle_spec):
    print()
    print("=" * 78)
    print("{}  -  {} minutes ({:.1f} h)".format(label, len(rows), rows[-1].minute / 60.0))
    if first_ts:
        started = dt.datetime.fromtimestamp(first_ts)
        print("started {} local".format(started.strftime("%Y-%m-%d %H:%M")))
    print("=" * 78)

    smoothed = rolling(rows, ROLLING_MINUTES)
    peak_cpu = max(v for _, v in smoothed)
    peak_at = max(smoothed, key=lambda p: p[1])[0]

    if idle_spec and first_ts is None:
        print("  --idle is wall-clock and this leg records elapsed time only; using the proxy below.")

    window = parse_idle(idle_spec, first_ts)
    if window:
        idle_rows = [r for r in rows if in_idle_window(r, window, first_ts)]
        idle_source = "declared window {}".format(idle_spec)
        idle_cpu = (sum(r.cpu_pct for r in idle_rows) / len(idle_rows)) if idle_rows else None
    else:
        quiet = quietest_window(rows, IDLE_WINDOW_MINUTES)
        idle_source = ("quietest {}-min stretch, minute {:.0f}-{:.0f} (proxy)".format(
            IDLE_WINDOW_MINUTES, quiet[1], quiet[2]) if quiet else "run too short")
        idle_cpu = quiet[0] if quiet else None

    checks = []

    if idle_cpu is None:
        checks.append(("CPU, idle", "n/a", "< {:.0f} %".format(IDLE_CPU_PCT), None, idle_source))
    else:
        checks.append(("CPU, idle", "{:.3f} %".format(idle_cpu), "< {:.0f} %".format(IDLE_CPU_PCT),
                       idle_cpu < IDLE_CPU_PCT, idle_source))

    checks.append(("CPU, peak 5-min", "{:.3f} %".format(peak_cpu), "< {:.0f} %".format(LOAD_CPU_PCT),
                   peak_cpu < LOAD_CPU_PCT, "at minute {:.0f}".format(peak_at)))

    for hours in WS_CHECKPOINT_HOURS:
        value = ws_at(rows, hours)
        if value is None:
            checks.append(("Private WS at {} h".format(hours), "n/a", "< {:.0f} MB".format(MAX_WS_MB), None,
                           "run ended before hour {}".format(hours)))
        else:
            checks.append(("Private WS at {} h".format(hours), "{:.1f} MB".format(value),
                           "< {:.0f} MB".format(MAX_WS_MB), value < MAX_WS_MB, ""))

    peak_ws = max(r.ws_mb for r in rows)
    checks.append(("Private WS, peak", "{:.1f} MB".format(peak_ws), "< {:.0f} MB".format(MAX_WS_MB),
                   peak_ws < MAX_WS_MB, "first {:.1f} MB, last {:.1f} MB".format(rows[0].ws_mb, rows[-1].ws_mb)))

    lost = lost_total(rows)
    checks.append(("Events lost", str(lost), "0", lost == 0,
                   "FileIO loss during the 1 GB copy is allowed - check the log before failing on this"))

    if extras is not None:
        checks.append(("Handler errors", str(extras["handler_errors"]), "0", extras["handler_errors"] == 0, ""))
        checks.append(("Late samples", str(extras["late_samples"]), "0", extras["late_samples"] == 0,
                       "non-zero means the clock stepped back"))

    width = max(len(name) for name, *_ in checks)
    print()
    for name, measured, budget, ok, note in checks:
        mark = "----" if ok is None else verdict(ok)
        line = "  {:<{w}}  {:>12}   {:<12} {}".format(name, measured, budget, mark, w=width)
        print(line + ("   {}".format(note) if note else ""))

    print()
    if extras is not None:
        print("  rows written {}   database {:.1f} MB   unattributed events {}   idle profile {:.1f} h".format(
            extras["rows_written"], extras["db_mb"], extras["unattributed_events"], extras["idle_hours"]))
    else:
        print("  handler errors and late samples have no health_minutes column - leg A reports them")

    trouble = sensor_trouble(rows)
    if trouble:
        print()
        print("  sensor states other than Running:")
        for (name, state), minutes in sorted(trouble.items()):
            print("    {:<16} {:<14} {} min".format(name, state, minutes))

    holes = gaps(rows)
    if holes:
        print()
        print("  gaps in the minute series (restart, sleep, or a stalled writer):")
        for a, b in holes[:10]:
            print("    minute {:.0f} -> {:.0f}   ({:.0f} min missing)".format(a, b, b - a - 1))
        if len(holes) > 10:
            print("    ... and {} more".format(len(holes) - 10))

    decided = [ok for *_, ok, _ in checks if ok is not None]
    return all(decided) and bool(decided)


def main():
    parser = argparse.ArgumentParser(description="S1 pass/fail table (docs/20_SPIKES.md §S1).")
    parser.add_argument("--db", help="leg B: the Agent's SQLite database, read for health_minutes")
    parser.add_argument("--csv", help="leg A: the CSV written by spikes/S1.EtwBudget --hours")
    parser.add_argument("--idle", help="the run's declared idle window as HH:MM-HH:MM, local time")
    args = parser.parse_args()

    if not args.db and not args.csv:
        parser.error("give --db, --csv, or both")

    results = []

    if args.csv:
        path = os.path.expandvars(args.csv)
        if not os.path.exists(path):
            print("leg A: no such file: {}".format(path))
            return 2
        rows, extras = read_csv(path)
        if not rows:
            print("leg A: {} has no samples".format(path))
            return 2
        label = "leg A  collector alone, no pipe server"
        results.append((label, report(label, rows, None, extras, args.idle)))

    if args.db:
        path = os.path.expandvars(args.db)
        if not os.path.exists(path):
            print("leg B: no such database: {}".format(path))
            return 2
        rows, first_ts = read_health_minutes(path)
        if not rows:
            print("leg B: health_minutes is empty - the Agent writes one row per minute, so either it has not"
                  " run for a full minute or it is failing to write (check the log for HealthWriteFailed).")
            return 2
        label = "leg B  the Agent that ships"
        results.append((label, report(label, rows, first_ts, None, args.idle)))

    print()
    print("=" * 78)
    for label, ok in results:
        print("  {:<44} {}".format(label, verdict(ok)))
    print("=" * 78)
    print("Paste the tables above into docs/20_SPIKES.md, S1 Result.")

    return 0 if all(ok for _, ok in results) else 1


if __name__ == "__main__":
    sys.exit(main())
