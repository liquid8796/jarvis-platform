"""Regression checks for the Windows x64 publish verifier; no native binaries are executed."""
from __future__ import annotations

import pathlib
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
VERIFY = ROOT / "scripts" / "Verify-AgentOutput.ps1"
MANAGED = (
    "JarvisCode.App.dll", "JarvisCode.Core.dll", "JarvisCode.Host.dll",
    "JarvisCode.Providers.dll", "Jarvis.Agent.Windows.dll", "Jarvis.Agent.Core.dll",
    "Jarvis.Protocol.dll", "jarvis-agent.exe", "jarvis-agent.dll",
    "jarvis-agent.deps.json", "jarvis-agent.runtimeconfig.json",
)
NATIVE = (
    "conpty.dll", "x64/OpenConsole.exe", "arm64/OpenConsole.exe",
    "licenses/Microsoft.Windows.Console.ConPTY-LICENSE.txt",
)


class AgentPublishAssets(unittest.TestCase):
    def setUp(self) -> None:
        self.shell = shutil.which("pwsh") or shutil.which("powershell")
        if not self.shell:
            self.skipTest("PowerShell is required for the Windows publish verifier")
        self.temp = tempfile.TemporaryDirectory(prefix="jarvis-publish-test-")
        self.addCleanup(self.temp.cleanup)
        self.output = pathlib.Path(self.temp.name)
        for name in MANAGED + NATIVE:
            path = self.output / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic packaging fixture", encoding="utf-8")

    def verify(self) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [self.shell, "-NoProfile", "-File", str(VERIFY), "-OutputDirectory",
             str(self.output), "-Component", "Cli", "-PublishedWinX64"],
            capture_output=True, text=True, errors="replace", timeout=20,
        )

    def test_architecture_specific_hosts_are_valid_without_a_root_host(self) -> None:
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("PASS", result.stdout)

    def test_a_root_host_does_not_replace_the_required_x64_host(self) -> None:
        (self.output / "x64/OpenConsole.exe").replace(self.output / "OpenConsole.exe")
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("x64/OpenConsole.exe", result.stdout + result.stderr)

    def test_x64_emulation_requires_the_native_arm64_host(self) -> None:
        (self.output / "arm64/OpenConsole.exe").unlink()
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("arm64/OpenConsole.exe", result.stdout + result.stderr)

    def test_missing_conpty_library_fails(self) -> None:
        (self.output / "conpty.dll").unlink()
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("conpty.dll", result.stdout + result.stderr)

    def test_standard_build_checks_native_assets_before_archiving(self) -> None:
        script = (ROOT / "scripts/Build.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("Verify-AgentOutput.ps1", script)
        self.assertIn("-PublishedWinX64", script)
        self.assertLess(script.index("Verify-AgentOutput.ps1"), script.index("Compress-Archive"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
