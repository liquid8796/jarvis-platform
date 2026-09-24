"""Load an explicitly selected addon into a fresh, task-owned Blender UI process.

Executed by Start-BlenderMcp.ps1, never used as a raw-socket code execution fallback.
Does not load a .blend file or persist Blender preferences.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import sys

# Set these before importing the addon; do not change the parent Agent's environment.
os.environ.update(BLENDER_MCP_SAFE_MODE="1", BLENDER_MCP_DISABLE_TELEMETRY="true",
                  DISABLE_TELEMETRY="true")

import bpy


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--addon", type=Path, required=True)
    parser.add_argument("--port", type=int, default=9876)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if not args.addon.is_absolute() or not args.addon.is_file() or not 1 <= args.port <= 65535:
        raise ValueError("An existing absolute addon path and a valid port are required.")
    spec = importlib.util.spec_from_file_location("jarvis_blender_mcp_addon", args.addon)
    if spec is None or spec.loader is None:
        raise RuntimeError("Cannot load the selected Blender MCP addon.")
    addon = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = addon
    spec.loader.exec_module(addon)
    addon.register()
    scene = bpy.context.scene
    scene.blendermcp_auto_start_server = False
    scene.blendermcp_port = args.port
    server = addon.BlenderMCPServer(host="127.0.0.1", port=args.port)
    # Use the addon's own canonical reference so its native Stop control works.
    bpy.types.blendermcp_server = server
    server.start()
    scene.blendermcp_server_running = bool(server.running)
    if not server.running:
        raise RuntimeError("Blender MCP addon did not start; check the dedicated process log.")
    print("JARVIS_BLENDER_READY", bpy.app.version_string, args.port, flush=True)


if __name__ == "__main__":
    main()
