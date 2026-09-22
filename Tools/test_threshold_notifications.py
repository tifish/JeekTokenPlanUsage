"""Regression tests against this worktree's running Debug app (no real toasts)."""

import json
from pathlib import Path
import subprocess


ADAPTER = Path(__file__).resolve().parents[1] / "bin" / "JeekTokenPlanUsageMcp.exe"
CYCLE_A = "2026-09-22T12:00:00Z"
CYCLE_B = "2026-09-22T17:00:00Z"


def sample(usage, reset=CYCLE_A, enabled=True):
    result = {"utilization": usage, "enabled": enabled}
    if reset is not None:
        result["resetsAt"] = reset
    return result


def rpc(surface, requests):
    completed = subprocess.run(
        [str(ADAPTER), "--surface", surface, "--no-launch"],
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
    print(f"PASS: {name}")

product = rpc("product", [requests[0]])[0]
assert product["tools"], "Product surface is unavailable."
assert not any(tool["name"] == "probe_threshold_notifications" for tool in product["tools"])
print("PASS: probe is absent from product surface")
print(f"All {len(cases)} notification cases and surface isolation passed.")
