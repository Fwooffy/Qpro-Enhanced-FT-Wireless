"""Optional discrete-GPU preprocessing for the relative pupil detector.

The image stages use the same median window and elliptical morphology as
OpenCV. Contours and the detector's quality checks still run on the CPU.
Importing this module does not import Torch or initialize a GPU.
"""

from __future__ import annotations

import cv2
import numpy as np

from gpu_readback import copy_to_cpu


class TorchPupilPreprocessor:
    """Batch both eyes on Qpro's validated CUDA or ROCm device."""

    def __init__(self, device: str = "auto") -> None:
        import torch
        from qpro_gpu import validated_torch_device_name

        selected = validated_torch_device_name(torch, device)
        if not selected.startswith("cuda"):
            raise RuntimeError("No validated discrete GPU is available for pupil processing")
        self.torch = torch
        self.device = selected
        self.backend = "amd-rocm" if torch.version.hip else "nvidia-cuda"
        index = int(selected.split(":", 1)[1]) if ":" in selected else 0
        self.name = str(torch.cuda.get_device_name(index))
        self.readback_cancelled = None
        self._kernel_rows = {}
        for size in (3, 7):
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (size, size))
            self._kernel_rows[size] = tuple(int(row.sum()) for row in kernel)

    def prepare(self, rois: list[np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
        """Read back the median and morphology images together."""
        torch = self.torch
        # OpenCV medianBlur uses replicated borders. Keep uint8 levels exact
        # in float32 so threshold decisions do not depend on half precision.
        with torch.inference_mode():
            pixels = torch.as_tensor(np.stack(rois), device=self.device).to(dtype=torch.float32)
            padded = torch.nn.functional.pad(pixels[:, None], (3, 3, 3, 3), mode="replicate")
            windows = padded.unfold(2, 7, 1).unfold(3, 7, 1)
            clean = windows.flatten(-2).median(dim=-1).values
            # An inverse threshold turns a grayscale minimum into a binary
            # maximum and vice versa. Do each morphology pass once per eye.
            morphed = self._morph(clean, 7, erode=True)
            morphed = self._morph(morphed, 7, erode=False)
            morphed = self._morph(morphed, 3, erode=False)
            morphed = self._morph(morphed, 3, erode=True)
            images = copy_to_cpu(
                torch.cat((clean, morphed), dim=1).to(dtype=torch.uint8),
                cancelled=self.readback_cancelled,
            ).numpy()
            return images[:, 0], images[:, 1]

    def _morph(self, image: object, size: int, *, erode: bool) -> object:
        torch = self.torch
        rows = self._kernel_rows[size]
        # OpenCV's default morphology border is neutral: outside pixels are
        # ignored for either minimum or maximum. For erosion, negate the
        # image so max pooling computes the negated minimum.
        source = -image if erode else image
        horizontal = {}
        for width in set(rows):
            filtered = source if width == 1 else torch.nn.functional.max_pool2d(
                source, (1, width), stride=1, padding=(0, width // 2),
            )
            horizontal[width] = torch.nn.functional.pad(
                filtered, (0, 0, size // 2, size // 2), value=float("-inf"),
            )
        height = source.shape[-2]
        result = horizontal[rows[0]][:, :, :height]
        for offset, width in enumerate(rows[1:], 1):
            result = torch.maximum(result, horizontal[width][:, :, offset:offset + height])
        return -result if erode else result
