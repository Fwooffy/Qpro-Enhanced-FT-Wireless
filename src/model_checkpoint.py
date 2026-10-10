"""Load Qpro model weights without executing checkpoint Python objects."""

from __future__ import annotations

import pickle
import warnings
from os import PathLike
from typing import BinaryIO

import torch


class ModelCheckpointError(ValueError):
    """A checkpoint cannot be read as model weights and ordinary metadata."""


def load_model_checkpoint(
    source: str | PathLike[str] | BinaryIO,
    *,
    map_location: str | torch.device = "cpu",
) -> dict[str, object]:
    """Preserve tensor and metadata dictionaries using PyTorch's restricted reader.

    Imported checkpoints can contain pickle instructions. Never retry with
    unrestricted loading or allow custom globals when the restricted reader
    rejects them. Qpro exports contain state dictionaries and plain metadata.
    """
    try:
        with warnings.catch_warnings():
            # PyTorch warns about JIT dispatch before rejecting these archives.
            # The rejection below explains the result without implying execution.
            warnings.filterwarnings(
                "ignore", category=UserWarning,
                message="'torch.load' received a zip file that looks like a TorchScript archive",
            )
            checkpoint = torch.load(
                source, map_location=map_location, weights_only=True,
            )
    except pickle.UnpicklingError:
        raise ModelCheckpointError(
            "This model could not be loaded safely. It contains unsupported Python "
            "objects or damaged checkpoint data. Qpro accepts model weights and "
            "plain metadata only; export the model again from a supported Qpro version."
        ) from None
    except EOFError:
        raise ModelCheckpointError(
            "This model checkpoint is incomplete or damaged. Export the model again "
            "from Qpro, then retry."
        ) from None
    except RuntimeError as error:
        detail = str(error)
        if "TorchScript archives" in detail:
            raise ModelCheckpointError(
                "This file is a TorchScript program, not a Qpro model checkpoint. "
                "Choose the exported .qptonguemodel package or its model weights instead."
            ) from None
        if any(message in detail for message in (
            "PytorchStreamReader", "Invalid magic number", "unexpected EOF",
        )):
            raise ModelCheckpointError(
                "This model checkpoint is incomplete or damaged. Export the model again "
                "from Qpro, then retry."
            ) from None
        raise
    if not isinstance(checkpoint, dict):
        raise ModelCheckpointError(
            "This file is not a Qpro model checkpoint. Its contents must be a "
            "dictionary of model weights and plain metadata."
        )
    return checkpoint
