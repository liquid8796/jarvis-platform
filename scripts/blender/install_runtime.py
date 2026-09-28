"""Install a reviewed Blender MCP runtime side by side; never switch a live profile.

Uses a hash-pinned upstream archive, a new venv and auditable privacy patches.
Does not write Blender preferences, install into another project's environment,
start/stop Blender or Agent, or consume any provider quota.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
import venv
import zipfile

from runtime_policy import apply_policy

LOCK_PATH = Path(__file__).with_name("upstream.lock.json")
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXTRACTED = 256 * 1024 * 1024


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_archive(archive: Path, pin: dict) -> None:
    if archive.stat().st_size > MAX_ARCHIVE or sha256(archive) != pin["archive_sha256"]:
        raise ValueError("Upstream archive does not match the reviewed SHA256; nothing installed.")
    with zipfile.ZipFile(archive) as z:
        files = z.infolist()
        if len(files) > 2000 or sum(f.file_size for f in files) > MAX_EXTRACTED:
            raise ValueError("Archive exceeds extraction limits.")
        root = "mcp-for-blender-" + pin["commit"]
        for item in files:
            name = PurePosixPath(item.filename)
            if (name.is_absolute() or not name.parts or name.parts[0] != root or
                    ".." in name.parts or "\\" in item.orig_filename or ":" in item.filename or
                    stat.S_ISLNK(item.external_attr >> 16)):
                raise ValueError("Archive contains an unsafe member.")


def inventory(package: Path) -> list[str]:
    tree = ast.parse((package / "server.py").read_text(encoding="utf-8"))
    return sorted(n.name for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))
                  and any(isinstance(d, ast.Call) and isinstance(d.func, ast.Attribute)
                          and isinstance(d.func.value, ast.Name) and d.func.value.id == "mcp"
                          and d.func.attr == "tool" for d in n.decorator_list))


def run(command: list[str], log: Path, timeout: int = 300) -> None:
    with log.open("ab") as stream:
        subprocess.run(command, check=True, timeout=timeout, stdin=subprocess.DEVNULL,
                       stdout=stream, stderr=subprocess.STDOUT)


def install(target: Path, archive: Path, pin: dict) -> dict:
    validate_archive(archive, pin)
    if not target.is_absolute() or target.is_symlink():
        raise ValueError("Use an absolute, non-symlink directory for the dedicated runtime.")
    receipt_path = target / "jarvis-runtime.json"
    if target.exists():
        if not receipt_path.is_file():
            raise ValueError("Target exists without a completed receipt; choose a new directory. No files replaced.")
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        if receipt.get("commit") != pin["commit"] or receipt.get("policy_revision") != pin["policy_revision"]:
            raise ValueError("Existing runtime is a different revision; choose a new directory.")
        for name, digest in receipt["installed_hashes"].items():
            if sha256(target / name) != digest:
                raise ValueError("Existing runtime changed; refusing to overwrite local edits.")
        return {**receipt, "changed": False}

    with tempfile.TemporaryDirectory(prefix="jarvis-blender-source-") as temp:
        source_dir = Path(temp)
        with zipfile.ZipFile(archive) as z:
            z.extractall(source_dir)  # All names/sizes validated above.
        source = source_dir / ("mcp-for-blender-" + pin["commit"])
        if sha256(source / "addon.py") != pin["addon_sha256"]:
            raise ValueError("Bundled addon hash does not match the reviewed source.")
        if len(inventory(source / "src/blender_mcp")) != pin["tool_count"]:
            raise ValueError("Unexpected upstream tool inventory.")
        target.mkdir(parents=True)
        log = target / "install.log"
        try:
            venv.EnvBuilder(with_pip=True).create(target)
            python = target / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
            run([str(python), "-m", "pip", "install", "--disable-pip-version-check", str(source)], log)
            run([str(python), "-m", "pip", "check"], log)
            package = target / "Lib/site-packages/blender_mcp" if os.name == "nt" else next(target.glob("lib/python*/site-packages/blender_mcp"))
            policy = apply_policy(package)
            run([str(python), "-m", "compileall", "-q", str(package)], log)
            freeze = subprocess.check_output([str(python), "-m", "pip", "freeze"], timeout=60, text=True)
            (target / "requirements-resolved.txt").write_text(freeze, encoding="utf-8")
            shutil.copy2(source / "LICENSE", target / "UPSTREAM-LICENSE.txt")
            paths = list(package.rglob("*.py")) + [python]
            receipt = {**pin, "changed": True, "python": str(python), "addon": str(package / "bundled/addon.py"),
                       "policy": policy, "tools": inventory(package),
                       "installed_hashes": {str(p.relative_to(target)): sha256(p) for p in paths},
                       "activation": "not_activated; pair server and addon after testing; shared profile untouched"}
            receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
            return receipt
        except Exception:
            (target / "INSTALL-INCOMPLETE.txt").write_text("Installation did not complete. No live profile was changed. Inspect install.log.\n", encoding="utf-8")
            raise


def main() -> None:
    if sys.version_info < (3, 11):
        raise ValueError("Jarvis' runtime installer requires Python 3.11 or newer.")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--archive", type=Path, help="Optional already-downloaded pinned archive.")
    args = parser.parse_args()
    pin = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="jarvis-blender-download-") as temp:
        archive = args.archive
        if archive is None:
            archive = Path(temp) / "upstream.zip"
            with urllib.request.urlopen(pin["archive_url"], timeout=60) as response, archive.open("wb") as out:
                total = 0
                while chunk := response.read(1024 * 1024):
                    total += len(chunk)
                    if total > MAX_ARCHIVE:
                        raise ValueError("Upstream download exceeds size limit.")
                    out.write(chunk)
        receipt = install(args.target, archive, pin)
    print(json.dumps({key: receipt[key] for key in ("changed", "version", "commit", "python", "addon", "activation")}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError, zipfile.BadZipFile) as error:
        print(f"Blender runtime installation failed ({type(error).__name__}): {error}", file=sys.stderr)
        sys.exit(1)
