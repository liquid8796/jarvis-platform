"""Offline tests for the pinned installer, and policy tests in an installed runtime."""
import asyncio
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts/blender"))
import install_runtime
import runtime_policy


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.commit = "a" * 40
        self.pin = {"commit": self.commit, "policy_revision": 1}

    def archive(self, member=None, symlink=False):
        path = self.root / "source.zip"
        with zipfile.ZipFile(path, "w") as z:
            name = member or f"mcp-for-blender-{self.commit}/README.md"
            info = zipfile.ZipInfo(name)
            info.filename = name  # Preserve a raw backslash member even on Windows.
            if symlink:
                info.create_system = 3
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
            z.writestr(info, "fixture")
        self.pin["archive_sha256"] = install_runtime.sha256(path)
        return path

    def test_lock_is_exact_not_latest(self):
        pin = json.loads(install_runtime.LOCK_PATH.read_text())
        self.assertEqual("2.1.1", pin["version"])
        self.assertEqual(36, pin["tool_count"])
        self.assertEqual(40, len(pin["commit"]))
        self.assertEqual(64, len(pin["archive_sha256"]))
        self.assertTrue(pin["archive_url"].endswith(pin["commit"]))

    def test_valid_archive(self):
        install_runtime.validate_archive(self.archive(), self.pin)

    def test_tampered_archive_rejected_before_target_created(self):
        archive = self.archive()
        with archive.open("ab") as stream:
            stream.write(b"tampered")
        target = self.root / "venv"
        with self.assertRaises(ValueError):
            install_runtime.install(target, archive, self.pin)
        self.assertFalse(target.exists())

    def test_paths_and_links_rejected(self):
        for name in ("../escape", "/absolute", "C:/escape", f"mcp-for-blender-{self.commit}/../escape",
                     f"mcp-for-blender-{self.commit}/sub\\escape"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                install_runtime.validate_archive(self.archive(name), self.pin)
        with self.assertRaises(ValueError):
            install_runtime.validate_archive(self.archive(symlink=True), self.pin)

    def test_existing_unmanaged_environment_is_never_overwritten(self):
        target = self.root / "venv"
        target.mkdir()
        marker = target / "user-file.txt"
        marker.write_text("original")
        with self.assertRaises(ValueError):
            install_runtime.install(target, self.archive(), self.pin)
        self.assertEqual("original", marker.read_text())

    def test_completed_install_is_idempotent_but_edits_fail_closed(self):
        target = self.root / "venv"
        target.mkdir()
        content = target / "server.py"
        content.write_text("reviewed")
        receipt = {**self.pin, "installed_hashes": {"server.py": install_runtime.sha256(content)}}
        (target / "jarvis-runtime.json").write_text(json.dumps(receipt))
        archive = self.archive()
        self.assertFalse(install_runtime.install(target, archive, self.pin)["changed"])
        content.write_text("local edit")
        with self.assertRaises(ValueError):
            install_runtime.install(target, archive, self.pin)
        self.assertEqual("local edit", content.read_text())

    def test_inventory_uses_decorated_top_level_tools_only(self):
        (self.root / "server.py").write_text("@mcp.tool()\nasync def public(): pass\ndef helper(): pass\n")
        self.assertEqual(["public"], install_runtime.inventory(self.root))

    def test_patch_rejects_unknown_source_without_writes(self):
        for name in runtime_policy.SOURCE_HASHES:
            (self.root / name).write_bytes(b"unreviewed source")
        with self.assertRaises(ValueError):
            runtime_policy.apply_policy(self.root)
        for name in runtime_policy.SOURCE_HASHES:
            self.assertEqual(b"unreviewed source", (self.root / name).read_bytes())

    def test_patch_anchor_must_be_unique(self):
        for text in ("missing", "anchor anchor"):
            with self.assertRaises(ValueError):
                runtime_policy.replace_once(text, "anchor", "new")


@unittest.skipUnless(importlib.util.find_spec("blender_mcp"), "Run with the staged Blender MCP Python for runtime policy tests")
class InstalledPolicyTests(unittest.TestCase):
    def test_disabled_collector_never_creates_identity_starts_sender_or_contacts_addon(self):
        from blender_mcp.telemetry import TelemetryCollector, EventType
        with patch.dict(os.environ, {"BLENDER_MCP_DISABLE_TELEMETRY": "true"}), \
             patch.object(TelemetryCollector, "_get_or_create_uuid", side_effect=AssertionError("identity created")), \
             patch("threading.Thread.start", side_effect=AssertionError("sender started")):
            collector = TelemetryCollector()
            self.assertFalse(collector.config.enabled)
            self.assertFalse(collector.check_user_consent())
            collector.record_event(EventType.TOOL_EXECUTION, tool_name="test", prompt_text="private")
            self.assertTrue(collector._queue.empty())
            self.assertEqual("", collector.upload_screenshot(b"private", "test"))

    def test_environment_opt_out_skips_consent_storage_and_dialog(self):
        from blender_mcp import consent_prompt
        with patch.dict(os.environ, {"BLENDER_MCP_DISABLE_TELEMETRY": "true"}), \
             patch.object(consent_prompt, "_session_key", side_effect=AssertionError("consent flow entered")):
            self.assertEqual("", asyncio.run(consent_prompt.maybe_prompt_for_consent(object())))

    def test_all_upstream_tools_and_safe_mode_improvements_present(self):
        import blender_mcp
        from blender_mcp.safe_mode import SandboxViolation, validate_code
        tools = install_runtime.inventory(Path(blender_mcp.__file__).parent)
        self.assertEqual(36, len(tools))
        for name in ("get_polyhaven_asset_preview", "get_tripo_status", "generate_tripo_model",
                     "poll_tripo_job_status", "import_generated_asset_tripo"):
            self.assertIn(name, tools)
        for code in ("import subprocess", "import bpy\nbpy.ops.extensions.package_install()",
                     "import bpy\nbpy.context.preferences.filepaths.script_directories.new()"):
            with self.subTest(code=code), self.assertRaises(SandboxViolation):
                validate_code(code)


if __name__ == "__main__":
    unittest.main()
