"""Keep runtime layer folding numerically equivalent to trained architectures."""

import copy
import unittest

import torch

from tongue_calibration import TONGUE_TARGET_NAMES
from tongue_model_preview import _prepare_inference_model
from train_tongue_model import create_model


class TongueInferenceOptimizationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.previous_threads = torch.get_num_threads()
        torch.set_num_threads(2)

    @classmethod
    def tearDownClass(cls):
        torch.set_num_threads(cls.previous_threads)

    def test_all_checkpoint_architectures_preserve_eval_predictions(self):
        for base in ("legacy-late-fusion-v1", "spatial-stereo-resnet-v2"):
            for cheeks in (False, True):
                architecture = "cheek-augmented-" + base if cheeks else base
                names = list(TONGUE_TARGET_NAMES)
                if cheeks:
                    names += ["cheekPuffLeft", "cheekPuffRight"]
                with self.subTest(architecture=architecture):
                    torch.manual_seed(71)
                    original = create_model(architecture, names).eval()
                    with torch.no_grad():
                        # Nontrivial trained-style running statistics exercise
                        # scale, offset and epsilon folding through residuals.
                        for layer in original.modules():
                            if isinstance(layer, torch.nn.BatchNorm2d):
                                layer.running_mean.uniform_(-0.3, 0.3)
                                layer.running_var.uniform_(0.3, 1.8)
                                layer.weight.uniform_(0.4, 1.6)
                                layer.bias.uniform_(-0.2, 0.2)
                    checkpoint = {name: value.clone() for name, value in original.state_dict().items()}
                    optimized = _prepare_inference_model(copy.deepcopy(original), torch.device("cpu"))
                    self.assertFalse(any(isinstance(layer, torch.nn.BatchNorm2d)
                                         for layer in optimized.modules()))
                    self.assertTrue(any(isinstance(layer, torch.nn.BatchNorm2d)
                                        for layer in original.modules()))
                    for size in (32, 48):
                        inputs = torch.cat((torch.zeros(1, 2, size, size),
                                            torch.ones(1, 2, size, size),
                                            torch.rand(1, 2, size, size)))
                        with torch.inference_mode():
                            expected = original(inputs)
                            actual = optimized(inputs)
                        torch.testing.assert_close(actual, expected, atol=2e-6, rtol=2e-5)
                    for name, value in original.state_dict().items():
                        torch.testing.assert_close(value, checkpoint[name], rtol=0, atol=0)

    def test_input_dependent_normalization_is_not_folded(self):
        original = torch.nn.Sequential(
            torch.nn.Conv2d(2, 3, 3, padding=1, bias=True),
            torch.nn.BatchNorm2d(3, track_running_stats=False),
            torch.nn.SiLU(),
        ).eval()
        optimized = _prepare_inference_model(copy.deepcopy(original), torch.device("cpu"))
        self.assertIsInstance(optimized[1], torch.nn.BatchNorm2d)
        inputs = torch.rand(2, 2, 16, 16)
        with torch.inference_mode():
            torch.testing.assert_close(optimized(inputs), original(inputs))


if __name__ == "__main__":
    unittest.main()
