"""Read GPU results without keeping a CPU core busy while kernels finish."""

from __future__ import annotations

import math
import time
from typing import Callable


class GPUReadbackCancelled(RuntimeError):
    """A tracking worker stopped before its GPU result was available."""


def copy_to_cpu(
    tensor, *, cancelled: Callable[[], bool] | None = None,
    timeout_seconds: float = 5.0,
):
    """Return a completed CPU tensor with unchanged shape and precision.

    Each GPU transfer owns its pinned buffer. The event is recorded after
    the transfer on this worker's current stream, so unrelated device work
    does not need a global synchronization. CPU inference keeps its existing
    direct path and importing this helper does not initialize Torch.
    """
    if tensor.device.type != "cuda":
        return tensor.cpu()
    if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise ValueError("GPU readback timeout must be finite and positive")
    if cancelled is not None and cancelled():
        raise GPUReadbackCancelled("Tracking stopped before GPU readback")

    import torch

    with torch.cuda.device(tensor.device):
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
            return target
        remaining = deadline - time.perf_counter()
        if remaining <= 0:
            raise TimeoutError("GPU result did not finish within the readback timeout")
        time.sleep(min(0.001, remaining))
