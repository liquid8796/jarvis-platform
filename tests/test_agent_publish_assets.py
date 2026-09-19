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
BROWSER = (
    "jarvis-browser-service.exe", "jarvis-browser-service.dll",
    "jarvis-browser-service.deps.json", "jarvis-browser-service.runtimeconfig.json",
    "browser/jarvis-browser-host.exe",
    "Assets/Browser/manifest.json", "Assets/Browser/background.js", "Assets/Browser/qa.js",
)


class AgentPublishAssets(unittest.TestCase):
    def setUp(self) -> None:
        self.shell = shutil.which("pwsh") or shutil.which("powershell")
        if not self.shell:
            self.skipTest("PowerShell is required for the Windows publish verifier")
        self.temp = tempfile.TemporaryDirectory(prefix="jarvis-publish-test-")
        self.addCleanup(self.temp.cleanup)
        self.output = pathlib.Path(self.temp.name)
        for name in MANAGED + NATIVE + BROWSER:
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

    def test_missing_structured_qa_runtime_fails(self) -> None:
        (self.output / "Assets/Browser/qa.js").unlink()
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("qa.js", result.stdout + result.stderr)

    def test_root_browser_host_is_rejected(self) -> None:
        (self.output / "jarvis-browser-host.exe").write_text(
            "misplaced native host", encoding="utf-8"
        )
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("jarvis-browser-host.exe", result.stdout + result.stderr)

    def test_entrypoint_projects_build_browser_companions(self) -> None:
        for relative in (
            "jarvis-agent/src/Jarvis.Agent.Desktop/Jarvis.Agent.Desktop.csproj",
            "jarvis-agent/src/Jarvis.Agent.Cli/Jarvis.Agent.Cli.csproj",
        ):
            project = (ROOT / relative).read_text(encoding="utf-8-sig")
            self.assertIn("Jarvis.Agent.BrowserService.csproj", project)
            self.assertIn("Jarvis.Agent.BrowserHost.csproj", project)
            self.assertGreaterEqual(project.count('ReferenceOutputAssembly="false"'), 2)

        targets = (ROOT / "Directory.Build.targets").read_text(encoding="utf-8-sig")
        self.assertIn('Name="CopyBrowserCompanionsToAgentBuild"', targets)
        self.assertIn('AfterTargets="Build"', targets)
        self.assertIn('DestinationFolder="$(OutDir)"', targets)
        self.assertIn('DestinationFolder="$(OutDir)browser\\"', targets)

    def test_standard_build_checks_native_assets_before_archiving(self) -> None:
        script = (ROOT / "scripts/Build.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("Verify-AgentOutput.ps1", script)
        self.assertIn("-PublishedWinX64", script)
        self.assertLess(script.index("Verify-AgentOutput.ps1"), script.index("Compress-Archive"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
