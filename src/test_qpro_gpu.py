"""GPU routing regression tests for mixed integrated/discrete PCs."""

import unittest
import json
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from qpro_gpu import (
    experimental_rocm_target_for_gpu_name,
    is_rocm_10_torch_build,
    is_rocm_721_torch_build,
    is_supported_rocm_gpu_name,
    preferred_torch_device_name,
    require_rocm_device_name,
    rocm_device_diagnostics,
    validated_torch_device_name,
)


ROCM_10_CARD_TARGETS = {
    "gfx1030": ("RX 6950 XT", "RX 6900 XT", "RX 6800 XT", "RX 6800"),
    "gfx1031": ("RX 6750 XT", "RX 6700 XT", "RX 6700"),
    "gfx1032": ("RX 6650 XT", "RX 6600 XT", "RX 6600"),
    "gfx1100": ("RX 7900 XTX", "RX 7900 XT", "RX 7900 GRE", "PRO W7900", "PRO W7900 Dual Slot"),
    "gfx1101": ("RX 7800 XT", "RX 7700 XT", "RX 7700"),
    "gfx1102": ("RX 7600 XT", "RX 7600"),
    "gfx1201": ("RX 9070 XT", "RX 9070", "RX 9070 GRE", "AI PRO R9700"),
    "gfx1200": ("RX 9060 XT", "RX 9060"),
}


class FakeCuda:
    def __init__(self, names, available=True, count_error=False, architectures=None, properties_error=False):
        self.names = names
        self.available = available
        self.count_error = count_error
        self.count_queries = 0
        self.architectures = architectures or [None] * len(names)
        self.properties_error = properties_error

    def is_available(self):
        return self.available

    def device_count(self):
        self.count_queries += 1
        if self.count_error:
            raise RuntimeError("offload-arch.exe points to an old Python path")
        return len(self.names)

    def get_device_name(self, index):
        return self.names[index]

    def get_device_properties(self, index):
        if self.properties_error:
            raise RuntimeError("HIP properties unavailable")
        return SimpleNamespace(gcnArchName=self.architectures[index])


def fake_torch(names, *, hip=None, rocm=None, cuda=None, available=True, count_error=False, torch_version="",
               architectures=None, properties_error=False, native_count=None):
    torch = SimpleNamespace(
        __version__=torch_version,
        version=SimpleNamespace(hip=hip, rocm=rocm, cuda=cuda),
        cuda=FakeCuda(names, available, count_error, architectures, properties_error),
    )
    if native_count is not None:
        torch._C = SimpleNamespace(_cuda_getDeviceCount=lambda: native_count)
    return torch


def write_experimental_marker(root, target, **overrides):
    record = {
        "schema": 1,
        "supportTier": "experimental-rocm-10",
        "rocmVersion": "10.0.0",
        "gfxTarget": target,
    }
    record.update(overrides)
    Path(root, "qpro-rocm-ready.json").write_text(json.dumps(record), encoding="utf-8")


class QproGpuTests(unittest.TestCase):
    def test_rocm_101_selects_each_mapped_discrete_target_after_integrated(self):
        for gfx, names in ROCM_10_CARD_TARGETS.items():
            for name in names:
                with self.subTest(gfx=gfx, name=name), tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root):
                    write_experimental_marker(root, gfx, rocmVersion="10.1.0")
                    torch = fake_torch(
                        ["AMD Radeon 780M", f"AMD Radeon {name}"],
                        hip="7.16.0", rocm="10.1.0", torch_version="2.14.0+rocm10.1.0",
                        architectures=["gfx1103", gfx + ":xnack-"],
                    )
                    self.assertTrue(is_rocm_10_torch_build(torch))
                    self.assertEqual(preferred_torch_device_name(torch), "cuda:1")
                    self.assertEqual(require_rocm_device_name(torch), "cuda:1")
                    self.assertEqual(validated_torch_device_name(torch, "cuda:1"), "cuda:1")
                    with self.assertRaises(RuntimeError):
                        validated_torch_device_name(torch, "cuda:0")

    def test_rocm_101_rejects_wrong_architecture_and_previous_release_receipt(self):
        torch = fake_torch(["AMD Radeon RX6700XT"], hip="7.16.0", rocm="10.1.0",
                           torch_version="2.14.0+rocm10.1.0", architectures=["gfx1031"])
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root):
            write_experimental_marker(root, "gfx1031")
            self.assertEqual(preferred_torch_device_name(torch), "cpu")
            write_experimental_marker(root, "gfx1031", rocmVersion="10.1.0")
            self.assertEqual(preferred_torch_device_name(torch), "cuda:0")
            torch.cuda.architectures = ["gfx1036"]
            self.assertEqual(preferred_torch_device_name(torch), "cpu")
            with self.assertRaises(RuntimeError):
                require_rocm_device_name(torch)

    def test_rocm_list_rejects_integrated_and_unlisted_cards(self):
        self.assertTrue(is_supported_rocm_gpu_name("AMD Radeon RX 7900 XTX"))
        self.assertTrue(is_supported_rocm_gpu_name("AMD Radeon(TM) RX 7900 XTX"))
        self.assertTrue(is_supported_rocm_gpu_name("AMD Radeon PRO W7900 Dual Slot"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon Graphics"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon 780M"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon RX 7600"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon RX 9070 Mobile"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon RX 6800 XT"))

    def test_experimental_models_map_to_rocm_10_targets(self):
        for gfx, names in ROCM_10_CARD_TARGETS.items():
            for name in names:
                full_name = f"AMD Radeon {name}"
                self.assertEqual(experimental_rocm_target_for_gpu_name(full_name), gfx)
                self.assertTrue(is_supported_rocm_gpu_name(full_name, "10.0.0"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon RX 6800 XT", "7.2.1"))
        self.assertEqual(experimental_rocm_target_for_gpu_name("AMD RX 9070 GRE"), "gfx1201")
        for name in ("AMD Radeon RX 6500 XT", "AMD Radeon RX 6400", "AMD Radeon 780M", "AMD Radeon RX 9050"):
            self.assertFalse(is_supported_rocm_gpu_name(name, "10.0.0"))
        self.assertFalse(is_supported_rocm_gpu_name("AMD Radeon RX 6800 XT", "10.1"))
        self.assertTrue(is_supported_rocm_gpu_name("AMD Radeon RX 7900 XTX", "10.0.0"))

    def test_desktop_card_names_accept_driver_spacing_without_accepting_mobile(self):
        for name in ("AMD Radeon RX 6700 XT", "AMD Radeon RX 6700XT", "AMD Radeon RX6700XT",
                     "AMD RX6700XT", "RX6700XT", "Radeon™ RX 6700XT", " AMD Radeon(TM)  RX6700XT "):
            with self.subTest(name=name):
                self.assertEqual(experimental_rocm_target_for_gpu_name(name), "gfx1031")
                self.assertTrue(is_supported_rocm_gpu_name(name, "10.0.0"))
        for name, target in (("AMD Radeon RX7900XTX", "gfx1100"), ("RX9070GRE", "gfx1201"),
                             ("Radeon® RX9060XT", "gfx1200")):
            self.assertEqual(experimental_rocm_target_for_gpu_name(name), target)
        for name in ("AMD Radeon RX6700M", "AMD Radeon RX6700XT Mobile", "AMD Radeon RX6700X",
                     "AMD Radeon RX6700XT Graphics", "AMD Radeon 780M", "RX67000XT", "RX6500XT"):
            self.assertIsNone(experimental_rocm_target_for_gpu_name(name))

    def test_experimental_rocm_skips_integrated_gpu(self):
        torch = fake_torch(
            ["AMD Radeon Graphics", "AMD Radeon RX 6800 XT"], hip="7.15.26333",
            rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
        )
        with tempfile.TemporaryDirectory() as root:
            write_experimental_marker(root, "gfx1030")
            with patch.dict("os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "", "QPRO_ROCM_EXPECTED_GFX_TARGET": ""}), patch(
                "qpro_gpu.sys.prefix", root
            ):
                self.assertEqual(preferred_torch_device_name(torch), "cuda:1")
                self.assertEqual(require_rocm_device_name(torch), "cuda:1")
                with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
                    validated_torch_device_name(torch, "cuda:0")

    def test_experimental_rocm_uses_only_the_installed_device_target(self):
        torch = fake_torch(
            ["AMD Radeon RX 7900 XT", "AMD Radeon RX 6800 XT"], hip="7.15.26333",
            rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1030"}
        ):
            self.assertEqual(require_rocm_device_name(torch), "cuda:1")
            with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
                validated_torch_device_name(torch, "cuda:0")

        with tempfile.TemporaryDirectory() as root:
            write_experimental_marker(root, "gfx1100")
            with patch.dict("os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1030"}), patch(
                "qpro_gpu.sys.prefix", root
            ):
                self.assertEqual(require_rocm_device_name(torch), "cuda:0")

    def test_rocm_10_refuses_missing_or_invalid_ready_marker(self):
        torch = fake_torch(
            ["AMD Radeon RX 7900 XTX"], hip="7.15.26333",
            rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1100"}
        ):
            self.assertEqual(preferred_torch_device_name(torch), "cpu")
            with self.assertRaisesRegex(RuntimeError, "no valid Qpro readiness record"):
                require_rocm_device_name(torch)
            with self.assertRaisesRegex(RuntimeError, "no valid Qpro readiness record"):
                validated_torch_device_name(torch, "cuda:0")
            for invalid in (
                {"supportTier": "wrong"},
                {"rocmVersion": "7.2.1"},
                {"gfxTarget": "gfx9999"},
            ):
                write_experimental_marker(root, "gfx1100", **invalid)
                self.assertEqual(preferred_torch_device_name(torch), "cpu")
            Path(root, "qpro-rocm-ready.json").write_text("{broken", encoding="utf-8")
            self.assertEqual(preferred_torch_device_name(torch), "cpu")

    def test_rocm_10_sdk_version_selects_every_mapped_card_and_marker(self):
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "", "QPRO_ROCM_EXPECTED_GFX_TARGET": ""}
        ):
            for gfx, names in ROCM_10_CARD_TARGETS.items():
                for name in names:
                    with self.subTest(gfx=gfx, name=name):
                        write_experimental_marker(root, gfx)
                        torch = fake_torch(
                            ["AMD Radeon Graphics", f"AMD Radeon {name}"], hip="7.15.26333",
                            rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
                        )
                        self.assertEqual(preferred_torch_device_name(torch), "cuda:1")
                        self.assertEqual(require_rocm_device_name(torch), "cuda:1")
                        self.assertEqual(validated_torch_device_name(torch, "cuda:1"), "cuda:1")
                        with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
                            validated_torch_device_name(torch, "cuda:0")

    def test_rocm_10_wheel_uses_sdk_release_instead_of_hip_version(self):
        # The installed ROCm 10 Windows wheel reports HIP 7.15.26333.
        torch = fake_torch(
            ["AMD Radeon RX 7900 XTX"], hip="7.15.26333",
            rocm="10.0.0", torch_version="2.13.0+rocm10.0.0", count_error=True,
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1100"}
        ):
            self.assertTrue(is_rocm_10_torch_build(torch))
            self.assertEqual(preferred_torch_device_name(torch), "cuda:0")
            self.assertEqual(require_rocm_device_name(torch), "cuda:0")
            self.assertEqual(validated_torch_device_name(torch, "cuda:0"), "cuda:0")
            self.assertEqual(torch.cuda.count_queries, 0)
            # Missing SDK metadata is supported only through the exact pin.
            del torch.version.rocm
            self.assertTrue(is_rocm_10_torch_build(torch))
            self.assertEqual(require_rocm_device_name(torch), "cuda:0")

    def test_rocm_10_rejects_mismatched_or_cpu_build_metadata(self):
        for torch_version, hip, rocm in (
            ("2.13.0+cpu", None, None),
            ("2.13.0+cpu", "7.15.26333", "10.0.0"),
            ("2.14.0+rocm10.0.0", "7.15.26333", "10.0.0"),
            ("2.13.0+rocm10.0.0", None, "10.0.0"),
            ("2.13.0+rocm10.0.0", "7.15.26333", "10.1.0"),
            ("2.13.0+rocm10.0.0", "7.15.26333", "7.2.1"),
            ("2.13.0+rocm10.0.0", "7.15.26333", ""),
            ("2.13.0+rocm10.1.0", "7.16.0", "10.1.0"),
            ("2.9.1+rocm7.2.1", "7.2.53211", "10.0.0"),
            ("", "10.0.0", None),
        ):
            with self.subTest(torch_version=torch_version, hip=hip, rocm=rocm), tempfile.TemporaryDirectory() as root, patch(
                "qpro_gpu.sys.prefix", root
            ), patch.dict("os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1100"}):
                torch = fake_torch(
                    ["AMD Radeon RX 7900 XTX"], torch_version=torch_version, hip=hip, rocm=rocm,
                    available=bool(hip),
                )
                self.assertFalse(is_rocm_10_torch_build(torch))
                self.assertEqual(preferred_torch_device_name(torch), "cpu")
                with self.assertRaises(RuntimeError):
                    require_rocm_device_name(torch)
                with self.assertRaises(RuntimeError):
                    validated_torch_device_name(torch, "cuda:0")

    def test_rocm_skips_igpu_at_device_zero(self):
        torch = fake_torch(
            ["AMD Radeon 780M", "AMD Radeon RX 7900 XTX"], hip="7.2.1"
        )
        self.assertEqual(preferred_torch_device_name(torch), "cuda:1")
        self.assertEqual(require_rocm_device_name(torch), "cuda:1")
        with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
            validated_torch_device_name(torch, "cuda:0")
        self.assertEqual(validated_torch_device_name(torch, "cuda:1"), "cuda:1")

    def test_rocm_uses_named_discrete_device_when_count_launcher_is_broken(self):
        torch = fake_torch(["AMD Radeon RX 7900 XTX"], hip="7.2.1", count_error=True)
        self.assertEqual(preferred_torch_device_name(torch), "cuda:0")
        self.assertEqual(require_rocm_device_name(torch), "cuda:0")
        self.assertEqual(validated_torch_device_name(torch, "cuda:0"), "cuda:0")
        self.assertEqual(torch.cuda.count_queries, 0)

    def test_rocm_wheel_release_identifies_hip_build_version(self):
        # The Windows 7.2.1 wheel reports torch.version.hip as 7.2.53211.
        torch = fake_torch(
            ["AMD Radeon RX 7900 XTX"], hip="7.2.53211-158bd99533",
            torch_version="2.9.1+rocm7.2.1", count_error=True,
        )
        self.assertTrue(is_rocm_721_torch_build(torch))
        self.assertEqual(preferred_torch_device_name(torch), "cuda:0")
        self.assertEqual(require_rocm_device_name(torch), "cuda:0")
        self.assertEqual(validated_torch_device_name(torch, "cuda:0"), "cuda:0")
        self.assertEqual(torch.cuda.count_queries, 0)

        torch.__version__ = "2.9.1+rocm7.2.0"
        self.assertFalse(is_rocm_721_torch_build(torch))
        self.assertEqual(preferred_torch_device_name(torch), "cpu")
        with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
            validated_torch_device_name(torch, "cuda:0")
        torch.__version__ = "2.9.2+rocm7.2.1"
        self.assertFalse(is_rocm_721_torch_build(torch))
        torch.__version__ = "2.9.1+rocm7.2.1"
        torch.version.hip = "7.3.53211"
        self.assertFalse(is_rocm_721_torch_build(torch))

    def test_broken_count_never_promotes_an_integrated_gpu(self):
        torch = fake_torch(["AMD Radeon 780M"], hip="7.2.1", count_error=True)
        self.assertEqual(preferred_torch_device_name(torch), "cpu")
        with self.assertRaisesRegex(RuntimeError, "integrated graphics"):
            require_rocm_device_name(torch)
        with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
            validated_torch_device_name(torch, "cuda:0")
        with self.assertRaisesRegex(RuntimeError, "unavailable"):
            validated_torch_device_name(torch, "cuda:1")

    def test_native_hip_count_finds_discrete_after_multiple_integrated_adapters(self):
        torch = fake_torch(
            ["AMD Radeon Graphics", "AMD Radeon 780M", "AMD Radeon RX6700XT"],
            hip="7.15.26333", rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
            count_error=True, native_count=3, architectures=["gfx1036", "gfx1103", "gfx1031:xnack-"],
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1031"}
        ):
            self.assertEqual(require_rocm_device_name(torch), "cuda:2")
            self.assertEqual(validated_torch_device_name(torch, "cuda:2"), "cuda:2")
            self.assertEqual(torch.cuda.count_queries, 0)
            with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
                validated_torch_device_name(torch, "cuda:0")

    def test_native_count_does_not_guess_a_hidden_discrete_adapter_index(self):
        torch = fake_torch(
            ["AMD Radeon 780M", "AMD Radeon RX6700XT"],
            hip="7.15.26333", rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
            count_error=True, native_count=1,
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1031"}
        ):
            self.assertEqual(preferred_torch_device_name(torch), "cpu")

    def test_reported_architecture_must_match_discrete_name_and_ready_target(self):
        torch = fake_torch(
            ["AMD Radeon RX6700XT", "AMD Radeon RX 6700 XT"],
            hip="7.15.26333", rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
            architectures=["gfx1036", "gfx1031:sramecc-:xnack-"],
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1031"}
        ):
            self.assertEqual(require_rocm_device_name(torch), "cuda:1")
            with self.assertRaisesRegex(RuntimeError, "installed gfx target"):
                validated_torch_device_name(torch, "cuda:0")
            write_experimental_marker(root, "gfx1030")
            self.assertEqual(preferred_torch_device_name(torch), "cpu")

    def test_properties_error_does_not_promote_a_device_by_name_alone(self):
        torch = fake_torch(["AMD Radeon RX 7900 XTX"], hip="7.2.1", properties_error=True)
        self.assertEqual(preferred_torch_device_name(torch), "cpu")
        with self.assertRaisesRegex(RuntimeError, "not a supported discrete"):
            validated_torch_device_name(torch, "cuda:0")

    def test_diagnostics_identify_integrated_wrong_target_and_architecture(self):
        torch = fake_torch(
            ["AMD Radeon Graphics", "AMD Radeon RX6700XT", "AMD Radeon RX 6800 XT", "RX 6700XT"],
            hip="7.15.26333", rocm="10.0.0", torch_version="2.13.0+rocm10.0.0",
            architectures=["gfx1036", "gfx1031", "gfx1030", "gfx1036"],
        )
        with tempfile.TemporaryDirectory() as root, patch("qpro_gpu.sys.prefix", root), patch.dict(
            "os.environ", {"QPRO_ROCM_INSTALL_SMOKE_TEST": "1", "QPRO_ROCM_EXPECTED_GFX_TARGET": "gfx1031"}
        ):
            details = rocm_device_diagnostics(torch)
            self.assertIn("cuda:0 AMD Radeon Graphics (gfx1036): integrated", details)
            self.assertIn("cuda:1 AMD Radeon RX6700XT (gfx1031): eligible discrete GPU", details)
            self.assertIn("requires gfx1030; environment prepared for gfx1031", details)
            self.assertIn("reported architecture differs from model target gfx1031", details)

    def test_visibility_settings_are_explained_without_rewriting_them_after_import(self):
        torch = fake_torch(["AMD Radeon Graphics"], hip="7.2.1")
        with patch.dict("os.environ", {"HIP_VISIBLE_DEVICES": "0", "ROCR_VISIBLE_DEVICES": "0"}):
            with self.assertRaisesRegex(RuntimeError, "HIP_VISIBLE_DEVICES.*ROCR_VISIBLE_DEVICES"):
                require_rocm_device_name(torch)
            self.assertEqual(__import__("os").environ["HIP_VISIBLE_DEVICES"], "0")

    def test_rocm_igpu_only_uses_cpu_or_explains_failure(self):
        torch = fake_torch(["AMD Radeon Graphics"], hip="7.2.1")
        self.assertEqual(preferred_torch_device_name(torch), "cpu")
        with self.assertRaisesRegex(RuntimeError, "integrated graphics"):
            require_rocm_device_name(torch)

    def test_nvidia_cuda_ignores_host_igpu(self):
        torch = fake_torch(["NVIDIA GeForce RTX 3060"], cuda="12.8")
        self.assertEqual(preferred_torch_device_name(torch), "cuda:0")

    def test_no_gpu_uses_cpu(self):
        torch = fake_torch([], available=False)
        self.assertEqual(preferred_torch_device_name(torch), "cpu")


if __name__ == "__main__":
    unittest.main()
