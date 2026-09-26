"""Regression tests against this worktree's running Debug app (no real toasts)."""

import json
import os
from pathlib import Path
import subprocess


APP = Path(__file__).resolve().parents[1] / "bin" / "JeekTokenPlanUsage.exe"
ADAPTER = Path(os.environ["LOCALAPPDATA"]) / "JeekTokenPlanUsage" / "Mcp" / "JeekTokenPlanUsageMcp.exe"
CYCLE_A = "2026-09-22T12:00:00Z"
CYCLE_B = "2026-09-22T17:00:00Z"


def sample(usage, reset=CYCLE_A, enabled=True):
    result = {"utilization": usage, "enabled": enabled}
    if reset is not None:
        result["resetsAt"] = reset
    return result


def rpc(surface, requests):
    completed = subprocess.run(
        [str(ADAPTER), "--surface", surface, "--app", str(APP), "--no-launch"],
        input="".join(json.dumps(request) + "\n" for request in requests),
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=60,
        check=True,
    )
    responses = [json.loads(line) for line in completed.stdout.splitlines() if line]
    assert len(responses) == len(requests), completed.stdout
    for request, response in zip(requests, responses):
        assert response["id"] == request["id"], response
        assert "error" not in response, response
        assert not response["result"].get("isError", False), response
    return [response["result"] for response in responses]


cases = [
    ("normal thresholds", [sample(u) for u in [79, 80, 80, 94, 95, 99, 100, 100]],
     [False, True, False, False, True, False, False, False]),
    ("already exhausted", [sample(u) for u in [100, 100, 101, 120, 99, 95]],
     [False] * 6),
    ("reset time changes while exhausted",
     [sample(100), sample(100, CYCLE_B), sample(100, None), sample(100)],
     [False] * 4),
    ("unknown reset time", [sample(u, None) for u in [100, 100, 99]],
     [False] * 3),
    ("new cycle re-arms", [sample(100)] + [sample(u, CYCLE_B) for u in [0, 80, 95, 100]],
     [False, False, True, True, False]),
    ("notifications disabled", [sample(80, enabled=False), sample(80), sample(95, enabled=False), sample(95)],
     [False, True, False, True]),
    ("exhausted while disabled", [sample(100, enabled=False), sample(100), sample(99)],
     [False] * 3),
    ("below full boundary", [sample(99.9), sample(100), sample(99.9)],
     [True, False, False]),
    ("no repeated alerts around thresholds", [sample(u) for u in [80, 79, 80, 95, 94, 95]],
     [True, False, False, True, False, False]),
    ("reported Claude jitter", [sample(80, "2026-09-26T06:09:59.528352Z"),
     sample(85, "2026-09-26T06:10:00Z"), sample(88, "2026-09-26T06:09:59.800Z"),
     sample(90, "2026-09-26T06:10:00.162269Z"),
     sample(95, "2026-09-26T06:10:00Z"), sample(99, "2026-09-26T06:10:00Z")],
     [True, False, False, False, True, False]),
    ("missing reset preserves history", [sample(80), sample(85, None), sample(88), sample(95, None)],
     [True, False, False, True]),
    ("first known reset preserves history", [sample(80, None), sample(85), sample(95)],
     [True, False, True]),
    ("stale reset cannot rearm", [sample(95, CYCLE_B), sample(88), sample(96, CYCLE_B)],
     [True, False, False]),
    ("one-minute boundary", [sample(80), sample(85, "2026-09-22T12:01:00Z"),
     sample(88, "2026-09-22T12:01:00.001Z")], [True, False, True]),
    ("exhausted then jitter", [sample(100), sample(99, "2026-09-22T12:00:00.500Z"),
     sample(95, None), sample(99)], [False] * 4),
    ("repeated jitter keeps anchor", [sample(80)] + [
        sample(88, reset) for _ in range(20) for reset in
        ["2026-09-22T12:00:00.600Z", "2026-09-22T11:59:59.800Z"]],
     [True] + [False] * 40),
]

requests = [{"jsonrpc": "2.0", "id": 0, "method": "tools/list"}]
for index, (_, samples, _) in enumerate(cases, 1):
    requests.append({
        "jsonrpc": "2.0",
        "id": index,
        "method": "tools/call",
        "params": {"name": "probe_threshold_notifications", "arguments": {"samples": samples}},
    })

results = rpc("debug", requests)
assert any(tool["name"] == "probe_threshold_notifications" for tool in results[0]["tools"])
for (name, _, expected), result in zip(cases, results[1:]):
    actual = [step["shouldNotify"] for step in result["structuredContent"]["results"]]
    assert actual == expected, f"{name}: expected {expected}, got {actual}"
    if name == "repeated jitter keeps anchor":
        assert len({step["cycleReset"] for step in result["structuredContent"]["results"]}) == 1
    print(f"PASS: {name}")

product = rpc("product", [requests[0]])[0]
assert product["tools"], "Product surface is unavailable."
assert not any(tool["name"] == "probe_threshold_notifications" for tool in product["tools"])
print("PASS: probe is absent from product surface")
print(f"All {len(cases)} notification cases and surface isolation passed.")
