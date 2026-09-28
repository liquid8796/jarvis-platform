"""Reproducible, hash-gated privacy patches for Jarvis' dedicated 2.1.1 runtime.

No geometry/provider implementation or Safe Mode validation is replaced. The
upstream sdist lacks telemetry.config; disabling collection must still work and
must not create a persistent telemetry identity or start a sender thread.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

SOURCE_HASHES = {
    "telemetry.py": "fd65b30cbf053cb870f96e6af054b517e28fa6c594230a768cb2ff67f6a011ea",
    "consent_prompt.py": "def8b22b6b58f8b74ad6994fd92ce58a6e2cef7fc026bb6b53258db280bccf33",
    "server.py": "3640c577f24e571df127a498678738d1a5c90a123525651cbe68c3d0bb3fcbfd",
}


def replace_once(text: str, old: str, new: str) -> str:
    if text.count(old) != 1:
        raise ValueError("Reviewed source anchor changed; refusing to guess a patch.")
    return text.replace(old, new, 1)


def transform(name: str, source: bytes) -> bytes:
    if hashlib.sha256(source).hexdigest() != SOURCE_HASHES.get(name):
        raise ValueError(f"Unreviewed {name}; refusing to patch this runtime.")
    text = source.decode("utf-8")
    if name == "telemetry.py":
        text = replace_once(text,
            "        from .config import telemetry_config\n        self.config = telemetry_config",
            "        if self._is_disabled():\n            from types import SimpleNamespace\n"
            "            self.config = SimpleNamespace(enabled=False)\n        else:\n"
            "            from .config import telemetry_config\n            self.config = telemetry_config")
        text = replace_once(text, "self._customer_uuid: str = self._get_or_create_uuid()",
                            'self._customer_uuid: str = self._get_or_create_uuid() if self.config.enabled else ""')
        text = replace_once(text, "        self._worker.start()", "        if self.config.enabled:\n            self._worker.start()")
        text = replace_once(text, "        with self._consent_lock:\n            if (",
                            "        if self._is_disabled():\n            return False\n        with self._consent_lock:\n            if (")
    elif name == "consent_prompt.py":
        text = replace_once(text, "    session_key = _session_key(ctx)",
            '    if any(os.getenv(key, "").lower() in ("1", "true", "yes", "on")\n'
            '           for key in ("DISABLE_TELEMETRY", "BLENDER_MCP_DISABLE_TELEMETRY", "MCP_DISABLE_TELEMETRY")):\n'
            '        return ""\n    session_key = _session_key(ctx)')
    elif name == "server.py":
        text = replace_once(text,
            'logger.info(f"Sending command: {command_type} with params: {params}")',
            'logger.info(f"Sending command: {command_type} (parameters omitted by Jarvis privacy policy)")')
        text = replace_once(text,
            'logger.error(f"Raw response (first 200 bytes): {response_data[:200]}")',
            'logger.error("Malformed response body omitted by Jarvis privacy policy")')
    compile(text, name, "exec")
    return text.encode("utf-8")


def apply_policy(package: Path) -> dict:
    # Validate every file before writing any; install_runtime only uses a new, owned venv.
    pending = {name: transform(name, (package / name).read_bytes()) for name in SOURCE_HASHES}
    for name, data in pending.items():
        (package / name).write_bytes(data)
    return {name: {"upstream_sha256": SOURCE_HASHES[name], "jarvis_sha256": hashlib.sha256(data).hexdigest()}
            for name, data in pending.items()}
