"""Stage tongue training and publish complete model pairs without replacement.

Old combined exports can still provide their original tongue branch when a
user refines them. New models contain only tongue outputs.
"""

import argparse
import json
import os
import shutil
import uuid
from pathlib import Path

import torch

from train_tongue_model import CHEEK_ARCHITECTURE_PREFIX, CHEEK_TARGET_NAMES


def tongue_parent(checkpoint):
    """Extract the original tongue branch; reject incomplete combined states."""
    if not str(checkpoint.get("architecture", "")).startswith(CHEEK_ARCHITECTURE_PREFIX):
        return checkpoint
    architecture = checkpoint.get("baseArchitecture")
    names = checkpoint.get("targetNames", [])
    state = {key.removeprefix("parent."): value for key, value in checkpoint["modelState"].items()
             if key.startswith("parent.")}
    if not architecture or checkpoint["architecture"] != CHEEK_ARCHITECTURE_PREFIX + architecture or \
            len(names) <= 2 or tuple(names[-2:]) != CHEEK_TARGET_NAMES or \
            any(name in CHEEK_TARGET_NAMES for name in names[:-2]) or not state:
        raise ValueError("Unsupported combined lower-face target schema or missing tongue state")
    plain = {**checkpoint, "architecture": architecture, "targetNames": list(names[:-2]), "modelState": state}
    for key in ("baseArchitecture", "cheekTraining", "experimental"):
        plain.pop(key, None)
    return plain


def publish_model_files(files, models: Path, version: int):
    """Publish complete files exclusively; metadata is the final commit marker."""
    if version < 1:
        raise ValueError("A positive model version is required")
    models.mkdir(parents=True, exist_ok=True)
    stem = f"qpro-stereo-tongue-v{version}"
    if any(path.name.startswith((stem + "-", stem + ".")) for path in models.iterdir()):
        raise ValueError("This model version is already occupied; existing models will not be overwritten")
    temporary, published = [], []
    try:
        for source, ending in files:
            destination = models / (stem + ending)
            staged = models / (".qpro-publish-" + uuid.uuid4().hex + ".tmp")
            temporary.append(staged)
            with staged.open("xb") as output, Path(source).open("rb") as input_stream:
                shutil.copyfileobj(input_stream, output)
            # Windows rename refuses an existing destination. A hard link
            # provides the same exclusive behavior on other test hosts.
            if os.name == "nt":
                os.rename(staged, destination)
            else:
                os.link(staged, destination)
                staged.unlink()
            published.append(destination)
    except BaseException:
        for path in published:
            path.unlink(missing_ok=True)
        raise
    finally:
        for path in temporary:
            path.unlink(missing_ok=True)


def publish_pair(staged_root: Path, root: Path, version: int):
    """Validate and commit a tongue-only pair; metadata is published last."""
    if staged_root.resolve() == root.resolve():
        raise ValueError("Train in a private staging directory before publishing")
    stem = f"qpro-stereo-tongue-v{version}"
    models = staged_root / "models"
    gate_path, direction_path = [models / (stem + ending) for ending in ("-gate.pt", "-direction.pt")]
    gate = torch.load(gate_path, map_location="cpu", weights_only=True)
    direction_checkpoint = torch.load(direction_path, map_location="cpu", weights_only=True)
    direction = direction_checkpoint
    if any(str(checkpoint.get("architecture", "")).startswith(CHEEK_ARCHITECTURE_PREFIX)
           or any(name in CHEEK_TARGET_NAMES for name in checkpoint.get("targetNames", ()))
           for checkpoint in (gate, direction)):
        raise ValueError("New models must contain tongue outputs only; unwrap the parent before refinement")
    if list(gate["targetNames"]) != list(direction["targetNames"]):
        raise ValueError("The trained gate and direction model use different tongue targets")
    files = [(gate_path, "-gate.pt"), (direction_path, "-direction.pt")]
    for branch in ("gate", "direction"):
        script = models / f"{stem}-{branch}.torchscript.pt"
        if script.exists():
            files.append((script, f"-{branch}.torchscript.pt"))
    metadata = models / (stem + ".metadata.json")
    if metadata.exists():
        data = json.loads(metadata.read_text(encoding="utf-8"))
        if data.get("version") != version or data.get("hasCameraCheeks", False) is not False:
            raise ValueError("Model metadata does not describe the staged checkpoint")
        files.append((metadata, ".metadata.json"))
    publish_model_files(files, root / "models", version)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    unwrap = commands.add_parser("unwrap")
    unwrap.add_argument("checkpoint", type=Path)
    unwrap.add_argument("--output", type=Path, required=True)
    publish = commands.add_parser("publish")
    publish.add_argument("--staged-root", type=Path, required=True)
    publish.add_argument("--root", type=Path, required=True)
    publish.add_argument("--version", type=int, required=True)
    args = parser.parse_args()
    if args.command == "unwrap":
        if args.output.resolve() == args.checkpoint.resolve():
            raise ValueError("Use a new temporary tongue-parent path")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("xb") as stream:
            torch.save(tongue_parent(torch.load(args.checkpoint, map_location="cpu", weights_only=True)), stream)
    else:
        publish_pair(args.staged_root, args.root, args.version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
