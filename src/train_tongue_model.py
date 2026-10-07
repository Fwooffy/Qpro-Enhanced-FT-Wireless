#!/usr/bin/env python3
"""Train a stereo lower-face tongue model from a corrected prompted cache."""

from __future__ import annotations

import argparse
import hashlib
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
from tongue_visibility_calibration import (
    choose_visibility_gate, classification_at_threshold, f1_at_threshold,
)
from qpro_gpu import validated_torch_device_name
from tongue_image_processing import (
    MODES, preprocess_stereo_images, resolve_input_preprocessing,
)


SIGNED_TARGETS = {"horizontal", "vertical", "twist"}
CHEEK_TARGET_NAMES = ("cheekPuffLeft", "cheekPuffRight")
CHEEK_ARCHITECTURE_PREFIX = "cheek-augmented-"


def parent_checkpoint_metadata(path: str | Path | None) -> tuple[str | None, str | None]:
    """Keep a verifiable parent reference without embedding a private PC path."""
    if path is None:
        return None, None
    source = Path(path).resolve(strict=True)
    digest = hashlib.sha256()
    with source.open("rb") as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return source.name, digest.hexdigest()


def shade_local_mouth_area(
    images: torch.Tensor, top: int, left: int, height: int, width: int,
    strength: float,
) -> torch.Tensor:
    """Dim a small, soft region in both views without hiding tongue geometry.

    Beard, moustache, bandage, and IR illumination can alter local contrast.
    This training-only perturbation leaves the labels and stereo geometry
    intact; a wearer's own labeled capture is still needed to measure quality.
    """
    vertical = (1.0 - torch.linspace(-1.0, 1.0, height).abs()).clamp_min(0.0)
    horizontal = (1.0 - torch.linspace(-1.0, 1.0, width).abs()).clamp_min(0.0)
    attenuation = 1.0 - strength * torch.outer(vertical, horizontal)
    images[:, top:top + height, left:left + width].mul_(attenuation)
    return images


class TongueFrames(Dataset):
    def __init__(
        self, cache: Path, indices: np.ndarray, augment: bool,
        input_preprocessing: str = "raw-v1",
    ) -> None:
        self.images = np.load(cache / "images.npy", mmap_mode="r")
        self.targets = np.load(cache / "targets.npy", mmap_mode="r")
        self.native = np.load(cache / "native_tongue_out.npy", mmap_mode="r")
        self.indices = np.asarray(indices, dtype=np.int64)
        self.augment = augment
        self.input_preprocessing = resolve_input_preprocessing(
            {"inputPreprocessing": input_preprocessing}
        )

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, item: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        index = int(self.indices[item])
        pixels = preprocess_stereo_images(self.images[index], self.input_preprocessing)
        images = torch.from_numpy(np.array(pixels, copy=True)).float().div_(255)
        if self.augment:
            contrast = random.uniform(0.88, 1.12)
            brightness = random.uniform(-0.05, 0.05)
            images.mul_(contrast).add_(brightness).clamp_(0, 1)
            if random.random() < 0.15:
                images.add_(torch.randn_like(images) * random.uniform(0.0, 0.015)).clamp_(0, 1)
            if random.random() < 0.25:
                # A modest local contrast change helps refinement avoid relying
                # on one bare-skin appearance. Apply the same soft area to both
                # cameras and never replace pixels with a solid occluder: the
                # tongue target must remain visible in the training example.
                size = images.shape[-1]
                width = random.randint(max(8, size // 8), max(9, size // 3))
                height = random.randint(max(8, size // 12), max(9, size // 4))
                left = random.randint(size // 6, min(size - width, size * 2 // 3))
                top = random.randint(size // 3, min(size - height, size * 2 // 3))
                shade_local_mouth_area(
                    images, top, left, height, width,
                    random.uniform(0.12, 0.28),
                )
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

    def stereo_features(self, cameras: torch.Tensor) -> torch.Tensor:
        left = self.encoder(cameras[:, 0:1])
        right = self.encoder(cameras[:, 1:2])
        return torch.cat((left, right, torch.abs(left - right), left * right), dim=1)

    def tongue_from_features(self, features: torch.Tensor) -> torch.Tensor:
        logits = self.fusion(features)
        return torch.where(self.signed_mask, torch.tanh(logits), torch.sigmoid(logits))

    def forward(self, cameras: torch.Tensor) -> torch.Tensor:
        return self.tongue_from_features(self.stereo_features(cameras))


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

    def stereo_features(self, cameras: torch.Tensor) -> torch.Tensor:
        left = self.encoder(cameras[:, 0:1])
        right = self.encoder(cameras[:, 1:2])
        stereo = torch.cat((left, right, torch.abs(left - right), left * right), dim=1)
        fused = self.stereo_fusion(stereo)
        return torch.cat((
            F.adaptive_avg_pool2d(fused, 1).flatten(1),
            F.adaptive_max_pool2d(fused, 1).flatten(1),
        ), dim=1)

    def tongue_from_features(self, features: torch.Tensor) -> torch.Tensor:
        logits = self.head(features)
        return torch.where(self.signed_mask, torch.tanh(logits), torch.sigmoid(logits))

    def forward(self, cameras: torch.Tensor) -> torch.Tensor:
        return self.tongue_from_features(self.stereo_features(cameras))


class FrozenTongueCheekModel(nn.Module):
    """Add cheek regression without changing the trained tongue model.

    The parent stays in evaluation mode even while its cheek head trains.
    Freezing parameters alone would still update BatchNorm statistics and
    enable Dropout, changing tongue output and the meaning of its features.
    Both outputs share one stereo encoder pass during live inference.
    """

    def __init__(self, base_architecture: str, target_names: list[str]) -> None:
        super().__init__()
        if tuple(target_names[-2:]) != CHEEK_TARGET_NAMES or len(target_names) <= 2:
            raise ValueError("Combined model needs the tongue targets followed by both cheek targets")
        if any(name in CHEEK_TARGET_NAMES for name in target_names[:-2]):
            raise ValueError("Combined model has duplicate cheek targets")
        self.base_architecture = base_architecture
        self.target_names = list(target_names)
        self.parent = create_model(base_architecture, target_names[:-2])
        if isinstance(self.parent, StereoTongueModel):
            feature_count = 96 * 4
        elif isinstance(self.parent, SpatialStereoTongueModel):
            feature_count = 256 * 2
        else:
            raise ValueError("Only a plain tongue checkpoint can be the frozen parent")
        self.parent.requires_grad_(False)
        self.parent.eval()
        # A trained tongue encoder has unequal feature scales. Feeding them
        # directly to a new cheek head can saturate its sigmoid and produce a
        # constant zero output. These fixed training-set statistics affect
        # only the cheek branch; they never normalize the parent's input.
        self.register_buffer("cheek_feature_mean", torch.zeros(feature_count))
        self.register_buffer("cheek_feature_scale", torch.ones(feature_count))
        self.cheek_head = nn.Sequential(
            nn.Linear(feature_count, 128), nn.SiLU(), nn.Linear(128, 2), nn.Sigmoid(),
        )

    def train(self, mode: bool = True) -> "FrozenTongueCheekModel":
        super().train(mode)
        self.parent.eval()
        return self

    def load_state_dict(self, state_dict, strict: bool = True, assign: bool = False):
        # The first private cheek experiment predates feature normalization.
        # Identity statistics retain its original inference behavior; never
        # infer statistics from live frames or a validation capture.
        if "cheek_feature_mean" not in state_dict and "cheek_feature_scale" not in state_dict:
            state_dict = dict(state_dict)
            state_dict["cheek_feature_mean"] = torch.zeros_like(self.cheek_feature_mean)
            state_dict["cheek_feature_scale"] = torch.ones_like(self.cheek_feature_scale)
        result = super().load_state_dict(state_dict, strict=strict, assign=assign)
        if not torch.isfinite(self.cheek_feature_mean).all() or \
                not torch.isfinite(self.cheek_feature_scale).all() or \
                not torch.all(self.cheek_feature_scale > 0):
            raise ValueError("Cheek feature normalization must be finite with positive scales")
        return result

    def fit_cheek_feature_normalization(self, features: torch.Tensor) -> None:
        if features.ndim != 2 or features.shape[1] != len(self.cheek_feature_mean) or \
                len(features) < 2 or not torch.isfinite(features).all():
            raise ValueError("Cheek normalization needs finite training features with the expected width")
        with torch.no_grad():
            self.cheek_feature_mean.copy_(features.mean(0).to(self.cheek_feature_mean.device))
            # Avoid amplifying a nearly constant feature or a sensor-noise-only
            # dimension while preserving meaningful variation in the capture.
            self.cheek_feature_scale.copy_(
                features.std(0, correction=0).clamp_min(.01).to(self.cheek_feature_scale.device),
            )

    def cheek_logits_from_features(self, features: torch.Tensor) -> torch.Tensor:
        normalized = (features - self.cheek_feature_mean) / self.cheek_feature_scale
        values = self.cheek_head[1](self.cheek_head[0](normalized))
        return self.cheek_head[2](values)

    def cheeks_from_features(self, features: torch.Tensor) -> torch.Tensor:
        return self.cheek_head[3](self.cheek_logits_from_features(features))

    def forward(self, cameras: torch.Tensor) -> torch.Tensor:
        with torch.no_grad():
            features = self.parent.stereo_features(cameras)
            tongue = self.parent.tongue_from_features(features)
        return torch.cat((tongue, self.cheeks_from_features(features)), dim=1)


def create_model(architecture: str, target_names: list[str]) -> nn.Module:
    if architecture.startswith(CHEEK_ARCHITECTURE_PREFIX):
        return FrozenTongueCheekModel(
            architecture[len(CHEEK_ARCHITECTURE_PREFIX):], target_names,
        )
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
    if not training:
        # A capture can be completed after skipping most cards. Let main()
        # report its explicit insufficient-samples error instead of NumPy's
        # opaque "need at least one array to concatenate" exception.
        empty = np.empty(0, dtype=np.int64)
        return empty, empty.copy()
    return np.concatenate(training), np.concatenate(validation)


def balanced_step_weights(step_ids: np.ndarray, indices: np.ndarray) -> np.ndarray:
    selected_steps = step_ids[indices]
    unique, counts = np.unique(selected_steps, return_counts=True)
    inverse = {int(step): 1.0 / float(count) for step, count in zip(unique, counts)}
    weights = np.asarray([inverse[int(step)] for step in selected_steps], dtype=np.float64)
    return weights / np.mean(weights)


def target_loss(
    prediction: torch.Tensor, target: torch.Tensor, target_names: list[str],
    focus: str = "balanced",
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
    if focus == "visibility":
        return 2.8 * visibility_loss + 0.5 * regression_loss
    if focus == "direction":
        return 0.8 * visibility_loss + 2.0 * regression_loss
    if focus != "balanced":
        raise ValueError(f"Unknown tongue training focus: {focus}")
    return 1.8 * visibility_loss + regression_loss


def run_training_epoch(
    model: nn.Module,
    loader: DataLoader,
    optimizer: torch.optim.Optimizer,
    scaler: torch.amp.GradScaler | None,
    device: torch.device,
    target_names: list[str],
    focus: str = "balanced",
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
            loss = target_loss(prediction, target, target_names, focus)
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


def heldout_pose_metrics(
    prediction: np.ndarray,
    target: np.ndarray,
    native: np.ndarray,
    step_ids: np.ndarray,
    target_names: list[str],
    camera_weight: float,
    threshold: float,
) -> dict[str, object]:
    """Audit prompt cards using the chosen global visibility gate.

    Pose-card targets are instructions, not verified physical tongue labels.
    These diagnostics expose weak poses in this capture; they cannot measure
    generalization to a different wearer.
    """
    if not (len(prediction) == len(target) == len(native) == len(step_ids)):
        raise ValueError("Held-out pose metrics require aligned frames and prompt IDs")
    visibility_index = target_names.index("visibility")
    horizontal_index = target_names.index("horizontal")
    vertical_index = target_names.index("vertical")
    expected_visible = target[:, visibility_index] >= 0.5
    fused_visibility = (
        camera_weight * prediction[:, visibility_index]
        + (1.0 - camera_weight) * native
    )
    missed_visible = expected_visible & (fused_visibility < threshold)
    false_visible = ~expected_visible & (fused_visibility >= threshold)
    direction_error = np.abs(
        prediction[:, [horizontal_index, vertical_index]]
        - target[:, [horizontal_index, vertical_index]]
    ).mean(axis=1)

    def summarize(mask: np.ndarray) -> dict[str, object]:
        visible = mask & expected_visible
        hidden = mask & ~expected_visible
        visible_count = int(np.count_nonzero(visible))
        hidden_count = int(np.count_nonzero(hidden))
        missed_count = int(np.count_nonzero(mask & missed_visible))
        false_count = int(np.count_nonzero(mask & false_visible))
        return {
            "samples": int(np.count_nonzero(mask)),
            "visibleSamples": visible_count,
            "missedVisible": missed_count,
            "visibilityFalseNegativeRate": (
                missed_count / visible_count if visible_count else None
            ),
            "hiddenSamples": hidden_count,
            "falseVisible": false_count,
            "visibilityFalsePositiveRate": (
                false_count / hidden_count if hidden_count else None
            ),
            "directionMae": (
                float(np.mean(direction_error[visible])) if visible_count else None
            ),
        }

    per_prompt = []
    for step_id in np.unique(step_ids):
        result = summarize(step_ids == step_id)
        result["promptId"] = int(step_id)
        per_prompt.append(result)

    horizontal = target[:, horizontal_index]
    vertical = target[:, vertical_index]
    diagonals = expected_visible & (np.abs(horizontal) >= 0.4) & (np.abs(vertical) >= 0.4)
    corners = []
    for name, horizontal_sign, vertical_sign in (
        ("upper-left", -1, 1),
        ("upper-right", 1, 1),
        ("lower-left", -1, -1),
        ("lower-right", 1, -1),
    ):
        mask = diagonals & (horizontal * horizontal_sign > 0) & (vertical * vertical_sign > 0)
        result = summarize(mask)
        result["corner"] = name
        corners.append(result)
    return {"perPrompt": per_prompt, "diagonalCorners": corners}


def evaluate(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
    target_names: list[str],
    step_ids: np.ndarray | None = None,
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
    visibility_index = target_names.index("visibility")
    expected_visible = target[:, visibility_index] >= 0.5
    # Match target_loss: hidden tongue detail is unsupervised and the live
    # tracker gates it out. Including those arbitrary outputs in checkpoint
    # selection can prefer worse visible poses merely for zeroing hidden ones.
    scored = np.broadcast_to(expected_visible[:, None], target.shape).copy()
    scored[:, visibility_index] = True
    active = (np.abs(target) > 0.10) & scored
    per_target = {}
    for index, name in enumerate(target_names):
        mask = active[:, index]
        samples = scored[:, index]
        per_target[name] = {
            "mae": float(np.mean(absolute[samples, index])) if np.any(samples) else None,
            "rawMae": float(np.mean(absolute[:, index])),
            "scoredSamples": int(np.count_nonzero(samples)),
            "activeMae": float(np.mean(absolute[mask, index])) if np.any(mask) else None,
            "activeSamples": int(np.count_nonzero(mask)),
        }
    camera_visibility = prediction[:, visibility_index]
    expected_visibility = target[:, visibility_index]
    thresholds = np.linspace(0.15, 0.85, 71)
    # Let held-out prompted poses decide whether native TongueOut helps at all.
    # Wireless camera arrival and Virtual Desktop labels can have different
    # delays, so camera-only must remain a valid choice.
    best_camera_weight, best_threshold, best_classification = choose_visibility_gate(
        camera_visibility, native, expected_visibility
    )
    metrics = {
        "mae": float(np.mean(absolute[scored])),
        "rawMae": float(np.mean(absolute)),
        "metricMask": "visibility on every frame; tongue details on expected-visible frames only",
        "visibleSamples": int(np.count_nonzero(expected_visible)),
        "scoredValues": int(np.count_nonzero(scored)),
        "activeMae": float(np.mean(absolute[active])) if np.any(active) else 0.0,
        "activeValues": int(np.count_nonzero(active)),
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
        "fusedVisibilityF1": best_classification["f1"],
        "fusedVisibilityPrecision": best_classification["precision"],
        "fusedVisibilityRecall": best_classification["recall"],
        "fusedVisibilityFalsePositiveRate": best_classification["falsePositiveRate"],
        "fusedVisibilityFalseNegativeRate": best_classification["falseNegativeRate"],
    }
    if step_ids is not None:
        metrics.update(heldout_pose_metrics(
            prediction, target, native, np.asarray(step_ids), target_names,
            best_camera_weight, best_threshold,
        ))
    return metrics


def checkpoint_score(metrics: dict[str, object], focus: str) -> tuple[float, str]:
    if focus in {"direction", "balanced"} and metrics.get("visibleSamples") == 0:
        raise ValueError("Direction/balanced checkpoint selection requires visible tongue validation samples")
    if focus == "direction":
        # A centered-only holdout still tests neutral-axis drift. An absent
        # active direction must not be scored as a perfectly predicted zero.
        direction_values = [
            (metrics["perTarget"][name]["activeMae"]
             if metrics["perTarget"][name]["activeMae"] is not None
             else metrics["perTarget"][name]["mae"])
            for name in ("horizontal", "vertical")
        ]
        if any(value is None for value in direction_values):
            raise ValueError("Direction checkpoint selection requires visible X/Y validation samples")
        direction_active_mae = float(np.mean(direction_values))
        return (
            0.25 * float(metrics["mae"])
            + 0.25 * float(metrics["activeMae"])
            + direction_active_mae
            + 0.15 * (1.0 - float(metrics["fusedVisibilityF1"])),
            "0.25*mae + 0.25*active_mae + mean(horizontal,vertical)_active_mae "
            "(visible_mae for axes without active samples) + 0.15*(1-visibility_f1)",
        )
    if focus == "visibility":
        return (
            0.60 * (1.0 - float(metrics["fusedVisibilityF1"]))
            + 0.75 * float(metrics["fusedVisibilityFalsePositiveRate"])
            + 0.25 * float(metrics["fusedVisibilityFalseNegativeRate"])
            + 0.10 * float(metrics["perTarget"]["visibility"]["mae"]),
            "0.60*(1-visibility_f1) + 0.75*false_positive_rate + "
            "0.25*false_negative_rate + 0.10*visibility_mae",
        )
    if focus == "balanced":
        return (
            float(metrics["mae"])
            + 0.50 * float(metrics["activeMae"])
            + 0.50 * (1.0 - float(metrics["fusedVisibilityF1"])),
            "mae + 0.50*active_mae + 0.50*(1-visibility_f1)",
        )
    raise ValueError(f"Unknown tongue checkpoint focus: {focus}")


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
        "--input-preprocessing", choices=MODES,
        help="inherit the initial checkpoint's input processing by default; new models use raw-v1",
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
    initial = None
    if arguments.initial_checkpoint:
        initial_path = Path(arguments.initial_checkpoint).resolve()
        initial = torch.load(initial_path, map_location="cpu", weights_only=False)
        if list(initial["targetNames"]) != target_names:
            raise ValueError("Initial checkpoint target schema does not match the dataset")
        if str(initial.get("architecture")) != arguments.architecture:
            raise ValueError("Initial checkpoint architecture does not match the requested model")
    inherited_preprocessing = resolve_input_preprocessing(initial or {})
    input_preprocessing = arguments.input_preprocessing or inherited_preprocessing
    print(f"TRAIN_INPUT_PREPROCESSING mode={input_preprocessing}", flush=True)
    step_ids = np.load(cache / "step_ids.npy", mmap_mode="r")
    trainable = np.load(cache / "trainable.npy", mmap_mode="r")
    dataset_type = str(metadata.get("datasetType", "prompted-video"))
    train_indices, validation_indices = blocked_train_validation_split(
        step_ids, trainable, dataset_type=dataset_type
    )
    if not len(train_indices) or not len(validation_indices):
        raise ValueError("Dataset does not contain enough repeated samples for a train/validation split")
    validation_step_ids = np.asarray(step_ids[validation_indices], dtype=np.int64)
    weights = balanced_step_weights(step_ids, train_indices)
    train_data = TongueFrames(cache, train_indices, augment=True,
                              input_preprocessing=input_preprocessing)
    validation_data = TongueFrames(cache, validation_indices, augment=False,
                                   input_preprocessing=input_preprocessing)
    sampler = WeightedRandomSampler(
        torch.as_tensor(weights, dtype=torch.double),
        num_samples=len(train_indices),
        replacement=True,
        generator=torch.Generator().manual_seed(seed),
    )
    selected_device = validated_torch_device_name(torch, arguments.device)
    device = torch.device(selected_device)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("GPU acceleration was requested but PyTorch cannot access the selected GPU")
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
    if initial is not None:
        model.load_state_dict(initial["modelState"])
        print(f"Refining from: {initial_path}")
    output = Path(arguments.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    best_score = float("inf")
    parent_name, parent_sha256 = parent_checkpoint_metadata(arguments.initial_checkpoint)

    def save_checkpoint(metrics: dict[str, object], score_description: str, epoch: int) -> None:
        # Save a CPU copy so users can load the personal model on another PC.
        scripted = torch.jit.script(model.eval().cpu())
        scripted.save(str(output.with_suffix(".torchscript.pt")))
        torch.save(
            {
                "version": 2,
                "architecture": arguments.architecture,
                "modelState": model.state_dict(),
                "targetNames": target_names,
                "imageSize": metadata["imageSize"],
                "inputPreprocessing": input_preprocessing,
                "validation": metrics,
                "split": (
                    "last 20 percent of manual repetitions within each prompt"
                    if dataset_type == "manual-stereo-stills"
                    else "every fifth 36-frame temporal block within each trainable prompt"
                ),
                "sampling": "inverse prompt-frequency balanced",
                "checkpointScore": score_description,
                "checkpointFocus": arguments.checkpoint_focus,
                "checkpointEpoch": epoch,
                "parentCheckpoint": parent_name,
                "parentCheckpointSha256": parent_sha256,
                "visibilityGate": {
                    "formula": "w * camera_visibility + (1-w) * native_TongueOut",
                    "cameraWeight": metrics["fusedVisibilityCameraWeight"],
                    "threshold": metrics["fusedVisibilityThreshold"],
                },
            },
            output,
        )
        model.to(device)

    if arguments.initial_checkpoint:
        # A short personal refinement can be worse than its starting model.
        # Keep the original weights when no epoch improves held-out poses.
        # An explicit processing change compares epochs under the new input
        # contract; developer promotion separately audits the original parent.
        baseline_metrics = evaluate(
            model, validation_loader, device, target_names, validation_step_ids
        )
        best_score, description = checkpoint_score(
            baseline_metrics, arguments.checkpoint_focus
        )
        save_checkpoint(baseline_metrics, description, 0)
        print(f"Initial checkpoint held-out score: {best_score:.4f}", flush=True)

    optimizer = torch.optim.AdamW(model.parameters(), lr=arguments.learning_rate, weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda") if device.type == "cuda" else None
    print(
        f"Device: {device}; balanced train draws: {len(train_indices)}; "
        f"held-out prompted frames: {len(validation_indices)}",
        flush=True,
    )
    for epoch in range(1, arguments.epochs + 1):
        loss = run_training_epoch(
            model, train_loader, optimizer, scaler, device, target_names,
            arguments.checkpoint_focus,
        )
        metrics = evaluate(
            model, validation_loader, device, target_names, validation_step_ids
        )
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
        score, score_description = checkpoint_score(metrics, arguments.checkpoint_focus)
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
            save_checkpoint(metrics, score_description, epoch)
            print(f"  Saved best checkpoint: {output}")
    checkpoint = torch.load(output, map_location="cpu", weights_only=False)
    metrics = checkpoint["validation"]
    print(
        f"Training complete: MAE={metrics['mae']:.4f}; active MAE={metrics['activeMae']:.4f}; "
        f"fused visibility F1={metrics['fusedVisibilityF1']:.3f} at "
        f"{metrics['fusedVisibilityThreshold']:.2f}"
    )
    visible_cards = [
        card for card in metrics["perPrompt"] if card["visibleSamples"]
    ]
    worst_cards = sorted(
        visible_cards,
        key=lambda card: (
            card["visibilityFalseNegativeRate"], card["directionMae"],
        ),
        reverse=True,
    )[:4]
    for card in worst_cards:
        print(
            f"HELDOUT_POSE card={card['promptId'] + 1} "
            f"visible={card['visibleSamples']} missed={card['missedVisible']} "
            f"fnr={card['visibilityFalseNegativeRate']:.3f} "
            f"direction_mae={card['directionMae']:.3f}",
            flush=True,
        )
    for corner in metrics["diagonalCorners"]:
        if corner["visibleSamples"]:
            print(
                f"HELDOUT_DIAGONAL corner={corner['corner']} "
                f"visible={corner['visibleSamples']} missed={corner['missedVisible']} "
                f"fnr={corner['visibilityFalseNegativeRate']:.3f} "
                f"direction_mae={corner['directionMae']:.3f}",
                flush=True,
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
