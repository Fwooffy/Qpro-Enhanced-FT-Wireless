#!/usr/bin/env python3
"""Train a stereo lower-face tongue model from a corrected prompted cache."""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

import numpy as np
import torch

if torch.version.hip:
    # Windows MIOpen HIPRTC cannot compile these tongue-model BatchNorm kernels.
    torch.backends.cudnn.enabled = False
from torch import nn
from torch.nn import functional as F
from torch.utils.data import DataLoader, Dataset, WeightedRandomSampler


SIGNED_TARGETS = {"horizontal", "vertical", "twist"}


class TongueFrames(Dataset):
    def __init__(self, cache: Path, indices: np.ndarray, augment: bool) -> None:
        self.images = np.load(cache / "images.npy", mmap_mode="r")
        self.targets = np.load(cache / "targets.npy", mmap_mode="r")
        self.native = np.load(cache / "native_tongue_out.npy", mmap_mode="r")
        self.indices = np.asarray(indices, dtype=np.int64)
        self.augment = augment

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, item: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        index = int(self.indices[item])
        images = torch.from_numpy(np.array(self.images[index], copy=True)).float().div_(255)
        if self.augment:
            contrast = random.uniform(0.88, 1.12)
            brightness = random.uniform(-0.05, 0.05)
            images.mul_(contrast).add_(brightness).clamp_(0, 1)
            if random.random() < 0.15:
                images.add_(torch.randn_like(images) * random.uniform(0.0, 0.015)).clamp_(0, 1)
            if random.random() < 0.70:
                # Apply one geometric perturbation to both synchronized views;
                # independent crops would manufacture false stereo disparity.
                pad = random.randint(2, 9)
                padded = F.pad(images, (pad, pad, pad, pad), mode="replicate")
                top = random.randint(0, pad * 2)
                left = random.randint(0, pad * 2)
                size = images.shape[-1]
                images = padded[:, top:top + size, left:left + size]
        target = torch.from_numpy(np.array(self.targets[index], copy=True)).float()
        native = torch.tensor(float(self.native[index]), dtype=torch.float32)
        return images, target, native


class TongueCameraEncoder(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        channels = (1, 24, 40, 64, 96)
        layers: list[nn.Module] = []
        for input_channels, output_channels in zip(channels, channels[1:]):
            layers.extend(
                [
                    nn.Conv2d(input_channels, output_channels, 3, stride=2, padding=1, bias=False),
                    nn.BatchNorm2d(output_channels),
                    nn.SiLU(inplace=True),
                ]
            )
        layers.append(nn.AdaptiveAvgPool2d(1))
        self.network = nn.Sequential(*layers)

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        return self.network(image).flatten(1)


class StereoTongueModel(nn.Module):
    """Shared view encoder with late stereo fusion and bounded outputs."""

    def __init__(self, target_names: list[str]) -> None:
        super().__init__()
        self.target_names = list(target_names)
        self.encoder = TongueCameraEncoder()
        self.fusion = nn.Sequential(
            nn.Linear(96 * 4, 256),
            nn.SiLU(inplace=True),
            nn.Dropout(0.12),
            nn.Linear(256, 128),
            nn.SiLU(inplace=True),
            nn.Linear(128, len(target_names)),
        )
        signed = [name in SIGNED_TARGETS for name in target_names]
        self.register_buffer("signed_mask", torch.tensor(signed, dtype=torch.bool))

    def forward(self, cameras: torch.Tensor) -> torch.Tensor:
        left = self.encoder(cameras[:, 0:1])
        right = self.encoder(cameras[:, 1:2])
        fused = torch.cat((left, right, torch.abs(left - right), left * right), dim=1)
        logits = self.fusion(fused)
        return torch.where(self.signed_mask, torch.tanh(logits), torch.sigmoid(logits))


class ResidualBlock(nn.Module):
    def __init__(self, channels: int) -> None:
        super().__init__()
        self.network = nn.Sequential(
            nn.Conv2d(channels, channels, 3, padding=1, bias=False),
            nn.BatchNorm2d(channels),
            nn.SiLU(inplace=True),
            nn.Conv2d(channels, channels, 3, padding=1, bias=False),
            nn.BatchNorm2d(channels),
        )

    def forward(self, values: torch.Tensor) -> torch.Tensor:
        return F.silu(values + self.network(values), inplace=True)


class SpatialTongueEncoder(nn.Module):
    """Preserves a spatial feature map so stereo evidence survives fusion."""

    def __init__(self) -> None:
        super().__init__()
        layers: list[nn.Module] = [
            nn.Conv2d(1, 32, 5, stride=2, padding=2, bias=False),
            nn.BatchNorm2d(32),
            nn.SiLU(inplace=True),
            ResidualBlock(32),
        ]
        for input_channels, output_channels in ((32, 64), (64, 96), (96, 160)):
            layers.extend([
                nn.Conv2d(input_channels, output_channels, 3, stride=2, padding=1, bias=False),
                nn.BatchNorm2d(output_channels),
                nn.SiLU(inplace=True),
                ResidualBlock(output_channels),
            ])
        self.network = nn.Sequential(*layers)

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        return self.network(image)


class SpatialStereoTongueModel(nn.Module):
    """Larger spatial stereo model intended for an RTX-class PC runtime."""

    def __init__(self, target_names: list[str]) -> None:
        super().__init__()
        self.target_names = list(target_names)
        self.encoder = SpatialTongueEncoder()
        self.stereo_fusion = nn.Sequential(
            nn.Conv2d(160 * 4, 224, 1, bias=False),
            nn.BatchNorm2d(224),
            nn.SiLU(inplace=True),
            ResidualBlock(224),
            nn.Conv2d(224, 256, 3, stride=2, padding=1, bias=False),
            nn.BatchNorm2d(256),
            nn.SiLU(inplace=True),
            ResidualBlock(256),
        )
        self.head = nn.Sequential(
            nn.Linear(256 * 2, 384),
            nn.SiLU(inplace=True),
            nn.Dropout(0.18),
            nn.Linear(384, 192),
            nn.SiLU(inplace=True),
            nn.Dropout(0.08),
            nn.Linear(192, len(target_names)),
        )
        signed = [name in SIGNED_TARGETS for name in target_names]
        self.register_buffer("signed_mask", torch.tensor(signed, dtype=torch.bool))

    def forward(self, cameras: torch.Tensor) -> torch.Tensor:
        left = self.encoder(cameras[:, 0:1])
        right = self.encoder(cameras[:, 1:2])
        stereo = torch.cat((left, right, torch.abs(left - right), left * right), dim=1)
        fused = self.stereo_fusion(stereo)
        pooled = torch.cat((
            F.adaptive_avg_pool2d(fused, 1).flatten(1),
            F.adaptive_max_pool2d(fused, 1).flatten(1),
        ), dim=1)
        logits = self.head(pooled)
        return torch.where(self.signed_mask, torch.tanh(logits), torch.sigmoid(logits))


def create_model(architecture: str, target_names: list[str]) -> nn.Module:
    if architecture == "legacy-late-fusion-v1":
        return StereoTongueModel(target_names)
    if architecture == "spatial-stereo-resnet-v2":
        return SpatialStereoTongueModel(target_names)
    raise ValueError(f"Unknown tongue model architecture: {architecture}")


def blocked_train_validation_split(
    step_ids: np.ndarray,
    trainable: np.ndarray,
    block_size: int = 36,
    dataset_type: str = "prompted-video",
) -> tuple[np.ndarray, np.ndarray]:
    indices = np.arange(len(step_ids), dtype=np.int64)
    training: list[np.ndarray] = []
    validation: list[np.ndarray] = []
    for step in np.unique(step_ids[trainable]):
        selected = indices[(step_ids == step) & trainable]
        if len(selected) < 4:
            continue
        if dataset_type == "manual-stereo-stills":
            # Each press is a deliberate repetition. Keep at least one complete
            # repetition per card unseen, instead of splitting adjacent video.
            holdout = np.zeros(len(selected), dtype=np.bool_)
            holdout[-max(1, round(len(selected) * 0.20)):] = True
        else:
            blocks = np.arange(len(selected)) // block_size
            holdout = blocks % 5 == 4
            if not np.any(holdout):
                holdout[-max(1, len(selected) // 5):] = True
        training.append(selected[~holdout])
        validation.append(selected[holdout])
    return np.concatenate(training), np.concatenate(validation)


def balanced_step_weights(step_ids: np.ndarray, indices: np.ndarray) -> np.ndarray:
    selected_steps = step_ids[indices]
    unique, counts = np.unique(selected_steps, return_counts=True)
    inverse = {int(step): 1.0 / float(count) for step, count in zip(unique, counts)}
    weights = np.asarray([inverse[int(step)] for step in selected_steps], dtype=np.float64)
    return weights / np.mean(weights)


def target_loss(
    prediction: torch.Tensor, target: torch.Tensor, target_names: list[str]
) -> torch.Tensor:
    visibility_index = target_names.index("visibility")
    expected_visible = target[:, visibility_index]
    predicted_visible = prediction[:, visibility_index]
    # BCE on a post-sigmoid probability is intentionally written out here.
    # torch.nn.functional.binary_cross_entropy rejects CUDA autocast even when
    # its inputs are promoted; the explicit float32 log form is AMP-safe.
    # Convert before clamping: float16 rounds 1 - 1e-5 back to exactly 1,
    # which would still make log1p(-p) infinite.
    probability = predicted_visible.float().clamp(1e-5, 1.0 - 1e-5)
    expected_probability = expected_visible.float()
    visibility_loss = -(
        expected_probability * torch.log(probability)
        + (1.0 - expected_probability) * torch.log1p(-probability)
    )
    # Hidden-tongue hard negatives get extra authority to reduce false TongueOut
    # activations from smiles, teeth, jaw opening, cheek motion, and speech.
    visibility_weights = torch.where(
        expected_visible >= 0.5,
        torch.full_like(expected_visible, 1.25),
        torch.full_like(expected_visible, 1.70),
    )
    visibility_loss = torch.mean(visibility_loss * visibility_weights)

    regression = F.smooth_l1_loss(prediction, target, beta=0.08, reduction="none")
    column_weights = torch.tensor(
        [0.0, 1.4, 2.2, 2.2, 2.5, 2.5, 2.5, 2.2, 2.2, 2.0],
        dtype=prediction.dtype, device=prediction.device,
    )
    if len(column_weights) != len(target_names):
        column_weights = torch.ones(len(target_names), device=prediction.device)
        column_weights[visibility_index] = 0.0
    visible_mask = (expected_visible >= 0.5).to(prediction.dtype).unsqueeze(1)
    active = (torch.abs(target) > 0.10).to(prediction.dtype)
    # Prompt balancing gives each pose card equal sampling mass, but a shape
    # head still sees many valid visible-tongue zeros for every one positive
    # card. Give rare active directions/shapes enough authority to avoid the
    # deceptively low-loss solution of predicting zero for every detail head.
    active_boost = torch.tensor(
        [0.0, 2.0, 6.0, 6.0, 4.0, 4.0, 4.0, 4.0, 4.0, 2.0],
        dtype=prediction.dtype, device=prediction.device,
    )
    if len(active_boost) != len(target_names):
        active_boost = torch.full(
            (len(target_names),), 6.0,
            dtype=prediction.dtype, device=prediction.device,
        )
        active_boost[visibility_index] = 0.0
    regression_weights = visible_mask * column_weights * (1.0 + active_boost * active)
    regression_loss = torch.sum(regression * regression_weights) / regression_weights.sum().clamp_min(1.0)
    return 1.8 * visibility_loss + regression_loss


def run_training_epoch(
    model: nn.Module,
    loader: DataLoader,
    optimizer: torch.optim.Optimizer,
    scaler: torch.amp.GradScaler | None,
    device: torch.device,
    target_names: list[str],
) -> float:
    model.train()
    total = 0.0
    count = 0
    for images, target, _native in loader:
        images = images.to(device, non_blocking=True)
        target = target.to(device, non_blocking=True)
        optimizer.zero_grad(set_to_none=True)
        with torch.amp.autocast(device_type=device.type, enabled=device.type == "cuda"):
            prediction = model(images)
            loss = target_loss(prediction, target, target_names)
        if not torch.isfinite(loss):
            raise FloatingPointError(
                "Tongue training produced a non-finite loss; no optimizer step was applied"
            )
        if scaler is not None:
            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()
        else:
            loss.backward()
            optimizer.step()
        total += float(loss.detach()) * len(images)
        count += len(images)
    return total / max(1, count)


def f1_at_threshold(values: np.ndarray, target: np.ndarray, threshold: float) -> float:
    return classification_at_threshold(values, target, threshold)["f1"]


def classification_at_threshold(
    values: np.ndarray, target: np.ndarray, threshold: float
) -> dict[str, float]:
    predicted = values >= threshold
    expected = target >= 0.5
    true_positive = int(np.count_nonzero(predicted & expected))
    false_positive = int(np.count_nonzero(predicted & ~expected))
    false_negative = int(np.count_nonzero(~predicted & expected))
    true_negative = int(np.count_nonzero(~predicted & ~expected))
    precision = true_positive / max(1, true_positive + false_positive)
    recall = true_positive / max(1, true_positive + false_negative)
    return {
        "f1": 2.0 * true_positive / max(
            1, 2 * true_positive + false_positive + false_negative
        ),
        "precision": precision,
        "recall": recall,
        "falsePositiveRate": false_positive / max(1, false_positive + true_negative),
        "falseNegativeRate": false_negative / max(1, false_negative + true_positive),
    }


def evaluate(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
    target_names: list[str],
) -> dict[str, object]:
    model.eval()
    predictions: list[np.ndarray] = []
    targets: list[np.ndarray] = []
    natives: list[np.ndarray] = []
    with torch.no_grad():
        for images, target, native in loader:
            prediction = model(images.to(device, non_blocking=True)).cpu().numpy()
            predictions.append(prediction)
            targets.append(target.numpy())
            natives.append(native.numpy())
    prediction = np.concatenate(predictions)
    target = np.concatenate(targets)
    native = np.concatenate(natives)
    absolute = np.abs(prediction - target)
    active = np.abs(target) > 0.10
    per_target = {}
    for index, name in enumerate(target_names):
        mask = active[:, index]
        per_target[name] = {
            "mae": float(np.mean(absolute[:, index])),
            "activeMae": float(np.mean(absolute[mask, index])) if np.any(mask) else None,
            "activeSamples": int(np.count_nonzero(mask)),
        }
    visibility_index = target_names.index("visibility")
    camera_visibility = prediction[:, visibility_index]
    expected_visibility = target[:, visibility_index]
    thresholds = np.linspace(0.15, 0.85, 71)
    # Keep both confidence sources, but let held-out prompts choose how much
    # authority native TongueOut deserves. Its Quest Pro edge cases are known
    # to be weaker than the dedicated camera model, so force only a small
    # minimum native contribution rather than assuming a 50/50 blend.
    blends = []
    for camera_weight in np.linspace(0.50, 0.95, 10):
        fused = camera_weight * camera_visibility + (1.0 - camera_weight) * native
        for threshold in thresholds:
            classification = classification_at_threshold(
                fused, expected_visibility, float(threshold)
            )
            blends.append(
                (
                    classification["f1"],
                    classification["precision"],
                    float(camera_weight),
                    float(threshold),
                )
            )
    best_f1, _best_precision, best_camera_weight, best_threshold = max(blends)
    best_fused = best_camera_weight * camera_visibility + (1.0 - best_camera_weight) * native
    best_classification = classification_at_threshold(
        best_fused, expected_visibility, best_threshold
    )
    return {
        "mae": float(np.mean(absolute)),
        "activeMae": float(np.mean(absolute[active])) if np.any(active) else 0.0,
        "perTarget": per_target,
        "cameraVisibilityF1AtHalf": f1_at_threshold(camera_visibility, expected_visibility, 0.5),
        "nativeVisibilityF1AtHalf": f1_at_threshold(native, expected_visibility, 0.5),
        "equalBlendVisibilityF1": max(
            f1_at_threshold(
                0.5 * camera_visibility + 0.5 * native,
                expected_visibility,
                float(threshold),
            )
            for threshold in thresholds
        ),
        "fusedVisibilityCameraWeight": best_camera_weight,
        "fusedVisibilityThreshold": float(best_threshold),
        "fusedVisibilityF1": best_f1,
        "fusedVisibilityPrecision": best_classification["precision"],
        "fusedVisibilityRecall": best_classification["recall"],
        "fusedVisibilityFalsePositiveRate": best_classification["falsePositiveRate"],
        "fusedVisibilityFalseNegativeRate": best_classification["falseNegativeRate"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Train the Quest Pro stereo tongue model")
    parser.add_argument("cache")
    parser.add_argument("--epochs", type=int, default=24)
    parser.add_argument("--batch-size", type=int, default=96)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--device", default="auto", help="auto, cpu, or a PyTorch CUDA device such as cuda:0")
    parser.add_argument("--output", default="models/qpro-stereo-tongue-v1.pt")
    parser.add_argument(
        "--initial-checkpoint",
        help="start from an existing compatible checkpoint for personal refinement",
    )
    parser.add_argument(
        "--architecture",
        choices=("legacy-late-fusion-v1", "spatial-stereo-resnet-v2"),
        default="spatial-stereo-resnet-v2",
    )
    parser.add_argument(
        "--checkpoint-focus",
        choices=("balanced", "visibility", "direction"),
        default="balanced",
        help="choose whether checkpoint selection prioritizes all heads, visibility, or X/Y direction",
    )
    arguments = parser.parse_args()
    if arguments.epochs < 1 or arguments.batch_size < 1:
        parser.error("epochs and batch size must be positive")

    seed = 20260806
    torch.manual_seed(seed)
    np.random.seed(seed)
    random.seed(seed)
    cache = Path(arguments.cache).resolve()
    metadata = json.loads((cache / "metadata.json").read_text(encoding="utf-8"))
    target_names = list(metadata["targetNames"])
    step_ids = np.load(cache / "step_ids.npy", mmap_mode="r")
    trainable = np.load(cache / "trainable.npy", mmap_mode="r")
    dataset_type = str(metadata.get("datasetType", "prompted-video"))
    train_indices, validation_indices = blocked_train_validation_split(
        step_ids, trainable, dataset_type=dataset_type
    )
    if not len(train_indices) or not len(validation_indices):
        raise ValueError("Dataset does not contain enough repeated samples for a train/validation split")
    weights = balanced_step_weights(step_ids, train_indices)
    train_data = TongueFrames(cache, train_indices, augment=True)
    validation_data = TongueFrames(cache, validation_indices, augment=False)
    sampler = WeightedRandomSampler(
        torch.as_tensor(weights, dtype=torch.double),
        num_samples=len(train_indices),
        replacement=True,
        generator=torch.Generator().manual_seed(seed),
    )
    selected_device = "cuda:0" if arguments.device == "auto" and torch.cuda.is_available() else ("cpu" if arguments.device == "auto" else arguments.device)
    device = torch.device(selected_device)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but PyTorch cannot access an NVIDIA GPU")
    train_loader = DataLoader(
        train_data,
        batch_size=arguments.batch_size,
        sampler=sampler,
        num_workers=0,
        pin_memory=device.type == "cuda",
    )
    validation_loader = DataLoader(
        validation_data,
        batch_size=arguments.batch_size,
        shuffle=False,
        num_workers=0,
        pin_memory=device.type == "cuda",
    )
    model = create_model(arguments.architecture, target_names).to(device)
    if arguments.initial_checkpoint:
        initial_path = Path(arguments.initial_checkpoint).resolve()
        initial = torch.load(initial_path, map_location="cpu", weights_only=False)
        if list(initial["targetNames"]) != target_names:
            raise ValueError("Initial checkpoint target schema does not match the dataset")
        if str(initial.get("architecture")) != arguments.architecture:
            raise ValueError("Initial checkpoint architecture does not match the requested model")
        model.load_state_dict(initial["modelState"])
        print(f"Refining from: {initial_path}")
    optimizer = torch.optim.AdamW(model.parameters(), lr=arguments.learning_rate, weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda") if device.type == "cuda" else None
    output = Path(arguments.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    best_score = float("inf")
    print(
        f"Device: {device}; balanced train draws: {len(train_indices)}; "
        f"held-out prompted frames: {len(validation_indices)}",
        flush=True,
    )
    for epoch in range(1, arguments.epochs + 1):
        loss = run_training_epoch(
            model, train_loader, optimizer, scaler, device, target_names
        )
        metrics = evaluate(model, validation_loader, device, target_names)
        trained_active_maes = [
            float(value["activeMae"])
            for name, value in metrics["perTarget"].items()
            if name != "visibility" and value["activeMae"] is not None
        ]
        balanced_active_mae = float(np.mean(trained_active_maes))
        direction_values = [
            metrics["perTarget"][name]["activeMae"]
            for name in ("horizontal", "vertical")
            if metrics["perTarget"][name]["activeMae"] is not None
        ]
        direction_active_mae = float(np.mean(direction_values)) if direction_values else 0.0
        if arguments.checkpoint_focus == "direction":
            score = (
                0.25 * float(metrics["mae"])
                + 0.25 * float(metrics["activeMae"])
                + direction_active_mae
                + 0.15 * (1.0 - float(metrics["fusedVisibilityF1"]))
            )
            score_description = (
                "0.25*mae + 0.25*active_mae + mean(horizontal,vertical)_active_mae "
                "+ 0.15*(1-visibility_f1)"
            )
        elif arguments.checkpoint_focus == "visibility":
            score = (
                0.60 * (1.0 - float(metrics["fusedVisibilityF1"]))
                + 0.75 * float(metrics["fusedVisibilityFalsePositiveRate"])
                + 0.25 * float(metrics["fusedVisibilityFalseNegativeRate"])
                + 0.10 * float(metrics["perTarget"]["visibility"]["mae"])
            )
            score_description = (
                "0.60*(1-visibility_f1) + 0.75*false_positive_rate + "
                "0.25*false_negative_rate + 0.10*visibility_mae"
            )
        else:
            score = (
                float(metrics["mae"])
                + 0.50 * float(metrics["activeMae"])
                + 0.50 * (1.0 - float(metrics["fusedVisibilityF1"]))
            )
            score_description = "mae + 0.50*active_mae + 0.50*(1-visibility_f1)"
        print(
            f"TRAIN_EPOCH current={epoch} total={arguments.epochs} "
            f"focus={arguments.checkpoint_focus}",
            flush=True,
        )
        print(
            f"Epoch {epoch:02d}: loss={loss:.5f} val_mae={metrics['mae']:.4f} "
            f"active={metrics['activeMae']:.4f} direction={direction_active_mae:.4f} "
            f"balanced_active={balanced_active_mae:.4f} "
            f"fused_visibility_f1={metrics['fusedVisibilityF1']:.3f} "
            f"fp={metrics['fusedVisibilityFalsePositiveRate']:.3f} "
            f"fn={metrics['fusedVisibilityFalseNegativeRate']:.3f}",
            flush=True,
        )
        if score < best_score:
            best_score = score
            scripted = torch.jit.script(model.eval().cpu())
            scripted_path = output.with_suffix(".torchscript.pt")
            scripted.save(str(scripted_path))
            torch.save(
                {
                    "version": 2,
                    "architecture": arguments.architecture,
                    "modelState": model.state_dict(),
                    "targetNames": target_names,
                    "imageSize": metadata["imageSize"],
                    "validation": metrics,
                    "split": (
                        "last 20 percent of manual repetitions within each prompt"
                        if dataset_type == "manual-stereo-stills"
                        else "every fifth 36-frame temporal block within each trainable prompt"
                    ),
                    "sampling": "inverse prompt-frequency balanced",
                    "checkpointScore": score_description,
                    "checkpointFocus": arguments.checkpoint_focus,
                    "parentCheckpoint": (
                        str(Path(arguments.initial_checkpoint).resolve())
                        if arguments.initial_checkpoint
                        else None
                    ),
                    "visibilityGate": {
                        "formula": "w * camera_visibility + (1-w) * native_TongueOut",
                        "cameraWeight": metrics["fusedVisibilityCameraWeight"],
                        "threshold": metrics["fusedVisibilityThreshold"],
                    },
                },
                output,
            )
            model.to(device)
            print(f"  Saved best checkpoint: {output}")
    checkpoint = torch.load(output, map_location="cpu", weights_only=False)
    metrics = checkpoint["validation"]
    print(
        f"Training complete: MAE={metrics['mae']:.4f}; active MAE={metrics['activeMae']:.4f}; "
        f"fused visibility F1={metrics['fusedVisibilityF1']:.3f} at "
        f"{metrics['fusedVisibilityThreshold']:.2f}"
    )
    print(f"MODEL {output}")
    if device.type == "cuda" and torch.version.hip:
        # Windows ROCm can leave the Python process alive after training unless
        # outstanding GPU work is synchronized before interpreter shutdown.
        print("TRAIN_STATUS phase=rocm-finalize", flush=True)
        torch.cuda.synchronize(device)
        print("TRAIN_STATUS phase=rocm-finalized", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
