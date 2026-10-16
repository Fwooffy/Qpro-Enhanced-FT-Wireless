"""Check public branding, compatibility instructions and the rendered PDF.

Run against a review PDF before copying it into a release. File identities used
by old updaters deliberately retain their legacy names.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import pdfplumber
from pypdf import PdfReader


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--audit", type=Path, required=True)
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    checks = []

    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})

    reader = PdfReader(args.pdf)
    texts = [page.extract_text() or "" for page in reader.pages]
    flat = " ".join("\n".join(texts).split())
    check("Reviewed 22-page structure", len(reader.pages) == 22)
    check("Public title metadata", reader.metadata.title == "Qpro Companion V3.0.0 - Setup and feature guide")
    check("Author credit", reader.metadata.author == "Fwooffy")
    check("Branded cover bookmark", reader.outline[0].title == "Qpro Companion")
    check("One bookmark per guide page", len(reader.outline) == len(reader.pages))
    check("No PDF JavaScript", "/JavaScript" not in str(reader.trailer["/Root"].get("/Names", {})))

    required = ["QproCompanion.V3.0.0.zip", "QproFaceTracking.V3.0.0.zip", "QproFaceTracking.exe",
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
                "Reconnect USB", "Check headset compatibility", "A failed check is not proof",
                "Pupil, eyebrow and tongue controls work separately", "VRChat menu scrolling",
                "Automatic selection requires an existing binding selection",
                "Preparation is not proof of live scrolling",
                "The bottom Start tracking / Stop tracking bar controls all selected features"]
    for phrase in required:
        check("Required instruction: " + phrase, phrase in flat)

    for phrase in ["QPRO HUB", "Live tracking", "Camera cheek puff", "Calibrate cheek puff",
                   "Developer lower-face model", "v13", "37 cards", "79 cards", "21 cheek",
                   "qpro_touch_pro", "TOUCHPAD_WAITING", "Under Show hand and controller details, choose"]:
        check("No obsolete instruction: " + phrase, phrase not in flat)

    links = 0
    page_refs = [page.indirect_reference for page in reader.pages]
    for index, page in enumerate(reader.pages, 1):
        text = texts[index - 1]
        check(f"Page {index}: public header", "QPRO COMPANION / SETUP & FEATURE GUIDE" in " ".join(text.split()))
        check(f"Page {index}: watermark and page count", "Made by Fwooffy" in text and f"{index:02d} / 22" in text)
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
        check(f"{path.name}: manual launch kept accurate", "QproFaceTracking.exe" in text)
        check(f"{path.name}: both download aliases explained", "QproCompanion.V3.0.0.zip" in text and "QproFaceTracking.V3.0.0.zip" in text)
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
