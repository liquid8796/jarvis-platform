#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
version = (ROOT / "VERSION").read_text(encoding="utf-8-sig").strip()
expected_assembly = version + ".0"
errors: list[str] = []

props = ET.parse(ROOT / "Directory.Build.props").getroot()
values = {node.tag: (node.text or "").strip() for node in props.iter()}
for field, expected in (("Version", version), ("AssemblyVersion", expected_assembly), ("FileVersion", expected_assembly)):
    if values.get(field) != expected:
        errors.append(f"Directory.Build.props {field}={values.get(field)!r}; expected {expected!r}")

readme = (ROOT / "README.md").read_text(encoding="utf-8-sig").splitlines()
expected_heading = f"# Jarvis Control - {version}"
if not readme or readme[0].strip() != expected_heading:
    errors.append(f"README heading is {readme[0].strip() if readme else '<empty>'!r}; expected {expected_heading!r}")

changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8-sig").splitlines()
first_release = next((line.strip() for line in changelog if line.startswith("## ")), None)
expected_release = f"## {version} -"
if first_release is None or not first_release.startswith(expected_release):
    errors.append(f"CHANGELOG first release is {first_release!r}; expected prefix {expected_release!r}")

if errors:
    print("Version verification failed:")
    for error in errors:
        print(" -", error)
    sys.exit(1)

print(f"Version verification passed: package {version}, assembly/file {expected_assembly}")
