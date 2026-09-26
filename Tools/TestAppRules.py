"""Integration checks against the current worktree's running Debug MCP.

Uses isolated files for watcher/shortcut/adapter checks, restores theme settings,
and never downloads an update or changes real startup entries.
"""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "bin" / "JeekTokenPlanUsage.exe"
ADAPTER = Path(os.environ["LOCALAPPDATA"]) / "JeekTokenPlanUsage" / "Mcp" / "JeekTokenPlanUsageMcp.exe"


def call(name, arguments, surface="debug"):
    request = {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
               "params": {"name": name, "arguments": arguments}}
    result = subprocess.run(
        [str(ADAPTER), "--surface", surface, "--app", str(APP), "--no-launch"],
        input=json.dumps(request) + "\n", capture_output=True, text=True,
        encoding="utf-8", timeout=30, check=True,
    )
    response = json.loads(result.stdout)
    assert "error" not in response, response
    response = response["result"]
    assert not response.get("isError"), response
    if "structuredContent" in response:
        return response["structuredContent"]
    value = response["content"][0]["text"]
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def get(path):
    return call("get_value", {"path": path})


def set_value(path, value):
    return call("set_value", {"path": path, "value": value})


def invoke(path, *args):
    return call("invoke", {"path": path, "args": list(args)})


def state():
    return call("get_ui_state", {}, "product")


def test_theme():
    original = get("Context._settings.Theme")
    try:
        for mode, dark in [("dark", True), ("light", False), ("system", None)]:
            result = call("ui_action", {"action": "set_theme", "mode": mode}, "product")
            assert result["state"]["settings"]["theme"] == mode
            if dark is not None:
                assert get("Context.IsDarkTheme") == dark
    finally:
        invoke("Context.SetTheme", original)
    print("PASS: live theme choices and product state")


def test_watcher():
    original_path = get("Context._settings.RoamingSettingsPath")
    original_directory = get("Context._settings.RoamingConfigDirectory")
    with tempfile.TemporaryDirectory(prefix="JeekSettingsTest-") as directory:
        folder = Path(directory)
        target = folder / "settings.json"
        target.write_bytes(Path(original_path).read_bytes())
        try:
            set_value("Context._settings._roamingConfigDirectory", directory)
            set_value("Context._settings._roamingSettingsPath", str(target))
            invoke("Context.StartSettingsWatcher")
            before = get("Context._settingsReloadCount")
            (folder / "unrelated.txt").write_text("noise")
            time.sleep(11)
            assert get("Context._settingsReloadCount") == before
            assert not get("Context._settingsReloadTimer.Enabled")
            invoke("Context._settings.Save")
            data = json.loads(target.read_text(encoding="utf-8-sig"))
            data["Theme"] = "dark" if data.get("Theme") != "dark" else "light"
            incoming = folder / "incoming.json"
            incoming.write_text(json.dumps(data), encoding="utf-8")
            incoming.replace(target)
            time.sleep(5)
            assert get("Context._settingsReloadCount") == before
            data["Language"] = "en"
            target.write_text(json.dumps(data), encoding="utf-8")
            time.sleep(6)
            assert get("Context._settingsReloadCount") == before
            time.sleep(5)
            assert get("Context._settingsReloadCount") == before + 1
            assert get("Context._settings.Theme") == data["Theme"]
            assert get("Context._settings.RoamingSettingsPath") == str(target)
        finally:
            set_value("Context._settings._roamingConfigDirectory", original_directory)
            set_value("Context._settings._roamingSettingsPath", original_path)
            invoke("Context.StartSettingsWatcher")
            invoke("Context.ReloadSettingsFromDisk")
    print("PASS: relevant-file filtering, external atomic save, resetting 10-second debounce")


def test_confirmations():
    original = state()["settings"]["roamingSettingsPath"]
    with tempfile.TemporaryDirectory(prefix="JeekConfirmTest-") as directory:
        try:
            result = call("ui_action", {"action": "set_storage", "mode": "custom",
                                       "customRoot": directory}, "product")
            assert result["status"] == "awaiting_user"
            assert get("Context._settings.RoamingSettingsPath") == original
            assert not (Path(directory) / "Config").exists()
            assert state()["operation"]["id"] == result["operationId"]
            invoke("Context._confirmationForm.#Cancel.PerformClick")
            assert state()["operation"]["status"] == "cancelled"
            invoke("Context.PreviewUpdateConfirmation")
            assert state()["operation"]["status"] == "awaiting_user"
            invoke("Context._confirmationForm.#Decline.PerformClick")
            assert state()["operation"]["status"] == "postponed"
            invoke("Context.PreviewUpdateConfirmation")
            invoke("Context._confirmationForm.#Confirm.PerformClick")
            assert state()["operation"]["status"] == "failed"  # Debug installation is forbidden.
        finally:
            if get("Context._confirmationForm") is not None:
                invoke("Context._confirmationForm.Close")
    print("PASS: GUI-only migration approval, update postponement and Debug install guard")


if __name__ == "__main__":
    operation = state().get("operation")
    assert not operation or operation["status"] not in (
        "checking", "downloading", "awaiting_user", "installing"
    ), "Finish the current GUI operation before running tests."
    for probe in ("probe_adapter_installation", "probe_startup_shortcut", "probe_dependencies"):
        call(probe, {})
        print("PASS:", probe)
    test_theme()
    test_watcher()
    test_confirmations()
