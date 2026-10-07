"""Choose a supported discrete GPU for Qpro's PyTorch workloads.

ROCm exposes its devices through PyTorch's ``cuda`` API.  On a hybrid PC,
device zero may be a Ryzen integrated GPU, so availability alone is not enough.
The older Radeon allowlist matches the ROCm 7.2.1 Windows PyTorch matrix.
Mapped RX 6000, 7000, and 9000 cards can use AMD's stable ROCm 10.1
packages with a device package selected for the card's gfx target. This Qpro
integration remains experimental until its GPU checks pass on each PC.
"""

from __future__ import annotations

import re
import json
import os
import sys
from pathlib import Path

# Keep previous verified environments usable after a release update. A new
# upstream version is deliberately not admitted just because its major matches.
_ROCM_10_TORCH_BUILDS = {
    "2.13.0+rocm10.0.0": "10.0.0",
    "2.14.0+rocm10.1.0": "10.1.0",
}
_ROCM_10_RELEASES = frozenset(_ROCM_10_TORCH_BUILDS.values())

_SUPPORTED_RADEON_721 = frozenset(
    name.casefold()
    for name in (
        "Radeon RX 9070 XT",
        "Radeon RX 9070",
        "Radeon AI PRO R9700",
        "Radeon RX 9060 XT",
        "Radeon RX 7900 XTX",
        "Radeon PRO W7900",
        "Radeon PRO W7900 Dual Slot",
        "Radeon RX 7700",
    )
)

# TheRock publishes Windows device packages for these gfx targets. This is a
# Qpro experiment: readiness still depends on training and inference checks.
_EXPERIMENTAL_RADEON_TARGETS = {
    name.casefold(): gfx
    for gfx, names in {
        "gfx1030": ("Radeon RX 6950 XT", "Radeon RX 6900 XT", "Radeon RX 6800 XT", "Radeon RX 6800"),
        "gfx1031": ("Radeon RX 6750 XT", "Radeon RX 6700 XT", "Radeon RX 6700"),
        "gfx1032": ("Radeon RX 6650 XT", "Radeon RX 6600 XT", "Radeon RX 6600"),
        "gfx1100": (
            "Radeon RX 7900 XTX", "Radeon RX 7900 XT", "Radeon RX 7900 GRE",
            "Radeon PRO W7900", "Radeon PRO W7900 Dual Slot",
        ),
        "gfx1101": ("Radeon RX 7800 XT", "Radeon RX 7700 XT", "Radeon RX 7700"),
        "gfx1102": ("Radeon RX 7600 XT", "Radeon RX 7600"),
        "gfx1201": (
            "Radeon RX 9070 XT", "Radeon RX 9070", "Radeon RX 9070 GRE",
            "Radeon AI PRO R9700",
        ),
        "gfx1200": ("Radeon RX 9060 XT", "Radeon RX 9060"),
    }.items()
    for name in names
}


def _normalized_gpu_name(name: str) -> str:
    normalized = re.sub(r"\((?:TM|R)\)", " ", str(name), flags=re.IGNORECASE)
    normalized = re.sub(r"[™®]", " ", normalized)
    normalized = re.sub(r"\s+", " ", normalized).strip()
    normalized = re.sub(r"^AMD\s+", "", normalized, flags=re.IGNORECASE)
    # Driver names vary in spacing (RX6700XT, RX 6700XT, RX 6700 XT).
    # Only canonicalize a complete desktop model: suffixes such as Mobile,
    # 6700M or an unknown series remain outside the exact discrete allowlist.
    model = re.fullmatch(r"(?:Radeon\s*)?RX\s*(\d{4})\s*(XTX|XT|GRE)?", normalized, re.IGNORECASE)
    if model:
        normalized = f"Radeon RX {model[1]}" + (f" {model[2]}" if model[2] else "")
    return normalized.casefold()


def experimental_rocm_target_for_gpu_name(name: str) -> str | None:
    """Return the ROCm 10 device package target for a mapped Radeon card."""
    return _EXPERIMENTAL_RADEON_TARGETS.get(_normalized_gpu_name(name))


def _expected_experimental_target(rocm_build: str | None = None) -> str | None:
    """Accept a verified marker, or an installer-only pre-marker smoke target."""
    marker = Path(sys.prefix) / "qpro-rocm-ready.json"
    try:
        if marker.is_file():
            record = json.loads(marker.read_text(encoding="utf-8-sig"))
            if not isinstance(record, dict):
                return None
            target = str(record.get("gfxTarget") or "").strip().lower()
            if (
                record.get("schema") == 1
                and record.get("supportTier") == "experimental-rocm-10"
                and str(record.get("rocmVersion") or "") in _ROCM_10_RELEASES
                and (rocm_build is None or record.get("rocmVersion") == rocm_build)
                and target in _EXPERIMENTAL_RADEON_TARGETS.values()
            ):
                return target
            return None
    except (OSError, ValueError, TypeError):
        return None
    if os.environ.get("QPRO_ROCM_INSTALL_SMOKE_TEST") == "1":
        requested = os.environ.get("QPRO_ROCM_EXPECTED_GFX_TARGET", "").strip().lower()
        if requested in _EXPERIMENTAL_RADEON_TARGETS.values():
            return requested
    return None


def is_supported_rocm_gpu_name(name: str, hip_version: str | None = "7.2.1") -> bool:
    """Check the discrete GPU against the allowlist for its ROCm build."""
    normalized = _normalized_gpu_name(name)
    version = str(hip_version or "")
    if version.startswith("7.2.1"):
        return normalized in _SUPPORTED_RADEON_721
    if version in _ROCM_10_RELEASES:
        return normalized in _EXPERIMENTAL_RADEON_TARGETS
    return False


def is_rocm_721_torch_build(torch_module: object) -> bool:
    """Match the pinned Windows wheel and its HIP 7.2 runtime build."""
    return (
        str(getattr(torch_module, "__version__", "")) == "2.9.1+rocm7.2.1"
        and str(getattr(torch_module.version, "hip", "") or "").startswith("7.2.")
    )


def is_rocm_10_torch_build(torch_module: object) -> bool:
    """Match Qpro's current or previous pinned wheel independently of HIP.

    ROCm 10.0.0 ships HIP 7.15.26333. New wheels expose the SDK release as
    ``torch.version.rocm``; the exact wheel suffix identifies it when that
    metadata is absent. Explicit conflicting metadata is never accepted.
    """
    release = _ROCM_10_TORCH_BUILDS.get(str(getattr(torch_module, "__version__", "")))
    if release is None or not getattr(torch_module.version, "hip", None):
        return False
    rocm = getattr(torch_module.version, "rocm", None)
    return rocm is None or str(rocm) == release


def _rocm_build_version(torch_module: object) -> str:
    """Identify the ROCm wheel release; HIP can report its own build number."""
    hip = str(getattr(torch_module.version, "hip", "") or "")
    if is_rocm_10_torch_build(torch_module):
        return _ROCM_10_TORCH_BUILDS[str(torch_module.__version__)]
    if "+rocm10." in str(getattr(torch_module, "__version__", "")):
        return ""
    rocm = getattr(torch_module.version, "rocm", None)
    if rocm is not None:
        # A different ROCm 10 wheel is not a build installed and verified
        # by Qpro. Keep future/unknown releases outside the device allowlist.
        return "" if str(rocm).startswith("10.") else str(rocm)
    if is_rocm_721_torch_build(torch_module):
        return "7.2.1"
    if hip.startswith("10."):
        return ""
    return hip


def _rocm_device_count(torch_module: object) -> int:
    """Use HIP's device order, including if Torch's discovery helper fails."""
    # PyTorch's public count can use offload-arch/AMD SMI before initialization.
    # Its native HIP count is the same one used by tensor allocation and avoids
    # assuming that a Windows display-adapter index is a Torch device index.
    native_count = getattr(getattr(torch_module, "_C", None), "_cuda_getDeviceCount", None)
    if callable(native_count):
        try:
            return max(0, int(native_count()))
        except Exception:
            pass
    return max(0, int(torch_module.cuda.device_count()))


def _supported_rocm_device_at(torch_module: object, index: int, rocm_build: str, expected_target: str | None) -> bool:
    try:
        name = torch_module.cuda.get_device_name(index)
        model_target = experimental_rocm_target_for_gpu_name(name)
        if not is_supported_rocm_gpu_name(name, rocm_build) or (
            expected_target is not None and model_target != expected_target
        ):
            return False
        properties = getattr(torch_module.cuda, "get_device_properties", None)
        if callable(properties):
            # A driver may report a familiar name with a different actual gfx
            # architecture. Never use it with a package prepared for another
            # target. Older wheels without this metadata still use the exact
            # model allowlist and the installer's training/inference checks.
            arch = str(getattr(properties(index), "gcnArchName", "") or "").strip().lower()
            if arch and arch.split(":", 1)[0] != model_target:
                return False
        return True
    except Exception:
        return False


def rocm_device_diagnostics(torch_module: object) -> str:
    """Describe the adapters Torch can actually query and any rejection."""
    build = _rocm_build_version(torch_module)
    experimental = build in _ROCM_10_RELEASES
    expected_target = _expected_experimental_target(build) if experimental else None
    details = []
    try:
        count = _rocm_device_count(torch_module)
    except Exception as exc:
        count = 1  # Device zero may still be queryable after a helper failure.
        message = (str(exc).splitlines() or [type(exc).__name__])[0]
        details.append(f"device enumeration failed: {message}")
    for index in range(max(1, count)):
        try:
            name = str(torch_module.cuda.get_device_name(index))
            model_target = experimental_rocm_target_for_gpu_name(name)
            arch = ""
            properties = getattr(torch_module.cuda, "get_device_properties", None)
            if callable(properties):
                arch = str(getattr(properties(index), "gcnArchName", "") or "").strip().lower()
            if not is_supported_rocm_gpu_name(name, build):
                reason = "integrated or unmapped GPU for this ROCm build"
            elif experimental and expected_target is None:
                reason = "no valid Qpro readiness record"
            elif expected_target is not None and model_target != expected_target:
                reason = f"requires {model_target}; environment prepared for {expected_target}"
            elif arch and arch.split(":", 1)[0] != model_target:
                reason = f"reported architecture differs from model target {model_target}"
            else:
                reason = "eligible discrete GPU"
            details.append(f"cuda:{index} {name}" + (f" ({arch})" if arch else "") + f": {reason}")
        except Exception as exc:
            message = (str(exc).splitlines() or [type(exc).__name__])[0]
            details.append(f"cuda:{index} query failed: {message}")
    return "; ".join(details)


def supported_rocm_device_name(torch_module: object) -> str | None:
    """Find a supported Radeon device, including when an iGPU is device zero."""
    if not getattr(torch_module.version, "hip", None):
        return None
    try:
        if not torch_module.cuda.is_available():
            return None
        rocm_build = _rocm_build_version(torch_module)
        experimental = rocm_build in _ROCM_10_RELEASES
        expected_target = _expected_experimental_target(rocm_build) if experimental else None
        if experimental and expected_target is None:
            return None

        # Some relocated Windows ROCm environments can still query and use
        # device zero, while their offload-arch launcher breaks device_count().
        # A successful name query identifies the actual Torch/HIP device and
        # still excludes integrated graphics through the discrete allowlist.
        if _supported_rocm_device_at(torch_module, 0, rocm_build, expected_target):
            return "cuda:0"
        for index in range(1, _rocm_device_count(torch_module)):
            if _supported_rocm_device_at(torch_module, index, rocm_build, expected_target):
                return f"cuda:{index}"
    except Exception:
        return None
    return None


def preferred_torch_device_name(torch_module: object) -> str:
    """Prefer supported discrete acceleration, otherwise use CPU."""
    if getattr(torch_module.version, "hip", None):
        return supported_rocm_device_name(torch_module) or "cpu"
    try:
        if getattr(torch_module.version, "cuda", None) and torch_module.cuda.is_available():
            # NVIDIA CUDA does not expose AMD or Intel integrated graphics.
            return "cuda:0"
    except Exception:
        pass
    return "cpu"


def require_rocm_device_name(torch_module: object) -> str:
    """Fail before work starts if ROCm cannot see a supported discrete GPU."""
    device = supported_rocm_device_name(torch_module)
    if device is None:
        rocm_build = _rocm_build_version(torch_module)
        experimental = rocm_build in _ROCM_10_RELEASES
        expected_target = _expected_experimental_target(rocm_build) if experimental else None
        if experimental and expected_target is None:
            raise RuntimeError(
                "ROCm 10 has no valid Qpro readiness record for a discrete GPU "
                "target. Run Install AMD ROCm again before using this environment. "
                f"Torch adapters: {rocm_device_diagnostics(torch_module)}"
            )
        target_hint = f" The experimental environment was prepared for {expected_target}." if expected_target else ""
        visibility = [key for key in ("HIP_VISIBLE_DEVICES", "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL") if key in os.environ]
        visibility_hint = (
            f" GPU visibility settings are active ({', '.join(visibility)}); restart through the Hub so Qpro can detect the discrete GPU."
            if visibility else ""
        )
        raise RuntimeError(
            "ROCm cannot see a discrete Radeon supported by this Qpro ROCm build. "
            f"Ryzen integrated graphics are not supported.{target_hint} Check the AMD driver, "
            f"the selected ROCm build, or use the CPU runtime.{visibility_hint} "
            f"Torch adapters: {rocm_device_diagnostics(torch_module)}"
        )
    return device


def validated_torch_device_name(torch_module: object, requested: str) -> str:
    """Validate an explicit CUDA choice so it cannot target a ROCm iGPU."""
    if requested == "auto":
        return preferred_torch_device_name(torch_module)
    if not requested.startswith("cuda"):
        return requested
    if not torch_module.cuda.is_available():
        raise RuntimeError("GPU acceleration was requested, but PyTorch cannot access a GPU")
    if getattr(torch_module.version, "hip", None):
        index = int(requested.split(":", 1)[1]) if ":" in requested else 0
        if index < 0:
            raise RuntimeError(f"ROCm device {requested} is unavailable")
        try:
            # The indexed name query validates the selected device directly.
            # device_count() may fail through a stale offload-arch launcher even
            # when Torch can access this device.
            name = torch_module.cuda.get_device_name(index)
        except Exception as exc:
            raise RuntimeError(f"ROCm device {requested} is unavailable") from exc
        rocm_build = _rocm_build_version(torch_module)
        experimental = rocm_build in _ROCM_10_RELEASES
        expected_target = _expected_experimental_target(rocm_build) if experimental else None
        if experimental and expected_target is None:
            raise RuntimeError("ROCm 10 has no valid Qpro readiness record for a discrete GPU target")
        if not _supported_rocm_device_at(torch_module, index, rocm_build, expected_target):
            raise RuntimeError(
                f"ROCm device {requested} ({name}) is not a supported discrete "
                "Radeon GPU for the installed gfx target; choose the supported GPU or CPU"
            )
    return requested
