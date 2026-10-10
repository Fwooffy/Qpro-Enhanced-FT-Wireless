import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import numpy as np
import torch

from train_tongue_model import (
    SpatialStereoTongueModel,
    StereoTongueModel,
    TongueFrames,
    balanced_step_weights,
    blocked_train_validation_split,
    checkpoint_score,
    classification_at_threshold,
    evaluate,
    heldout_pose_metrics,
    shade_local_mouth_area,
    target_loss,
)
from tongue_image_processing import preprocess_stereo_images


class TongueTrainingTests(unittest.TestCase):
    @staticmethod
    def evaluate_predictions(prediction, target, names):
        loader = torch.utils.data.DataLoader(torch.utils.data.TensorDataset(
            prediction, target, target[:, names.index("visibility")],
        ), batch_size=len(target))
        return evaluate(torch.nn.Identity(), loader, torch.device("cpu"), names)

    def test_checkpoint_selection_ignores_unsupervised_hidden_tongue_details(self):
        names = ["visibility", "extension", "horizontal", "vertical", "curl_up",
                 "bend_down", "roll", "flat", "squish", "twist"]
        target = torch.zeros(10, len(names))
        target[:2, 0] = 1
        target[:2, 1] = .75
        target[0, 2] = .75
        target[1, 3] = .75
        correct = target.clone()
        correct[:, 0] = .02
        correct[:2, 0] = .98
        correct[2:, 1:] = .9
        worse = target.clone()
        worse[:, 0] = correct[:, 0]
        worse[0, 2] = worse[1, 3] = .65
        good_metrics = self.evaluate_predictions(correct, target, names)
        bad_metrics = self.evaluate_predictions(worse, target, names)
        self.assertGreater(good_metrics["rawMae"], bad_metrics["rawMae"])
        for focus in ("direction", "balanced"):
            self.assertLess(checkpoint_score(good_metrics, focus)[0],
                            checkpoint_score(bad_metrics, focus)[0])
        changed_hidden = correct.clone()
        changed_hidden[2:, 1:] = 0
        changed = self.evaluate_predictions(changed_hidden, target, names)
        self.assertEqual(good_metrics["mae"], changed["mae"])
        self.assertEqual(good_metrics["activeMae"], changed["activeMae"])
        self.assertEqual(good_metrics["perTarget"]["horizontal"]["scoredSamples"], 2)
        self.assertEqual(good_metrics["perTarget"]["visibility"]["scoredSamples"], 10)

    def test_hidden_only_validation_does_not_claim_perfect_direction(self):
        names = ["visibility", "extension", "horizontal", "vertical"]
        target = torch.zeros(4, len(names))
        metrics = self.evaluate_predictions(torch.full_like(target, .1), target, names)
        self.assertEqual(metrics["visibleSamples"], 0)
        self.assertIsNone(metrics["perTarget"]["horizontal"]["mae"])
        self.assertEqual(metrics["perTarget"]["horizontal"]["scoredSamples"], 0)
        for focus in ("direction", "balanced"):
            with self.assertRaisesRegex(ValueError, "visible tongue validation"):
                checkpoint_score(metrics, focus)
        self.assertTrue(np.isfinite(checkpoint_score(metrics, "visibility")[0]))

    def test_centered_only_validation_scores_visible_direction_drift(self):
        names = ["visibility", "extension", "horizontal", "vertical"]
        target = torch.tensor([[1., .5, 0., 0.], [0., 0., 0., 0.]])
        prediction = target.clone()
        prediction[0, 2] = .4
        metrics = self.evaluate_predictions(prediction, target, names)
        centered = self.evaluate_predictions(target, target, names)
        self.assertIsNone(metrics["perTarget"]["horizontal"]["activeMae"])
        self.assertAlmostEqual(metrics["perTarget"]["horizontal"]["mae"], .4)
        self.assertGreater(checkpoint_score(metrics, "direction")[0],
                           checkpoint_score(centered, "direction")[0] + .19)

    @staticmethod
    def make_preprocessing_cache(root):
        pixels = np.tile(np.arange(128, dtype=np.uint8), (128, 1))
        images = np.repeat(np.stack([pixels, pixels.T])[None], 12, axis=0)
        np.save(root / "images.npy", images)
        np.save(root / "targets.npy", np.zeros((12, 4), np.float32))
        np.save(root / "native_tongue_out.npy", np.zeros(12, np.float32))
        np.save(root / "step_ids.npy", np.repeat(np.arange(2), 6))
        np.save(root / "trainable.npy", np.ones(12, bool))
        (root / "metadata.json").write_text(json.dumps({
            "targetNames": ["visibility", "horizontal", "vertical", "extension"],
            "datasetType": "manual-stereo-stills", "imageSize": 128,
            "nativeTongueMapping": "source-flags-v2",
        }), encoding="utf-8")
        return images

    def test_dataset_processing_matches_inference_without_changing_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            original = self.make_preprocessing_cache(cache)
            for mode in ("raw-v1", "clahe-v1"):
                dataset = TongueFrames(cache, np.asarray([0]), False, mode)
                actual, _, _ = dataset[0]
                expected = torch.from_numpy(
                    preprocess_stereo_images(original[0], mode).copy()
                ).float().div_(255)
                self.assertTrue(torch.equal(actual, expected))
                for array in (dataset.images, dataset.targets, dataset.native):
                    array._mmap.close()
            np.testing.assert_array_equal(np.load(cache / "images.npy"), original)

    def test_old_native_mapping_cache_fails_before_training_or_output(self):
        import train_tongue_model
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            original = self.make_preprocessing_cache(cache)
            metadata_path = cache / "metadata.json"
            metadata = json.loads(metadata_path.read_text())
            del metadata["nativeTongueMapping"]
            metadata_path.write_text(json.dumps(metadata))
            output = cache / "candidate.pt"
            with (mock.patch.object(sys, "argv", ["train", str(cache), "--output", str(output)]),
                  mock.patch.object(train_tongue_model, "create_model") as create):
                with self.assertRaisesRegex(ValueError, "Regenerate the cache"):
                    train_tongue_model.main()
            create.assert_not_called()
            self.assertFalse(output.exists())
            np.testing.assert_array_equal(np.load(cache / "images.npy"), original)

    def test_refinement_inherits_processing_and_persists_explicit_override(self):
        import train_tongue_model
        for parent_mode, override, expected in (
            (None, None, "raw-v1"),
            ("clahe-v1", None, "clahe-v1"),
            (None, "clahe-v1", "clahe-v1"),
        ):
            with self.subTest(parent=parent_mode, override=override), \
                    tempfile.TemporaryDirectory() as directory:
                cache = Path(directory)
                self.make_preprocessing_cache(cache)
                model = torch.nn.Linear(1, 1)
                parent = {
                    "targetNames": ["visibility", "horizontal", "vertical", "extension"],
                    "architecture": "legacy-late-fusion-v1", "imageSize": 128,
                    "modelState": model.state_dict(),
                }
                if parent_mode is not None:
                    parent["inputPreprocessing"] = parent_mode
                parent_path = cache / "parent.pt"
                output = cache / "candidate.pt"
                torch.save(parent, parent_path)
                metrics = {
                    "mae": 0.1, "activeMae": 0.1, "fusedVisibilityF1": 0.9,
                    "fusedVisibilityFalsePositiveRate": 0.0,
                    "fusedVisibilityFalseNegativeRate": 0.1,
                    "fusedVisibilityCameraWeight": 0.8, "fusedVisibilityThreshold": 0.5,
                    "perTarget": {name: {"activeMae": 0.1} for name in parent["targetNames"]},
                    "perPrompt": [], "diagonalCorners": [],
                }
                modes_seen = []
                datasets_seen = []

                def dataset(*args, **kwargs):
                    value = TongueFrames(*args, **kwargs)
                    datasets_seen.append(value)
                    return value

                def evaluation(model, loader, *args):
                    modes_seen.append(loader.dataset.input_preprocessing)
                    return metrics

                command = [
                    "train_tongue_model.py", str(cache), "--epochs", "1", "--device", "cpu",
                    "--architecture", "legacy-late-fusion-v1",
                    "--initial-checkpoint", str(parent_path), "--output", str(output),
                ]
                if override is not None:
                    command.extend(["--input-preprocessing", override])
                with mock.patch.object(sys, "argv", command), \
                        mock.patch.object(train_tongue_model, "create_model", return_value=model), \
                        mock.patch.object(train_tongue_model, "TongueFrames", side_effect=dataset), \
                        mock.patch.object(train_tongue_model, "evaluate", side_effect=evaluation), \
                        mock.patch.object(train_tongue_model, "run_training_epoch", return_value=0.0), \
                        mock.patch.object(train_tongue_model, "checkpoint_score", side_effect=[(0.1, "initial"), (0.2, "epoch")]), \
                        mock.patch.object(train_tongue_model.torch.jit, "script"), \
                        mock.patch("builtins.print"):
                    self.assertEqual(train_tongue_model.main(), 0)
                candidate = torch.load(output, weights_only=True)
                self.assertEqual(candidate["inputPreprocessing"], expected)
                self.assertEqual(candidate["checkpointEpoch"], 0)
                self.assertEqual(modes_seen, [expected, expected])
                self.assertEqual(candidate["parentCheckpoint"], "parent.pt")
                self.assertEqual(candidate["imageSize"], 128)
                for value in datasets_seen:
                    for array in (value.images, value.targets, value.native):
                        array._mmap.close()

    def test_split_reports_no_trainable_card_without_numpy_concatenate_error(self):
        steps = np.asarray([0, 0, 0, 1, 1, 1])
        trainable = np.asarray([True, True, True, False, False, False])
        training, validation = blocked_train_validation_split(
            steps, trainable, dataset_type="manual-stereo-stills"
        )
        self.assertEqual(training.dtype, np.int64)
        self.assertEqual(validation.dtype, np.int64)
        self.assertEqual(len(training), 0)
        self.assertEqual(len(validation), 0)

    def test_local_mouth_shading_preserves_stereo_geometry_and_evidence(self):
        images = torch.ones(2, 64, 64)
        result = shade_local_mouth_area(images, 18, 17, 22, 24, 0.28)
        self.assertEqual(tuple(result.shape), (2, 64, 64))
        self.assertTrue(torch.equal(result[0], result[1]))
        self.assertEqual(float(result[:, 0, 0].min()), 1.0)
        self.assertLess(float(result[:, 28, 28].max()), 1.0)
        self.assertGreaterEqual(float(result.min()), 0.72)

    def test_split_excludes_untrainable_frames(self):
        steps = np.repeat(np.arange(3), 100)
        trainable = np.ones(len(steps), dtype=bool)
        trainable[200:] = False
        training, validation = blocked_train_validation_split(steps, trainable, 20)
        self.assertTrue(np.all(training < 200))
        self.assertTrue(np.all(validation < 200))
        self.assertFalse(set(training) & set(validation))

    def test_balancing_equalizes_prompt_mass(self):
        steps = np.asarray([0] * 10 + [1] * 100)
        indices = np.arange(len(steps))
        weights = balanced_step_weights(steps, indices)
        self.assertAlmostEqual(float(np.sum(weights[:10])), float(np.sum(weights[10:])))

    def test_model_bounds_signed_and_unsigned_outputs(self):
        names = [
            "visibility", "extension", "horizontal", "vertical", "curl_up",
            "bend_down", "roll", "flat", "squish", "twist",
        ]
        model = StereoTongueModel(names).eval()
        output = model(torch.zeros(2, 2, 160, 160))
        self.assertEqual(tuple(output.shape), (2, 10))
        self.assertTrue(torch.all(output[:, :2] >= 0))
        self.assertTrue(torch.all(output[:, :2] <= 1))
        self.assertTrue(torch.all(output[:, 2:4] >= -1))
        self.assertTrue(torch.all(output[:, 2:4] <= 1))

    def test_spatial_stereo_model_preserves_output_contract(self):
        names = [
            "visibility", "extension", "horizontal", "vertical", "curl_up",
            "bend_down", "roll", "flat", "squish", "twist",
        ]
        model = SpatialStereoTongueModel(names).eval()
        output = model(torch.zeros(1, 2, 224, 224))
        self.assertEqual(tuple(output.shape), (1, 10))
        self.assertTrue(torch.all(output[:, [0, 1, 4, 5, 6, 7, 8]] >= 0))
        self.assertTrue(torch.all(output[:, [0, 1, 4, 5, 6, 7, 8]] <= 1))

    def test_manual_still_split_holds_out_repetitions_per_card(self):
        steps = np.repeat(np.arange(3), 6)
        trainable = np.ones(len(steps), dtype=bool)
        training, validation = blocked_train_validation_split(
            steps, trainable, dataset_type="manual-stereo-stills"
        )
        self.assertEqual(len(training), 15)
        self.assertEqual(len(validation), 3)
        self.assertFalse(set(training) & set(validation))

    def test_visibility_loss_stays_finite_at_float16_probability_limits(self):
        names = [
            "visibility", "extension", "horizontal", "vertical", "curl_up",
            "bend_down", "roll", "flat", "squish", "twist",
        ]
        prediction = torch.zeros(2, 10, dtype=torch.float16)
        prediction[0, 0] = 1.0
        prediction[1, 0] = 0.0
        target = torch.zeros(2, 10, dtype=torch.float16)
        target[0, 0] = 1.0
        self.assertTrue(torch.isfinite(target_loss(prediction, target, names)))

    def test_visibility_and_direction_checkpoints_optimize_different_errors(self):
        names = ["visibility", "extension", "horizontal", "vertical"]
        target = torch.tensor([[1.0, 1.0, 0.8, 0.0]])
        missed_visibility = torch.tensor([[0.1, 1.0, 0.8, 0.0]])
        missed_direction = torch.tensor([[1.0, 0.2, 0.0, 0.0]])
        self.assertGreater(
            target_loss(missed_visibility, target, names, "visibility"),
            target_loss(missed_visibility, target, names, "direction"),
        )
        self.assertGreater(
            target_loss(missed_direction, target, names, "direction"),
            target_loss(missed_direction, target, names, "visibility"),
        )

    def test_classification_metrics_expose_false_positive_rate(self):
        values = np.asarray([0.9, 0.8, 0.7, 0.1])
        target = np.asarray([1.0, 0.0, 1.0, 0.0])
        metrics = classification_at_threshold(values, target, 0.5)
        self.assertAlmostEqual(metrics["precision"], 2 / 3)
        self.assertEqual(metrics["recall"], 1.0)
        self.assertEqual(metrics["falsePositiveRate"], 0.5)
        self.assertEqual(metrics["falseNegativeRate"], 0.0)

    def test_heldout_pose_metrics_expose_missed_diagonals_by_card(self):
        names = ["visibility", "horizontal", "vertical"]
        target = np.asarray([
            [0.0, 0.0, 0.0], [0.0, 0.0, 0.0],
            [1.0, -0.75, 0.75], [1.0, -0.75, 0.75],
            [1.0, 0.75, -0.75], [1.0, 0.0, 0.0],
        ])
        prediction = np.asarray([
            [0.1, 0.0, 0.0], [0.9, 0.0, 0.0],
            [0.4, -0.75, 0.75], [0.8, -0.25, 0.25],
            [0.3, 0.5, -0.25], [0.9, 0.1, -0.1],
        ])
        step_ids = np.asarray([0, 0, 1, 1, 2, 3])
        native = np.zeros(len(target))
        report = heldout_pose_metrics(
            prediction, target, native, step_ids, names, 1.0, 0.5
        )
        cards = {card["promptId"]: card for card in report["perPrompt"]}
        self.assertEqual(cards[0]["visibleSamples"], 0)
        self.assertIsNone(cards[0]["visibilityFalseNegativeRate"])
        self.assertEqual(cards[0]["visibilityFalsePositiveRate"], 0.5)
        self.assertEqual(cards[1]["missedVisible"], 1)
        self.assertEqual(cards[1]["visibilityFalseNegativeRate"], 0.5)
        self.assertAlmostEqual(cards[1]["directionMae"], 0.25)
        self.assertEqual(cards[2]["visibilityFalseNegativeRate"], 1.0)
        corners = {corner["corner"]: corner for corner in report["diagonalCorners"]}
        self.assertEqual(corners["upper-left"]["visibleSamples"], 2)
        self.assertEqual(corners["upper-left"]["missedVisible"], 1)
        self.assertAlmostEqual(corners["lower-right"]["directionMae"], 0.375)
        self.assertIsNone(corners["upper-right"]["directionMae"])

        # The audit uses the selected global gate; it does not refit each card.
        native[2] = 1.0
        blended = heldout_pose_metrics(
            prediction, target, native, step_ids, names, 0.8, 0.5
        )
        self.assertEqual(blended["perPrompt"][1]["missedVisible"], 0)

    def test_heldout_pose_metrics_reject_misaligned_prompt_ids(self):
        with self.assertRaisesRegex(ValueError, "aligned"):
            heldout_pose_metrics(
                np.zeros((2, 3)), np.zeros((2, 3)), np.zeros(2),
                np.asarray([0]), ["visibility", "horizontal", "vertical"],
                1.0, 0.5,
            )


if __name__ == "__main__":
    unittest.main()
