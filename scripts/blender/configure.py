"""Merge a trusted local Blender MCP Python runtime into the operator's MCP profile.

The PowerShell entry point holds an exclusive sidecar lock while this helper runs.
No packages are installed and no Blender or Agent process is started.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import uuid

MAX_BYTES = 256 * 1024


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON properties are not supported; original file preserved.")
        result[key] = value
    return result


def reject_constant(value):
    raise ValueError("Non-finite JSON numbers are not supported.")


def check_depth(value, depth=0):
    if isinstance(value, (dict, list)):
        if depth >= 32:
            raise ValueError("MCP configuration exceeds the supported nesting depth.")
        for child in (value.values() if isinstance(value, dict) else value):
            check_depth(child, depth + 1)


def merge_config(path: Path, python_exe: Path, port: int) -> dict:
    if not 1 <= port <= 65535:
        raise ValueError("Port must be between 1 and 65535.")
    if not python_exe.is_absolute() or python_exe.name.lower() != "python.exe":
        raise ValueError("Specify an absolute path to the trusted environment's python.exe.")
    if not python_exe.is_file():
        raise ValueError("Python executable does not exist.")
    # lstat-style checks prevent accidentally replacing a config link rather than its target.
    if path.is_symlink():
        raise ValueError("Symlink configuration files are not supported.")
    exists = path.exists()
    original = None
    if exists:
        with path.open("rb") as stream:
            original = stream.read(MAX_BYTES + 1)
        if len(original) > MAX_BYTES:
            raise ValueError("MCP configuration exceeds 256 KiB; original file preserved.")
    root = json.loads(original.decode("utf-8-sig"), object_pairs_hook=unique_object,
                      parse_constant=reject_constant) if original is not None else {}
    check_depth(root)
    if not isinstance(root, dict):
        raise ValueError("MCP configuration root must be an object.")
    servers = root.setdefault("mcpServers", {})
    if not isinstance(servers, dict):
        raise ValueError("mcpServers must be an object; original file preserved.")
    entry = {
        "type": "stdio", "command": str(python_exe),
        "args": ["-m", "blender_mcp.server"], "disabled": False,
        "env": {
            "BLENDER_HOST": "127.0.0.1", "BLENDER_PORT": str(port),
            "BLENDER_MCP_SAFE_MODE": "1", "BLENDER_MCP_DISABLE_TELEMETRY": "true",
            "DISABLE_TELEMETRY": "true", "PYTHONIOENCODING": "utf-8",
        },
    }
    if servers.get("blender") == entry:
        return {"changed": False, "config": str(path)}
    servers["blender"] = entry
    output = (json.dumps(root, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    if len(output) > MAX_BYTES:
        raise ValueError("Merged MCP configuration would exceed 256 KiB.")
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    backup = None
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(output)
            stream.flush()
            os.fsync(stream.fileno())
        # Cooperative writers hold the sidecar lock. Detect most non-cooperative edits too.
        if path.exists() != exists or (exists and path.read_bytes() != original):
            raise ValueError("MCP configuration changed during setup; no replacement was performed.")
        if original is not None:
            backup = path.with_name(path.name + ".blender-" + uuid.uuid4().hex + ".bak")
            with backup.open("xb") as stream:
                stream.write(original)
                stream.flush()
                os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return {"changed": True, "config": str(path), "backup": str(backup) if backup else None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--python", required=True, type=Path)
    parser.add_argument("--port", type=int, default=9876)
    args = parser.parse_args()
    if not args.config.is_absolute():
        parser.error("Configuration path must be absolute.")
    if importlib.util.find_spec("blender_mcp.server") is None:
        parser.error("Selected Python environment does not have blender_mcp.server installed.")
    result = merge_config(args.config, args.python, args.port)
    print(json.dumps(result, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, ModuleNotFoundError) as error:
        # Never print the configuration or JSONDecodeError.doc (which may contain credentials).
        print("Blender MCP setup failed: " + str(error), file=sys.stderr)
        sys.exit(1)
