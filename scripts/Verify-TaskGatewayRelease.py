"""Verify the built Windows agent / Linux ARM64 server release without deploying it.

Runs only the published CLI `help` command. It does not read enrollment credentials,
connect to production, arm control, or launch a second interactive agent.
"""
from __future__ import annotations

import hashlib
import gzip
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
VERSION = (ROOT / "VERSION").read_text(encoding="utf-8-sig").strip()
if not re.fullmatch(r"\d+\.\d+\.\d+", VERSION):
    raise SystemExit("Invalid VERSION")
OUT = ROOT / "artifacts" / "verification" / VERSION
OUT.mkdir(parents=True, exist_ok=True)
AGENT = ROOT / "artifacts" / "agent" / VERSION
SERVER = ROOT / "artifacts" / "server" / f"{VERSION}-linux-arm64"
CHECKS: list[dict] = []


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def check(name: str, detail: object) -> None:
    CHECKS.append({"name": name, "status": "PASS", "detail": detail})
    print("PASS " + name)


BLOCKED_EXT = {".pfx", ".p12", ".key", ".pem", ".ttf", ".otf", ".woff", ".woff2", ".eot", ".ttc", ".db", ".sqlite", ".sqlite3"}
BLOCKED_NAMES = {"agent.local.json", "tool-permissions.json", "computer-settings.json", ".env", "appsettings.production.json", ".lease"}
FRAMEWORK_PRIVATE = {"system.private.corelib.dll", "system.private.datacontractserialization.dll", "system.private.uri.dll",
    "system.private.windows.core.dll", "system.private.windows.gdiplus.dll", "system.private.xml.dll", "system.private.xml.linq.dll"}


def safe_member(name: str) -> str:
    name = name.replace("\\", "/")
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and ":" not in name, "Unsafe archive path: " + name)
    require(path.suffix.lower() not in BLOCKED_EXT, "Prohibited extension: " + name)
    require(path.name.lower() not in BLOCKED_NAMES, "Prohibited runtime configuration: " + name)
    require(".private." not in path.name.lower() or path.name.lower() in FRAMEWORK_PRIVATE, "Private artifact: " + name)
    require(not any(part.lower() in {"taskruns", ".git", "backups"} for part in path.parts), "Runtime/source data in package: " + name)
    return str(path)


def verify_archive(path: Path, source: Path, kind: str) -> dict:
    require(path.is_file() and path.stat().st_size > 0, "Missing package: " + str(path))
    names: set[str] = set()
    if kind == "zip":
        with zipfile.ZipFile(path) as archive:
            require(archive.testzip() is None, "ZIP CRC verification failed")
            members = archive.infolist()
            for member in members:
                name = safe_member(member.filename)
                if member.is_dir():
                    continue
                require(name not in names, "Duplicate archive member: " + name)
                names.add(name)
                with archive.open(member) as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                local = source / name
                require(local.is_file() and digest == sha256(local), "ZIP differs from publish output: " + name)
    else:
        # Read to gzip EOF as well, so trailer CRC/length corruption is detected.
        with gzip.open(path, "rb") as stream:
            while stream.read(1024 * 1024):
                pass
        with tarfile.open(path, "r:gz") as archive:
            for member in archive:
                name = safe_member(member.name)
                if member.isdir():
                    continue
                require(member.isfile(), "Archive links/devices are not supported: " + name)
                require(name not in names, "Duplicate archive member: " + name)
                names.add(name)
                with archive.extractfile(member) as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                local = source / name
                require(local.is_file() and digest == sha256(local), "TAR differs from publish output: " + name)
    expected = {p.relative_to(source).as_posix() for p in source.rglob("*") if p.is_file()}
    require(names == expected, "Archive members do not exactly match publish files")
    for name in names:
        safe_member(name)
    digest = sha256(path)
    path.with_suffix(path.suffix + ".sha256").write_text(digest + "  " + path.name + "\n", encoding="ascii")
    return {"file": str(path), "bytes": path.stat().st_size, "files": len(names), "sha256": digest}


def main() -> None:
    require(os.name == "nt", "Run this verification on Windows to inspect PE versions and smoke-test the agent CLI")
    props = ET.parse(ROOT / "Directory.Build.props")
    for field, value in (("Version", VERSION), ("AssemblyVersion", VERSION + ".0"), ("FileVersion", VERSION + ".0")):
        require(props.findtext(".//" + field) == value, "Version mismatch: " + field)
    check("source-version", VERSION)

    test_results: dict[str, dict] = {}
    for path in sorted((ROOT / "artifacts" / "test-results" / VERSION).glob("*.trx"), key=lambda item: item.stat().st_mtime):
        document = ET.parse(path)
        counter = document.find(".//{*}Counters")
        result = document.find(".//{*}UnitTestResult")
        if counter is None or result is None:
            continue
        suite = next((s for s in ("Jarvis.Core.Tests", "Jarvis.Agent.Windows.Tests", "Jarvis.Server.Tests")
            if result.attrib.get("testName", "").startswith(s + ".")), None)
        if suite:
            test_results[suite] = {"file": str(path), **{k: int(counter.attrib.get(k, "0")) for k in ("total", "executed", "passed", "failed", "error", "aborted", "notExecuted")}}
    require(len(test_results) == 3, "Three complete test-suite results are required")
    for suite, result in test_results.items():
        require(result["total"] > 0 and result["total"] == result["passed"] == result["executed"], "Incomplete or failing suite: " + suite)
        require(all(result[k] == 0 for k in ("failed", "error", "aborted", "notExecuted")), "Unsuccessful test result: " + suite)
    check("test-suites", test_results)

    for component, label in (("desktop", "Desktop"), ("cli", "Cli")):
        completed = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(ROOT / "scripts/Verify-AgentOutput.ps1"),
            "-OutputDirectory", str(AGENT / component), "-Component", label, "-RequireSymbols"], capture_output=True, text=True, timeout=30)
        require(completed.returncode == 0, completed.stdout + completed.stderr)
    check("agent-entry-points-and-baseline-host-exclusion", "Existing Verify-AgentOutput.ps1 passed for Desktop and CLI")

    paths = [AGENT / "desktop/Jarvis.Agent.Desktop.dll", AGENT / "desktop/Jarvis.Agent.Core.dll", AGENT / "desktop/Jarvis.Protocol.dll",
        AGENT / "cli/jarvis-agent.dll", AGENT / "cli/Jarvis.Agent.Core.dll", AGENT / "cli/Jarvis.Protocol.dll", SERVER / "jarvis-mcp-server.dll", SERVER / "Jarvis.Protocol.dll"]
    quoted = ",".join("'" + str(path).replace("'", "''") + "'" for path in paths)
    command = "$ErrorActionPreference='Stop'; @(" + quoted + ") | ForEach-Object { [pscustomobject]@{ Path=$_; Assembly=[Reflection.AssemblyName]::GetAssemblyName($_).Version.ToString(); File=[Diagnostics.FileVersionInfo]::GetVersionInfo($_).FileVersion } } | ConvertTo-Json"
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True, text=True, timeout=30)
    require(result.returncode == 0, result.stdout + result.stderr)
    versions = json.loads(result.stdout)
    require(all(item["Assembly"] == VERSION + ".0" and item["File"] == VERSION + ".0" for item in versions), "Published assembly/file version mismatch")
    check("published-binary-versions", versions)

    smoke = subprocess.run([str(AGENT / "cli/jarvis-agent.exe"), "help"], cwd=AGENT / "cli", capture_output=True, text=True, timeout=15)
    require(smoke.returncode == 0 and f"Jarvis Agent {VERSION}" in smoke.stdout, "Published CLI smoke test failed: " + smoke.stdout + smoke.stderr)
    check("published-cli-help", smoke.stdout.splitlines()[0])

    executable = SERVER / "jarvis-mcp-server"
    with executable.open("rb") as stream:
        header = stream.read(64)
    require(header[:4] == b"\x7fELF" and int.from_bytes(header[18:20], "little") == 183, "Server executable is not Linux ARM64 ELF")
    require((SERVER / "jarvis-mcp-server.deps.json").is_file() and (SERVER / "jarvis-mcp-server.runtimeconfig.json").is_file(), "Server runtime manifests missing")
    require((SERVER / "wwwroot/index.html").is_file(), "Server web assets missing")
    check("linux-arm64-server-layout", "ARM64 ELF and server runtime/web assets; not executed on this Windows machine")
    check("agent-zip-integrity-security-and-publish-parity", verify_archive(ROOT / f"artifacts/Jarvis-Agent-{VERSION}-win-x64.zip", AGENT, "zip"))
    check("server-tar-integrity-security-and-publish-parity", verify_archive(ROOT / f"artifacts/jarvis-mcp-server-{VERSION}-linux-arm64.tar.gz", SERVER, "tar"))
    report = {"version": VERSION, "passed": True, "checks": CHECKS,
        "scope": "Real TestServer/AgentConnection/ProcessToolSet integration, published CLI smoke and archive verification. No production deployment, no Linux native execution, no autonomous model planner."}
    (OUT / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("ALL_RELEASE_CHECKS_PASSED")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        (OUT / "verification.json").write_text(json.dumps({"version": VERSION, "passed": False, "checks": CHECKS,
            "error": str(error)}, indent=2), encoding="utf-8")
        raise
