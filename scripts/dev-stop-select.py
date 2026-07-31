#!/usr/bin/env python3
"""Select processes provably attached to one Aspire AppHost from a /proc snapshot."""

from __future__ import annotations

import argparse
import os
import sys


ProcessRecord = tuple[int, int, int, str, str]


def parse_snapshot(lines: list[str]) -> dict[int, ProcessRecord]:
    processes: dict[int, ProcessRecord] = {}
    for line in lines:
        fields = line.rstrip("\n").split("\t", maxsplit=5)
        if len(fields) != 6:
            continue
        try:
            pid, ppid, sid, starttime = map(int, fields[:4])
        except ValueError:
            continue
        processes[pid] = (ppid, sid, starttime, fields[4], fields[5])
    return processes


def read_proc_record(proc_root: str, pid: int) -> ProcessRecord | None:
    try:
        stat = open(os.path.join(proc_root, str(pid), "stat"), encoding="utf-8").read()
        end = stat.rfind(")")
        if end < 0:
            return None
        comm = stat[stat.find("(") + 1 : end]
        fields = stat[end + 2 :].split()
        ppid, sid, starttime = int(fields[1]), int(fields[3]), int(fields[19])
        raw_args = open(os.path.join(proc_root, str(pid), "cmdline"), "rb").read()
        command = raw_args.replace(b"\0", b" ").decode("utf-8", errors="replace").strip()
        return ppid, sid, starttime, comm, command
    except (FileNotFoundError, PermissionError, ProcessLookupError, ValueError, IndexError):
        return None


def snapshot(proc_root: str) -> int:
    for entry in sorted(os.listdir(proc_root), key=lambda value: int(value) if value.isdigit() else -1):
        if not entry.isdigit():
            continue
        pid = int(entry)
        record = read_proc_record(proc_root, pid)
        if record is None:
            continue
        ppid, sid, starttime, comm, command = record
        print(pid, ppid, sid, starttime, comm, command, sep="\t")
    return 0


def select(
    processes: dict[int, ProcessRecord],
    apphost_pid: int,
    apphost_path: str,
    protected: set[int],
) -> list[int]:
    def monitors_apphost(command: str) -> bool:
        tokens = command.split()
        return any(
            tokens[index] == "--monitor" and tokens[index + 1] == str(apphost_pid)
            for index in range(len(tokens) - 1)
        )

    anchors = {
        pid
        for pid, (_, _, _, comm, command) in processes.items()
        if comm == "dcp" and monitors_apphost(command)
    }

    app = processes.get(apphost_pid)
    if app is not None:
        anchors.add(apphost_pid)
        parent = app[0]
        while parent in processes:
            ppid, _, _, comm, command = processes[parent]
            if comm == "aspire" and apphost_path in command:
                anchors.add(parent)
            parent = ppid

    descendants = set(anchors)
    changed = True
    while changed:
        changed = False
        for pid, (ppid, _, _, _, _) in processes.items():
            if pid not in descendants and ppid in descendants:
                descendants.add(pid)
                changed = True

    return sorted(descendants - protected - {0, 1})


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apphost-pid", type=int)
    parser.add_argument("--apphost-path")
    parser.add_argument("--protected", default="")
    parser.add_argument("--proc-root", default="/proc")
    parser.add_argument("--snapshot", action="store_true")
    parser.add_argument("--session-id", type=int)
    parser.add_argument("--identity-matches", nargs=2, metavar=("PID", "STARTTIME"), type=int)
    args = parser.parse_args()
    if args.snapshot:
        return snapshot(args.proc_root)
    if args.identity_matches:
        pid, expected = args.identity_matches
        record = read_proc_record(args.proc_root, pid)
        return 0 if record is not None and record[2] == expected else 1
    if args.session_id is not None:
        protected = {int(value) for value in args.protected.split(",") if value.isdigit()}
        processes = parse_snapshot(sys.stdin.readlines())
        for pid, (_, sid, starttime, _, _) in sorted(processes.items()):
            if sid == args.session_id and pid not in protected and pid not in {0, 1}:
                print(pid, starttime, sep="\t")
        return 0
    if args.apphost_pid is None or args.apphost_path is None:
        parser.error("--apphost-pid and --apphost-path are required for selection")
    protected = {int(value) for value in args.protected.split(",") if value.isdigit()}
    processes = parse_snapshot(sys.stdin.readlines())
    for pid in select(processes, args.apphost_pid, args.apphost_path, protected):
        print(pid, processes[pid][2], sep="\t")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
