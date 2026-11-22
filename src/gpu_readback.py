"""Read GPU results without keeping a CPU core busy while kernels finish."""

from __future__ import annotations

import math
import sys
import time
from typing import Callable


class GPUReadbackCancelled(RuntimeError):
    """A tracking worker stopped before its GPU result was available."""


def copy_to_cpu(
    tensor, *, cancelled: Callable[[], bool] | None = None,
    timeout_seconds: float = 5.0,
):
    """Return a completed CPU tensor with unchanged shape and precision.

    Poll this worker's stream event so a busy GPU doesn't occupy a CPU core.
    The Windows ROCm 7.2.1 wheel uses a normal CPU copy once the kernels
    finish. Its timeout bounds the wait for the kernels, and cancellation
    during the short final copy discards the result. Other GPU runtimes
    enqueue the copy before recording the event.
    """
    if tensor.device.type != "cuda":
        return tensor.cpu()
    if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise ValueError("GPU readback timeout must be finite and positive")
    if cancelled is not None and cancelled():
        raise GPUReadbackCancelled("Tracking stopped before GPU readback")

    import torch
    from qpro_gpu import is_rocm_721_torch_build

    target = None
    with torch.cuda.device(tensor.device):
        # A pinned async device-to-host transfer on the legacy Windows ROCm
        # wheel can keep Python alive after Stop, even after the worker exits.
        if sys.platform != "win32" or not is_rocm_721_torch_build(torch):
            target = torch.empty(tensor.shape, dtype=tensor.dtype, device="cpu", pin_memory=True)
            target.copy_(tensor, non_blocking=True)
        event = torch.cuda.Event()
        event.record(torch.cuda.current_stream(tensor.device))

    # PyTorch copy_kernel_cuda records the host allocator's transfer event.
    # Its caching pinned allocator cannot recycle this allocation before the
    # copy completes, including when cancellation/timeout releases our tensor.
    # Never expose or manually reuse a partially written staging buffer.
    deadline = time.perf_counter() + timeout_seconds
    while True:
        if cancelled is not None and cancelled():
            raise GPUReadbackCancelled("Tracking stopped during GPU readback")
        if event.query():
            if target is not None:
                return target
            result = tensor.cpu()
            if cancelled is not None and cancelled():
                raise GPUReadbackCancelled("Tracking stopped during GPU readback")
            return result
        remaining = deadline - time.perf_counter()
        if remaining <= 0:
            raise TimeoutError("GPU result did not finish within the readback timeout")
        time.sleep(min(0.001, remaining))
