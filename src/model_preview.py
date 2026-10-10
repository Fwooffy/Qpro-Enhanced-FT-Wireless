#!/usr/bin/env python3
"""Live inference and teacher-comparison UI for a five-camera Qpro model."""

from __future__ import annotations

import math
import time
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
import torch

from train_model import QuestProTrackingModel
from qpro_gpu import validated_torch_device_name
from model_checkpoint import load_model_checkpoint


@dataclass
class Prediction:
    expressions: np.ndarray
    eye_orientations: np.ndarray
    inference_ms: float


class LiveModelPreview:
    def __init__(self, checkpoint_path: str | Path, device_name: str = "auto") -> None:
        checkpoint_path = Path(checkpoint_path).resolve()
        device_name = validated_torch_device_name(torch, device_name)
        self.device = torch.device(device_name)
        checkpoint = load_model_checkpoint(checkpoint_path, map_location=self.device)
        self.expression_names = list(checkpoint["expressionNames"])
        self.image_size = int(checkpoint["imageSize"])
        self.model = QuestProTrackingModel(len(self.expression_names)).to(self.device)
        self.model.load_state_dict(checkpoint["modelState"])
        self.model.eval()
        self.checkpoint_path = checkpoint_path

    def predict(self, strip: np.ndarray) -> Prediction:
        if strip.shape != (400, 2000):
            raise ValueError(f"Model requires a 400x2000 five-camera strip, got {strip.shape}")
        cameras = np.empty(
            (1, 5, self.image_size, self.image_size), dtype=np.float32
        )
        for camera in range(5):
            panel = strip[:, camera * 400:(camera + 1) * 400]
            cameras[0, camera] = cv2.resize(
                panel,
                (self.image_size, self.image_size),
                interpolation=cv2.INTER_AREA,
            ).astype(np.float32) / 255.0
        inputs = torch.from_numpy(cameras).to(self.device)
        if self.device.type == "cuda":
            torch.cuda.synchronize(self.device)
        started = time.perf_counter()
        with torch.inference_mode():
            expressions, orientations = self.model(inputs)
        if self.device.type == "cuda":
            torch.cuda.synchronize(self.device)
        elapsed_ms = (time.perf_counter() - started) * 1000
        return Prediction(
            expressions=expressions[0].float().cpu().numpy(),
            eye_orientations=orientations[0].float().cpu().numpy(),
            inference_ms=elapsed_ms,
        )

    def render(
        self,
        prediction: Prediction,
        teacher_sample: dict[str, object] | None,
        frame_monotonic_ns: int,
        labels_live: bool,
    ) -> np.ndarray:
        image = np.zeros((760, 1100, 3), dtype=np.uint8)
        teacher = (
            np.asarray(teacher_sample["values"], dtype=np.float32)
            if teacher_sample is not None else np.zeros_like(prediction.expressions)
        )
        alignment_ms = (
            abs(int(teacher_sample["arrivalMonotonicNs"]) - frame_monotonic_ns) / 1e6
            if teacher_sample is not None else math.inf
        )
        title = "Quest Pro pilot model vs factory teacher"
        cv2.putText(
            image, title, (25, 42), cv2.FONT_HERSHEY_SIMPLEX, 0.9,
            (240, 240, 240), 2, cv2.LINE_AA,
        )
        live_text = "LABELS LIVE" if labels_live else "LABELS FROZEN"
        cv2.putText(
            image, live_text, (875, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.55,
            (80, 255, 120) if labels_live else (80, 80, 255), 2, cv2.LINE_AA,
        )
        overall_mae = float(np.mean(np.abs(prediction.expressions - teacher)))
        active = teacher > 0.10
        active_mae = (
            float(np.mean(np.abs(prediction.expressions[active] - teacher[active])))
            if np.any(active) else 0.0
        )
        eye_error = self._eye_angle_error(
            prediction.eye_orientations,
            np.asarray(
                (teacher_sample or {}).get("leftEyeOrientation", [0, 0, 0, 1])
                + (teacher_sample or {}).get("rightEyeOrientation", [0, 0, 0, 1]),
                dtype=np.float32,
            ),
        )
        metrics = (
            f"Inference {prediction.inference_ms:.2f} ms  |  label alignment "
            f"{alignment_ms:.1f} ms  |  MAE {overall_mae:.3f}  |  "
            f"active MAE {active_mae:.3f}  |  eye error {eye_error:.1f} deg"
        )
        cv2.putText(
            image, metrics, (25, 76), cv2.FONT_HERSHEY_SIMPLEX, 0.54,
            (180, 220, 255), 1, cv2.LINE_AA,
        )
        cv2.putText(
            image, "factory", (790, 108), cv2.FONT_HERSHEY_SIMPLEX, 0.50,
            (255, 200, 70), 2, cv2.LINE_AA,
        )
        cv2.putText(
            image, "model", (910, 108), cv2.FONT_HERSHEY_SIMPLEX, 0.50,
            (255, 90, 230), 2, cv2.LINE_AA,
        )
        ranking = np.argsort(np.maximum(teacher, prediction.expressions))[::-1][:13]
        bar_x = 285
        bar_width = 470
        for row, expression_index in enumerate(ranking):
            y = 142 + row * 44
            name = self.expression_names[int(expression_index)]
            expected = float(teacher[expression_index])
            predicted = float(prediction.expressions[expression_index])
            cv2.putText(
                image, name, (25, y + 10), cv2.FONT_HERSHEY_SIMPLEX, 0.49,
                (225, 225, 225), 1, cv2.LINE_AA,
            )
            cv2.rectangle(image, (bar_x, y - 8), (bar_x + bar_width, y + 19),
                          (45, 45, 45), 1)
            cv2.rectangle(
                image, (bar_x, y - 5),
                (bar_x + round(bar_width * expected), y + 5),
                (255, 200, 70), -1,
            )
            cv2.rectangle(
                image, (bar_x, y + 8),
                (bar_x + round(bar_width * predicted), y + 17),
                (255, 90, 230), -1,
            )
            cv2.putText(
                image, f"{expected:5.2f}", (790, y + 10),
                cv2.FONT_HERSHEY_SIMPLEX, 0.48, (255, 200, 70), 1, cv2.LINE_AA,
            )
            cv2.putText(
                image, f"{predicted:5.2f}", (910, y + 10),
                cv2.FONT_HERSHEY_SIMPLEX, 0.48, (255, 90, 230), 1, cv2.LINE_AA,
            )
        cv2.putText(
            image,
            "Move naturally through gaze, blinks, brows, jaw, smiles, vowels, and speech. Q quits.",
            (25, 735), cv2.FONT_HERSHEY_SIMPLEX, 0.52,
            (170, 170, 170), 1, cv2.LINE_AA,
        )
        return image

    @staticmethod
    def _eye_angle_error(predicted: np.ndarray, teacher: np.ndarray) -> float:
        errors = []
        for offset in (0, 4):
            left = predicted[offset:offset + 4]
            right = teacher[offset:offset + 4]
            left = left / max(float(np.linalg.norm(left)), 1e-8)
            right = right / max(float(np.linalg.norm(right)), 1e-8)
            dot = float(np.clip(abs(np.dot(left, right)), 0, 1))
            errors.append(math.degrees(2 * math.acos(dot)))
        return float(np.mean(errors))
