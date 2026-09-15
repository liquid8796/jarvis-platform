"""Regenerates HelpTexts.cs from the reference CLI's own --help output.

The CLI's help text is not written by hand: it is captured from the installed
Claude Code CLI and brand-swapped, which is what keeps `jarvis --help` identical
to `claude --help` line for line — including the help of every nested command
(`jarvis mcp add --help`, `jarvis project purge --help`, ...), which this script
discovers by walking the reference's own "Commands:" listings.

Usage (from anywhere):

    python <repo>/src/JarvisCode.Cli/gen-help-texts.py [--cli <path to claude.exe>]

Then run the parity suite, which diffs every generated constant against the
installed reference and fails when the two drift:

    dotnet test tests/JarvisCode.Parity.Tests
"""

import argparse
import io
import os
import re
import subprocess
import sys

# The pseudo-commands commander adds to every listing; they carry no help of
# their own that we reproduce.
IGNORED = {"help"}

MAX_DEPTH = 3


def run_help(cli, path):
    """`claude <path...> --help`, decoded as UTF-8 (the text carries dashes and glyphs)."""
    result = subprocess.run(
        [cli, *path, "--help"],
        capture_output=True,
        stdin=subprocess.DEVNULL,
        timeout=120,
    )
    return (result.stdout + result.stderr).decode("utf-8", errors="replace").replace("\r\n", "\n")


def children(help_text):
    """The command names a help listing declares, in its own order."""
    if "Commands:" not in help_text:
        return []
    section = help_text.split("Commands:", 1)[1]
    names = []
    for line in section.split("\n"):
        # An entry sits at exactly two spaces of indent; description text wraps
        # much deeper, so anything else in this section is prose.
        match = re.match(r"^  ([a-z][\w-]*(?:\|[\w-]+)*)(?:\s|$)", line)
        if not match:
            continue
        name = match.group(1).split("|")[0]
        if name not in IGNORED and name not in names:
            names.append(name)
    return names


def walk(cli):
    """Every command path whose help we reproduce, root first, breadth first."""
    root = run_help(cli, [])
    paths = [([], root)]
    frontier = [([name], None) for name in children(root)]
    while frontier:
        path, _ = frontier.pop(0)
        text = run_help(cli, path)
        paths.append((path, text))
        if len(path) < MAX_DEPTH:
            frontier.extend(([*path, name], None) for name in children(text))
    return paths


def rebrand(text):
    """Swap the product and command names, leaving claude.ai / claude-* model ids alone."""
    text = text.replace("Claude Code", "Jarvis Code")
    # Only the bare command word followed by whitespace: never claude.ai,
    # claude-fable-5, CLAUDE_CODE_*, or 'claude-...' inside quotes.
    return re.sub(r"(?<![\w.'\-])claude(?=\s)", "jarvis", text)


def const_name(path):
    if not path:
        return "Root"
    return "".join(part.capitalize() for segment in path for part in re.split(r"[-_]", segment))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--cli",
        default=os.path.join(os.path.expanduser("~"), ".local", "bin", "claude.exe"),
        help="the reference CLI to capture from",
    )
    args = parser.parse_args()
    if not os.path.exists(args.cli):
        sys.exit(f"reference CLI not found: {args.cli} (pass --cli)")

    version = subprocess.run(
        [args.cli, "--version"], capture_output=True, stdin=subprocess.DEVNULL, timeout=120
    ).stdout.decode("utf-8", errors="replace").strip()
    print(f"capturing from {args.cli} ({version})")

    captured = walk(args.cli)
    print(f"  {len(captured)} command(s)")

    out = io.StringIO()
    out.write(f"// GENERATED from the reference CLI's own --help output ({version},\n")
    out.write("// captured live, brand-swapped claude->jarvis). Regenerate with\n")
    out.write("// gen-help-texts.py rather than editing by hand.\n")
    out.write("namespace JarvisCode.Cli;\n\n")
    out.write("internal static class HelpTexts\n{\n")

    lookup = []
    for path, text in captured:
        name = const_name(path)
        escaped = rebrand(text).replace('"', '""')
        out.write(f'    public const string {name} =\n@"{escaped}";\n\n')
        if path:
            lookup.append((" ".join(path), name))

    out.write("    /// <summary>Command path (\"mcp add\") -> its help text.</summary>\n")
    out.write("    public static readonly IReadOnlyDictionary<string, string> ByPath =\n")
    out.write("        new Dictionary<string, string>(StringComparer.Ordinal)\n        {\n")
    for path, name in lookup:
        out.write(f'            ["{path}"] = {name},\n')
    out.write("        };\n")
    out.write("}\n")

    destination = os.path.join(os.path.dirname(os.path.abspath(__file__)), "HelpTexts.cs")
    with open(destination, "w", encoding="utf-8", newline="") as handle:
        handle.write(out.getvalue())
    print(f"wrote {destination}")


if __name__ == "__main__":
    main()
