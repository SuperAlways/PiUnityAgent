#!/usr/bin/env python3
"""Check PiUnityAgent deferred-capability ledger vs code annotations.

Usage:
  python scripts/check-deferred.py --root . --gate all
  python scripts/check-deferred.py --root . --current-stage S4 --gate exit

Rules:
  1. Every TODO(Sx, Dn) / "Deferred to Sx: ... (Dn)" in src/**/*.cs or
     tests/**/*.cs must reference an ID in docs/待办-后置能力.md.
  2. An entry marked 已完成 must not still have a code stub.
  3. With --current-stage, entries whose latest target stage is <= the current
     stage must be 已完成/重排期/已取消; otherwise the stage gate fails.
  4. Each entry must have a shape anchor or an acceptance-test anchor.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REGISTRY_REL = Path("docs") / "待办-后置能力.md"
CODE_DIRS = ("src", "tests")
VALID_STATUSES = {"未开工", "进行中", "已完成", "重排期", "已取消"}
DONE_STATUSES = {"已完成", "重排期", "已取消"}

TODO_RE = re.compile(r"TODO\([^)]*(D\d+)\)")
DEFERRED_RE = re.compile(r"Deferred to [^:\n]+:[^\n]*?(D\d+)")
STAGE_RE = re.compile(r"(2B|S\d+)")
STAGE_ORDER = {f"S{i}": i for i in range(1, 14)}
STAGE_ORDER["2B"] = 14


def fail(message: str) -> None:
    print(f"FAIL  {message}")


def parse_registry(root: Path) -> tuple[dict[str, dict[str, str]], list[str]]:
    path = root / REGISTRY_REL
    if not path.is_file():
        return {}, [f"registry not found: {path}"]

    entries: dict[str, dict[str, str]] = {}
    parse_errors: list[str] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| D"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 10:
            parse_errors.append(f"registry row has too few columns: {line[:80]}")
            continue
        entry_id = cells[0]
        if not re.fullmatch(r"D\d+", entry_id):
            parse_errors.append(f"invalid Dn id: {entry_id}")
            continue
        entries[entry_id] = {
            "id": entry_id,
            "capability": cells[1],
            "reference": cells[2],
            "shape_anchor": cells[3],
            "test_anchor": cells[4],
            "s3_state": cells[5],
            "target_stage": cells[6],
            "acceptance": cells[7],
            "status": cells[8],
            "commit": cells[9],
            "note": cells[10] if len(cells) > 10 else "",
        }
    return entries, parse_errors


def parse_stage_tokens(target_stage: str) -> list[int]:
    return [STAGE_ORDER[token] for token in STAGE_RE.findall(target_stage) if token in STAGE_ORDER]


def scan_code(root: Path, entries: dict[str, dict[str, str]]) -> list[str]:
    failures: list[str] = []
    markers: list[tuple[str, str, int, str]] = []
    for directory in CODE_DIRS:
        base = root / directory
        if not base.is_dir():
            continue
        for path in base.rglob("*.cs"):
            try:
                lines = path.read_text(encoding="utf-8").splitlines()
            except UnicodeDecodeError:
                lines = path.read_text(encoding="utf-8-sig").splitlines()
            for number, line in enumerate(lines, 1):
                for match in TODO_RE.finditer(line):
                    markers.append((match.group(1), str(path), number, "TODO"))
                for match in DEFERRED_RE.finditer(line):
                    markers.append((match.group(1), str(path), number, "Deferred"))

    marker_counts: dict[str, int] = {}
    for entry_id, path, line, kind in markers:
        marker_counts[entry_id] = marker_counts.get(entry_id, 0) + 1
        if entry_id not in entries:
            failures.append(f"{path}:{line}: {kind} references unknown {entry_id}")
        elif entries[entry_id]["status"] == "已完成":
            failures.append(
                f"{path}:{line}: {entry_id} is 已完成 but still has a {kind} stub"
            )

    if markers:
        print("code markers: " + ", ".join(
            f"{entry_id}={count}" for entry_id, count in sorted(marker_counts.items())
        ))
    else:
        print("code markers: none")
    return failures


def check_entries(entries: dict[str, dict[str, str]]) -> list[str]:
    failures: list[str] = []
    for entry in entries.values():
        entry_id = entry["id"]
        if entry["status"] not in VALID_STATUSES:
            failures.append(
                f"{entry_id}: invalid status '{entry['status']}' "
                f"(expected one of {', '.join(sorted(VALID_STATUSES))})"
            )
        shape = entry["shape_anchor"].strip("` ")
        test = entry["test_anchor"].strip("` ")
        if shape in {"", "—", "-"} and test in {"", "—", "-"}:
            failures.append(f"{entry_id}: needs a shape anchor or acceptance test anchor")
    return failures


def check_stage_gate(
    entries: dict[str, dict[str, str]], current_stage: str
) -> list[str]:
    if current_stage not in STAGE_ORDER:
        return [f"unknown current stage: {current_stage}"]
    current_order = STAGE_ORDER[current_stage]
    failures: list[str] = []
    for entry in entries.values():
        if entry["status"] in DONE_STATUSES:
            continue
        stages = parse_stage_tokens(entry["target_stage"])
        if not stages:
            # 后续扩展 / 无阶段目标：不参与当前阶段清账。
            continue
        latest_target = max(stages)
        if latest_target <= current_order:
            failures.append(
                f"{entry['id']}: target stage {entry['target_stage']} has passed "
                f"current {current_stage} but status is {entry['status']}"
            )
    return failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".", help="PiUnityAgent repository root")
    parser.add_argument(
        "--gate",
        choices=("all", "exit"),
        default="all",
        help="all = consistency only; exit = also check current-stage clean-up",
    )
    parser.add_argument(
        "--current-stage",
        default=None,
        help="stage being closed, e.g. S4 / S5 / S8 / S9 / 2B",
    )
    args = parser.parse_args()

    root = Path(args.root).resolve()
    entries, parse_errors = parse_registry(root)
    failures = list(parse_errors)
    failures += check_entries(entries)
    failures += scan_code(root, entries)

    if args.gate == "exit":
        if args.current_stage is None:
            failures.append("--gate exit requires --current-stage")
        else:
            failures += check_stage_gate(entries, args.current_stage)

    print(f"registry entries: {len(entries)}")
    if failures:
        for message in failures:
            fail(message)
        print(f"\n{len(failures)} deferred-governance failure(s)")
        return 1

    print("OK    deferred-capability ledger and code annotations are consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
