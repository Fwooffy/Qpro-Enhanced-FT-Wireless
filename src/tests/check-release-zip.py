"""Verify that every file in a Qpro release ZIP matches its SHA256SUMS list."""

from __future__ import annotations

import argparse
import ast
import hashlib
import re
from pathlib import Path
from zipfile import ZipFile


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("zip_path")
    arguments = parser.parse_args()
    with ZipFile(arguments.zip_path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise SystemExit("Duplicate ZIP entry names")
        roots = {name.split("/", 1)[0] for name in names}
        if len(roots) != 1:
            raise SystemExit(f"Expected one top-level folder; found {roots}")
        root = roots.pop()
        if not root.lower().startswith("qprofacetracking"):
            raise SystemExit(
                "The ZIP's app folder must keep the QproFaceTracking name so installed "
                "V3.0.0 updaters can open it; the download filename may use QproCompanion"
            )
        manifest_name = f"{root}/SHA256SUMS.txt"
        manifest = archive.read(manifest_name).decode("utf-8-sig")
        expected = {}
        for line in manifest.splitlines():
            digest, relative = line.split("  ", 1)
            expected[f"{root}/{relative}"] = digest.lower()
        files = {name for name in names if not name.endswith("/")}
        if files - {manifest_name} != set(expected):
            raise SystemExit(
                f"Manifest mismatch: missing={sorted(files - {manifest_name} - set(expected))} "
                f"extra={sorted(set(expected) - files)}"
            )
        for name, digest in expected.items():
            with archive.open(name) as stream:
                hasher = hashlib.sha256()
                while chunk := stream.read(1024 * 1024):
                    hasher.update(chunk)
            if hasher.hexdigest() != digest:
                raise SystemExit(f"SHA-256 mismatch: {name}")
        user_path_marker = b":" + bytes((92,)) + b"users" + bytes((92,))
        text_extensions = (".json", ".md", ".ps1", ".py", ".txt", ".cmd")
        for name in sorted(files):
            if not name.lower().endswith(text_extensions):
                continue
            content = archive.read(name).replace(bytes((92, 92)), bytes((92,))).lower()
            if user_path_marker in content:
                raise SystemExit(f"Personal Windows user path in packaged file: {name}")
        # Portable PDBs can put a local absolute path in the DLL's PE debug
        # record even when no .pdb file is included in the release.
        binary_user_path = re.compile(rb"[a-z]:[\\/]+users[\\/]+", re.IGNORECASE)
        for name in sorted(files):
            with archive.open(name) as stream:
                overlap = b""
                while chunk := stream.read(64 * 1024):
                    content = overlap + chunk.replace(b"\x00", b"")
                    if binary_user_path.search(content):
                        raise SystemExit(f"Personal Windows user path in packaged binary: {name}")
                    overlap = content[-64:]
        local_modules = {path.stem for path in Path(__file__).resolve().parents[1].glob("*.py")}
        runtime_prefix = f"{root}/QproRuntime/"
        packaged_modules = {
            Path(name).stem for name in files
            if name.startswith(runtime_prefix) and "/" not in name[len(runtime_prefix):]
            and name.endswith(".py")
        }
        for name in sorted(files):
            if not name.startswith(runtime_prefix) or not name.endswith(".py"):
                continue
            try:
                tree = ast.parse(archive.read(name), filename=name)
            except (SyntaxError, UnicodeDecodeError) as error:
                raise SystemExit(f"Invalid packaged Python: {name}: {error}") from error
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    imported = (alias.name.split(".", 1)[0] for alias in node.names)
                elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                    imported = (node.module.split(".", 1)[0],)
                else:
                    continue
                for module in imported:
                    if module in local_modules and module not in packaged_modules:
                        raise SystemExit(f"Missing local module: {name} imports {module}.py")
        print(f"Verified {len(expected)} files under {root}")


if __name__ == "__main__":
    main()
