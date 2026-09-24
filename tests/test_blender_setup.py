"""Run with: python -m unittest discover -s tests -p test_blender_setup.py -v"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

MODULE = Path(__file__).resolve().parents[1] / "scripts/blender/configure.py"
spec = importlib.util.spec_from_file_location("blender_configure", MODULE)
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class BlenderSetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="jarvis-blender-setup-")
        self.root = Path(self.temp.name)
        self.python = self.root / "trusted env/python.exe"
        self.python.parent.mkdir()
        self.python.write_bytes(b"test fixture, never executed")
        self.config = self.root / "profile/mcp.json"

    def tearDown(self):
        self.temp.cleanup()

    def save(self, content):
        self.config.parent.mkdir(exist_ok=True)
        self.config.write_bytes(content)

    def merge(self, **kwargs):
        return setup.merge_config(self.config, self.python, **{"port": 9876, **kwargs})

    def test_new_config_is_explicit_safe_and_does_not_require_blender_running(self):
        result = self.merge()
        self.assertTrue(result["changed"])
        self.assertIsNone(result["backup"])
        entry = json.loads(self.config.read_text())["mcpServers"]["blender"]
        self.assertEqual(["-m", "blender_mcp.server"], entry["args"])
        self.assertEqual(str(self.python), entry["command"])
        self.assertEqual("127.0.0.1", entry["env"]["BLENDER_HOST"])
        self.assertEqual("1", entry["env"]["BLENDER_MCP_SAFE_MODE"])
        self.assertEqual("true", entry["env"]["BLENDER_MCP_DISABLE_TELEMETRY"])

    def test_merge_preserves_unity_other_servers_metadata_and_unicode_with_exact_backup(self):
        original = {"metadata": {"name": "Cảnh thử"}, "mcpServers": {
            "unityMCP": {"url": "http://localhost:8080/mcp", "disabled": False},
            "other": {"command": "tool.exe", "args": ["--literal", ">=1"]}}}
        data = b"\xef\xbb\xbf" + json.dumps(original, ensure_ascii=False).encode("utf-8")
        self.save(data)
        result = self.merge(port=19876)
        merged = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(original["metadata"], merged["metadata"])
        for name in ("unityMCP", "other"):
            self.assertEqual(original["mcpServers"][name], merged["mcpServers"][name])
        self.assertEqual(data, Path(result["backup"]).read_bytes())
        self.assertEqual("19876", merged["mcpServers"]["blender"]["env"]["BLENDER_PORT"])

    def test_idempotent_setup_does_not_rewrite_or_create_backup(self):
        self.merge()
        before = self.config.read_bytes()
        modified = self.config.stat().st_mtime_ns
        self.assertFalse(self.merge()["changed"])
        self.assertEqual(before, self.config.read_bytes())
        self.assertEqual(modified, self.config.stat().st_mtime_ns)
        self.assertEqual([], list(self.config.parent.glob("*.bak")))

    def test_corrupt_duplicate_non_object_or_nonfinite_json_is_preserved(self):
        for data in (b"{broken", b"[]", b"null", b'{"mcpServers":[]}',
                     b'{"mcpServers":{},"mcpServers":{}}', b'{"x":NaN}',
                     b'{"mcpServers":{"x":{},"x":{}}}'):
            with self.subTest(data=data):
                self.save(data)
                with self.assertRaises(ValueError):
                    self.merge()
                self.assertEqual(data, self.config.read_bytes())
        self.assertEqual([], list(self.config.parent.glob("*.tmp")))
        self.assertEqual([], list(self.config.parent.glob("*.bak")))

    def test_oversized_and_deep_json_are_preserved(self):
        for data in (b" " * (setup.MAX_BYTES + 1), ("[" * 34 + "0" + "]" * 34).encode()):
            self.save(data)
            with self.assertRaises(ValueError):
                self.merge()
            self.assertEqual(data, self.config.read_bytes())

    def test_invalid_ports_never_create_configuration(self):
        for port in (0, -1, 65536):
            with self.subTest(port=port), self.assertRaises(ValueError):
                self.merge(port=port)
        self.assertFalse(self.config.exists())

    def test_missing_or_wrong_executable_does_not_create_configuration(self):
        for exe in (self.root / "absent/python.exe", self.root / "cmd.exe", Path("python.exe")):
            with self.subTest(exe=str(exe)), self.assertRaises(ValueError):
                setup.merge_config(self.config, exe, 9876)
        self.assertFalse(self.config.exists())

    def test_failed_atomic_replace_preserves_original_and_removes_temporary_file(self):
        original = b'{"mcpServers":{"unityMCP":{"command":"keep-me"}}}'
        self.save(original)
        with patch.object(setup.os, "replace", side_effect=OSError("simulated replace failure")):
            with self.assertRaises(OSError):
                self.merge()
        self.assertEqual(original, self.config.read_bytes())
        self.assertEqual([], list(self.config.parent.glob("*.tmp")))
        backup = list(self.config.parent.glob("*.bak"))
        self.assertEqual(1, len(backup))
        self.assertEqual(original, backup[0].read_bytes())


if __name__ == "__main__":
    unittest.main()
