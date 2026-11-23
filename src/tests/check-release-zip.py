"""Verify a Qpro release ZIP or its unpacked staging folder."""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
import re
from contextlib import nullcontext
from pathlib import Path
from zipfile import ZipFile


class StagedFolder:
    def __init__(self, folder: Path):
        self.folder = folder.resolve()

    def namelist(self):
        return [path.relative_to(self.folder.parent).as_posix() for path in self.folder.rglob("*") if path.is_file()]

    def open(self, name):
        return (self.folder.parent / name).open("rb")

    def read(self, name):
        with self.open(name) as stream:
            return stream.read()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("release_path", type=Path)
    arguments = parser.parse_args()
    staged = arguments.release_path.is_dir()
    with (nullcontext(StagedFolder(arguments.release_path)) if staged else ZipFile(arguments.release_path)) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise SystemExit("Duplicate ZIP entry names")
        roots = {name.split("/", 1)[0] for name in names}
        if len(roots) != 1:
            raise SystemExit(f"Expected one top-level folder; found {roots}")
        root = roots.pop()
        if not staged and not root.lower().startswith("qprofacetracking"):
            raise SystemExit(
                "The ZIP's app folder must keep the QproFaceTracking name so installed "
                "V3.0.0 updaters can open it; the download filename may use QproCompanion"
            )
        manifest_name = f"{root}/SHA256SUMS.txt"
        manifest = archive.read(manifest_name).decode("utf-8-sig")
        expected = {}
        for line in manifest.splitlines():
            digest, relative = line.split("  ", 1)
            if f"{root}/{relative}" in expected:
                raise SystemExit(f"Duplicate SHA256SUMS entry: {relative}")
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
        release_metadata = json.loads(archive.read(runtime_prefix + "release-manifest.json").decode("utf-8-sig"))
        backup = release_metadata.get("vrcftBackupBundled", False)
        if type(backup) is not bool:
            raise SystemExit("Invalid VRCFaceTracking backup declaration")
        if backup:
            for required in ("vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll",
                             "install-vrcft-eye-bridge.ps1", "uninstall-vrcft-eye-bridge.ps1",
                             "vrcft-module-installation.ps1"):
                if runtime_prefix + required not in files:
                    raise SystemExit(f"Missing VRCFaceTracking backup file: {required}")
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
