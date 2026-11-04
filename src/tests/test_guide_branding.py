"""Check public branding, compatibility instructions and the rendered PDF.

Run against a review PDF before copying it into a release. File identities used
by old updaters deliberately retain their legacy names.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

import pdfplumber
from pypdf import PdfReader


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--version", help="Expected three-part version; defaults to the text guide's declared version")
    parser.add_argument("--pages", type=int, help="Expected pages; defaults to cover plus the print source's H2 sections")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    checks = []
    version_match = re.search(r"^# Qpro Companion (V\d+\.\d+\.\d+)",
                              (source / "RELEASE_INSTRUCTIONS.md").read_text(encoding="utf-8"))
    if version_match is None:
        raise ValueError("Text setup instructions must declare a three-part release version")
    version = args.version or version_match[1]
    if not re.fullmatch(r"V\d+\.\d+\.\d+", version):
        raise ValueError("Expected version must use Vmajor.minor.patch")
    expected_pages = args.pages if args.pages is not None else 1 + len(re.findall(
        r"^## ", (source / "PDF_GUIDE.md").read_text(encoding="utf-8"), re.MULTILINE))
    if expected_pages < 2:
        raise ValueError("Expected guide must include a cover and at least one content page")

    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})

    reader = PdfReader(args.pdf)
    texts = [page.extract_text() or "" for page in reader.pages]
    flat = " ".join("\n".join(texts).split())
    check(f"Reviewed {expected_pages}-page source structure", len(reader.pages) == expected_pages)
    check("Public title metadata", reader.metadata.title == f"Qpro Companion {version} - Setup and feature guide")
    check("Author credit", reader.metadata.author == "Fwooffy")
    check("Branded cover bookmark", reader.outline[0].title == "Qpro Companion")
    check("One bookmark per guide page", len(reader.outline) == len(reader.pages))
    check("No PDF JavaScript", "/JavaScript" not in str(reader.trailer["/Root"].get("/Names", {})))

    required = [f"QproCompanion.{version}.zip", f"QproFaceTracking.{version}.zip", "QproFaceTracking.exe",
                "update compatibility", ".qptonguemodel", "Qpro Companion — Virtual Desktop",
                "Qpro Companion — Steam Link", "Older QproFaceTracking cards remain recognized",
                "Developer tongue model", "Mustachio", "two bundled choices", "Local model numbers can change",
                "Weighted camera + native", "same wearer's headset", "small tongue tip",
                "broader wearer and facial-hair testing remain pending", "Individual cheek puff (1/0)",
                "Individual cheek suck (1/0)", "original puff or suck values", "restores native cheeks",
                "16 tongue cards", "22 tongue cards", "58 tongue cards", "Facial tracking", "Controllers",
                "Activity > Tracking health", "Automatically switch between controllers and hands",
                "Skip Check gaze setup and Prepare gaze", "Thumb-rest mode", "Apply mode to",
                "Directly below the two feature switches", "keep both controllers awake", "No premade game assignments",
                "Dashboard scrolling", "preserving ordinary controls", "Qpro Companion thumb-rest scrolling",
                "Personal dashboard choices stay selected", "other modes and Stop send neutral scroll",
                "provided by Magisk", "stays off and disabled", "those buttons are disabled too",
                "Check USB connection", "Check headset compatibility", "A failed check is not proof",
                "Pupil, eyebrow and tongue controls work separately", "VRChat menu scrolling",
                "Automatic selection requires an existing binding selection",
                "Preparation is not proof of live scrolling",
                "The bottom Start tracking / Stop tracking bar controls all selected features",
                "1. Connect", "2. PC runtime", "3. VRCFT module", "Next step", "Open Facial tracking",
                "Show optional tools and recovery", "Show wireless setup options", "Show pairing options",
                "View setup activity", "ModuleProcess", "VRCFaceTracking module maintenance", "Show tongue settings",
                "Optional features", "Checks & Updates", "Recovery > Show gaze recovery tools",
                "Recovery > Show PC runtime repair and recovery", "Recovery > Show VRCFaceTracking module maintenance",
                "read-only preflight result", "PE code/layout family", "fixed-offset hooks",
                "Mono hand-result fields, types, sizes and layout", "not support for all versions"]
    for phrase in required:
        check("Required instruction: " + phrase, phrase in flat)

    for phrase in ["QPRO HUB", "Live tracking", "Camera cheek puff", "Calibrate cheek puff",
                   "Developer lower-face model", "v13", "37 cards", "79 cards", "21 cheek",
                   "qpro_touch_pro", "TOUCHPAD_WAITING", "Under Show hand and controller details, choose",
                   "Reconnect USB", "Show module uninstall", "Show camera settings"]:
        check("No obsolete instruction: " + phrase, phrase not in flat)

    links = 0
    page_refs = [page.indirect_reference for page in reader.pages]
    for index, page in enumerate(reader.pages, 1):
        text = texts[index - 1]
        check(f"Page {index}: public header", "QPRO COMPANION / SETUP & FEATURE GUIDE" in " ".join(text.split()))
        check(f"Page {index}: watermark and page count", "Made by Fwooffy" in text and f"{index:02d} / {expected_pages}" in text)
        for pointer in page.get("/Annots", []):
            item = pointer.get_object()
            if item.get("/Subtype") != "/Link":
                continue
            links += 1
            destination = item.get("/Dest")
            if destination is not None:
                check(f"Page {index}: valid internal link {links}",
                      isinstance(destination, list) and bool(destination) and destination[0] in page_refs)
    check("Navigable links retained", links > 30)

    with pdfplumber.open(args.pdf) as document:
        for index, page in enumerate(document.pages, 1):
            outside = [word["text"] for word in page.extract_words()
                       if word["x0"] < 47 or word["x1"] > page.width - 47
                       or word["top"] < 14 or word["bottom"] > page.height - 20]
            check(f"Page {index}: text within margins", not outside)

    names = ["README.md", "RELEASE_INSTRUCTIONS.md", "RELEASE_FIX_NOTES.md", "RELEASE_README.md",
             "RELEASE_NOTES_V3.0.0.md", "PDF_GUIDE.md", "CONTROLLER_INPUT.md"]
    for name in names:
        text = (source / name).read_text(encoding="utf-8")
        check(f"{name}: public title", text.splitlines()[0].startswith("# Qpro Companion"))
        check(f"{name}: current page labels", "Live tracking" not in text)
        check(f"{name}: no withdrawn controller identity", "qpro_touch_pro" not in text and "TOUCHPAD_WAITING" not in text)
        check(f"{name}: no trailing spaces", not any(line.rstrip() != line for line in text.splitlines()))
    for path in [source.parent / "README.md", source / "RELEASE_INSTRUCTIONS.md", source / "RELEASE_README.md"]:
        text = path.read_text(encoding="utf-8")
        # The public README describes the published stable release while these
        # setup documents can describe a newer test build. Verify both aliases
        # for each document's declared version without rewriting release history.
        document_version = re.search(r"^# Qpro Companion (V\d+\.\d+\.\d+)", text)
        if document_version is None:
            raise ValueError(f"{path}: missing public release version")
        document_version = document_version[1]
        check(f"{path.name}: manual launch kept accurate", "QproFaceTracking.exe" in text)
        if path == source.parent / "README.md":
            check("Published README: named release download", f"QproCompanion.{document_version}.zip" in text)
            check("Published README: legacy identities explained", "existing app/data folders" in text and "update compatibility" in text)
        else:
            check(f"{path.name}: both download aliases explained", f"QproCompanion.{document_version}.zip" in text and f"QproFaceTracking.{document_version}.zip" in text)
        check(f"{path.name}: model export preserved", ".qptonguemodel" in text)

    result = {"status": "PASS" if all(item["passed"] for item in checks) else "FAIL",
              "pdf": str(args.pdf), "sha256": hashlib.sha256(args.pdf.read_bytes()).hexdigest(),
              "pages": len(reader.pages), "bytes": args.pdf.stat().st_size, "links": links, "checks": checks}
    args.audit.parent.mkdir(parents=True, exist_ok=True)
    args.audit.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"{result['status']}: {sum(item['passed'] for item in checks)}/{len(checks)} checks; {len(reader.pages)} pages")
    for item in checks:
        if not item["passed"]:
            print("FAIL: " + item["name"])
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
