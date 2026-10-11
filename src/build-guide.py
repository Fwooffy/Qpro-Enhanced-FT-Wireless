"""Render the edited PDF companion in the Hub's colours.

PDF_GUIDE.md is the concise print edition; RELEASE_INSTRUCTIONS.md remains the
full reference. Each H2 starts a page. Fail on overflow instead of quietly
moving a checklist away from its instructions.
"""
from __future__ import annotations

import argparse
import html
import json
import re
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import BaseDocTemplate, Flowable, Frame, PageBreak, PageTemplate, Paragraph, Spacer, Table, TableStyle

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "PDF_GUIDE.md"
REFERENCE = ROOT / "RELEASE_INSTRUCTIONS.md"
OUTPUT = ROOT / "Quest_Pro_Enhanced_Face_Tracking_Guide.pdf"
PAGE_W, PAGE_H = A4
MARGIN = 48
WIDTH = PAGE_W - 2 * MARGIN
FRAME_TOP, FRAME_BOTTOM = PAGE_H - 88, 66
HEIGHT = FRAME_TOP - FRAME_BOTTOM

# HubForm palette. The footer has its own area below the content frame.
BACKGROUND = colors.HexColor("#213942")
PANEL = colors.HexColor("#192F37")
INSET = colors.HexColor("#11232A")
RAISED = colors.HexColor("#2C4B55")
BORDER = colors.HexColor("#527C84")
ACCENT = colors.HexColor("#72D0C6")
MUTED = colors.HexColor("#C9DCDE")
GOOD = colors.HexColor("#91E4B8")
WARNING = colors.HexColor("#F0D08A")
BAD = colors.HexColor("#F38D85")
WHITE = colors.HexColor("#F7FAFB")

BODY = ParagraphStyle("body", fontName="Helvetica", fontSize=10.3, leading=14.6,
                      textColor=MUTED, spaceAfter=9, allowWidows=0, allowOrphans=0)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=9, leading=12.8)
TITLE = ParagraphStyle("title", parent=BODY, fontName="Helvetica-Bold", fontSize=26,
                       leading=31, textColor=WHITE, spaceAfter=13, keepWithNext=True)
DECK = ParagraphStyle("deck", parent=BODY, fontSize=11.2, leading=16, spaceAfter=18)
H3 = ParagraphStyle("heading", parent=BODY, fontName="Helvetica-Bold", fontSize=12,
                    leading=16, textColor=ACCENT, spaceBefore=6, spaceAfter=7, keepWithNext=True)
CELL = ParagraphStyle("cell", parent=BODY, fontSize=9.6, leading=13.4, spaceAfter=0)
HEAD = ParagraphStyle("table heading", parent=CELL, fontName="Helvetica-Bold", textColor=WHITE)
NUMBER = ParagraphStyle("number", parent=BODY, fontName="Helvetica-Bold", textColor=ACCENT, fontSize=11, spaceAfter=0)
KICKER = ParagraphStyle("kicker", parent=BODY, fontName="Helvetica-Bold", fontSize=9,
                        leading=12, textColor=ACCENT, spaceAfter=10)
TOKEN = re.compile(r"\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|`([^`]+)`|\*([^*]+)\*")


def normalized(text: str) -> str:
    return text.translate(str.maketrans({"\u2011": "-", "\u2013": "-", "\u2014": "-",
        "\u2018": "'", "\u2019": "'", "\u201c": '"', "\u201d": '"', "\u00d7": "x"}))


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def inline(text: str) -> str:
    text = normalized(text)
    result, pos = [], 0
    for match in TOKEN.finditer(text):
        result.append(html.escape(text[pos:match.start()]))
        if match.group(1) is not None:
            label, target = match.group(1), match.group(2)
            result.append(f'<link href="{html.escape(target, quote=True)}" color="#72D0C6"><u>{html.escape(label)}</u></link>')
        elif match.group(3) is not None:
            result.append(f'<font color="#F7FAFB"><b>{html.escape(match.group(3))}</b></font>')
        elif match.group(4) is not None:
            result.append(f'<font face="Courier" size="9.1">{html.escape(match.group(4))}</font>')
        else:
            result.append(f"<i>{html.escape(match.group(5))}</i>")
        pos = match.end()
    result.append(html.escape(text[pos:]))
    return "".join(result)


def paragraph(text: str, style=BODY):
    return Paragraph(inline(text).replace("\n", "<br/>"), style)


def note(kind: str, title: str, text: str):
    tone = {"TIP": GOOD, "NOTE": ACCENT, "WARNING": WARNING, "CAUTION": BAD}[kind]
    heading = ParagraphStyle(f"note-{kind}", parent=H3, textColor=tone, spaceBefore=0, spaceAfter=6)
    table = Table([[paragraph(title, heading)], [paragraph(text, CELL)]], colWidths=[WIDTH])
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), PANEL), ("LINEBEFORE", (0, 0), (0, -1), 3, tone),
        ("LEFTPADDING", (0, 0), (-1, -1), 14), ("RIGHTPADDING", (0, 0), (-1, -1), 14),
        ("TOPPADDING", (0, 0), (-1, 0), 12), ("BOTTOMPADDING", (0, 0), (-1, 0), 1),
        ("TOPPADDING", (0, 1), (-1, 1), 0), ("BOTTOMPADDING", (0, 1), (-1, 1), 13),
    ]))
    return [table, Spacer(1, 12)]


def step(number: str, text: str):
    table = Table([[paragraph(number, NUMBER), paragraph(text)]], colWidths=[27, WIDTH - 27])
    table.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"), ("LEFTPADDING", (0, 0), (-1, -1), 0),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4), ("TOPPADDING", (0, 0), (-1, -1), 0),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
    ]))
    return table


def grid(rows: list[list[str]]):
    if not rows or any(len(row) != 2 for row in rows):
        raise ValueError("Guide tables must have two columns")
    table = Table([[paragraph(cell, HEAD if i == 0 else CELL) for cell in row] for i, row in enumerate(rows)],
                  colWidths=[WIDTH * .34, WIDTH * .66])
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), RAISED), ("ROWBACKGROUNDS", (0, 1), (-1, -1), [PANEL, INSET]),
        ("LINEBELOW", (0, 0), (-1, 0), 1, ACCENT), ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 10), ("RIGHTPADDING", (0, 0), (-1, -1), 10),
        ("TOPPADDING", (0, 0), (-1, -1), 9), ("BOTTOMPADDING", (0, 0), (-1, -1), 9),
    ]))
    return [table, Spacer(1, 13)]


class CoverMark(Flowable):
    """Draw an eye/camera motif as vectors, with no external image assets."""
    def __init__(self):
        super().__init__()
        self.width, self.height = WIDTH, 138

    def draw(self):
        c, x, y = self.canv, WIDTH / 2, 69
        c.setFillColor(PANEL)
        c.roundRect(x - 88, y - 58, 176, 116, 20, fill=1, stroke=0)
        c.setStrokeColor(ACCENT)
        c.setLineWidth(2)
        for dx in (-38, 38):
            c.ellipse(x + dx - 24, y - 5, x + dx + 24, y + 23, stroke=1, fill=0)
            c.setFillColor(ACCENT)
            c.circle(x + dx, y + 9, 7, fill=1, stroke=0)
        p = c.beginPath()
        p.moveTo(x - 34, y - 20)
        p.curveTo(x - 15, y - 42, x + 15, y - 42, x + 34, y - 20)
        c.drawPath(p)
        c.setStrokeColor(BORDER)
        c.line(0, 0, WIDTH, 0)


class GuideDoc(BaseDocTemplate):
    def __init__(self, version: str, titles: list[str]):
        super().__init__(str(OUTPUT), pagesize=A4, leftMargin=MARGIN, rightMargin=MARGIN,
                         topMargin=88, bottomMargin=66, title=f"QproFaceTracking {version} - Setup and feature guide",
                         author="Fwooffy", subject="Rooted Quest Pro setup, features and recovery", pageCompression=1)
        self.version, self.titles, self.records = version, titles, []
        self.addPageTemplates(PageTemplate("guide", frames=[Frame(MARGIN, FRAME_BOTTOM, WIDTH, HEIGHT,
            leftPadding=0, rightPadding=0, topPadding=0, bottomPadding=0)], onPage=self.paint_page))

    def paint_page(self, c, doc):
        c.saveState()
        c.setFillColor(BACKGROUND)
        c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
        c.setFillColor(PANEL)
        c.rect(0, PAGE_H - 56, PAGE_W, 56, fill=1, stroke=0)
        c.setFillColor(ACCENT)
        c.rect(MARGIN, PAGE_H - 56, 34, 3, fill=1, stroke=0)
        c.setFont("Helvetica-Bold", 9)
        c.setFillColor(WHITE)
        c.drawString(MARGIN, PAGE_H - 33, "QPRO HUB  /  SETUP & FEATURE GUIDE")
        c.setFont("Helvetica", 9)
        c.setFillColor(MUTED)
        c.drawRightString(PAGE_W - MARGIN, PAGE_H - 33, self.version)
        c.setStrokeColor(BORDER)
        c.setLineWidth(.5)
        c.line(MARGIN, 47, PAGE_W - MARGIN, 47)
        c.setFont("Helvetica", 8)
        c.setFillColor(MUTED)
        c.drawString(MARGIN, 29, "ROOTED QUEST PRO")
        c.setFillColor(BORDER)
        c.setFont("Helvetica-Bold", 10)
        c.drawCentredString(PAGE_W / 2, 28, "Made by Fwooffy")
        c.setFont("Helvetica", 8)
        c.setFillColor(MUTED)
        c.drawRightString(PAGE_W - MARGIN, 29, f"{doc.page:02d} / {len(self.titles):02d}")
        if doc.page <= len(self.titles):
            title = self.titles[doc.page - 1]
            c.bookmarkPage(slug(title))
            c.addOutlineEntry(title, slug(title), level=0, closed=False)
        c.restoreState()

    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph) and flowable.style.name == TITLE.name:
            self.records.append({"page": self.page, "title": flowable.getPlainText()})


def sections(markdown: str):
    found, current = [], None
    for line in markdown.splitlines():
        if line.startswith("## "):
            current = {"title": line[3:].strip(), "lines": []}
            found.append(current)
        elif current is not None:
            current["lines"].append(line)
    return found


def page_content(section: dict, number: int):
    lines = section["lines"]
    story = [paragraph(f"GUIDE  /  {number:02d}", KICKER), paragraph(section["title"], TITLE)]
    i, first_paragraph = 0, True
    while i < len(lines):
        line = lines[i].strip()
        if not line:
            i += 1
            continue
        if line.startswith("### "):
            story.append(paragraph(line[4:], H3))
        elif line.startswith("> [!"):
            match = re.fullmatch(r"> \[!(TIP|NOTE|WARNING|CAUTION)\] (.+)", line)
            if not match:
                raise ValueError(f"Invalid callout: {line}")
            body = []
            i += 1
            while i < len(lines) and lines[i].startswith("> "):
                body.append(lines[i][2:])
                i += 1
            story.extend(note(match[1], match[2], " ".join(body)))
            continue
        elif line.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                row = [cell.strip() for cell in lines[i].strip().strip("|").split("|")]
                if not all(re.fullmatch(r":?-{3,}:?", cell) for cell in row):
                    rows.append(row)
                i += 1
            story.extend(grid(rows))
            continue
        elif match := re.match(r"^(\d+)\.\s+(.*)", line):
            story.append(step(match[1], match[2]))
        elif line.startswith("- "):
            story.append(step("-", line[2:]))
        else:
            story.append(paragraph(line, DECK if first_paragraph else BODY))
            first_paragraph = False
        i += 1
    used = sum(item.wrap(WIDTH, HEIGHT)[1] + item.getSpaceBefore() + item.getSpaceAfter() for item in story)
    if used > HEIGHT:
        raise ValueError(f"Page {number} ({section['title']}) needs {used:.1f}pt; only {HEIGHT:.1f}pt available")
    return story


def cover(version: str):
    cover_title = ParagraphStyle("cover title", parent=TITLE, fontSize=32, leading=38)
    return [paragraph("QUEST PRO  /  ROOTED HEADSET", KICKER), Spacer(1, 10),
            paragraph("QproFaceTracking", cover_title), paragraph("Set up once.\nChoose what you track.", TITLE),
            paragraph(f"{version} release candidate\nUSB or wireless ADB - Virtual Desktop or Steam Link", DECK),
            CoverMark(), Spacer(1, 24), paragraph("Start here", H3),
            paragraph("Get ordinary face tracking moving first. Then add tongue tracking, cheek controls, "
                      "pupil dilation or other optional features at your own pace."),
            *note("WARNING", "Required before setup", "A **rooted Meta Quest Pro** and the latest "
                  "**VRCFaceTracking from Steam**. Other Quest models and unrooted headsets are not supported."),
            paragraph("This guide follows the Hub's menu and button names. Click underlined text for links, "
                      "or use the PDF bookmarks to jump to a topic.", SMALL)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit", type=Path, help="Write actual page headings for review")
    args = parser.parse_args()
    match = re.search(r"^# QproFaceTracking (V\d+\.\d+\.\d+)", REFERENCE.read_text(encoding="utf-8"))
    if not match:
        raise ValueError("The text guide must declare a three-part QproFaceTracking version")
    version = match[1]
    pages = sections(SOURCE.read_text(encoding="utf-8"))
    titles = ["QproFaceTracking"] + [item["title"] for item in pages]
    if len(set(map(slug, titles))) != len(titles):
        raise ValueError("Page headings must be unique for internal links")
    story = cover(version)
    for i, section in enumerate(pages, 2):
        story.append(PageBreak())
        story.extend(page_content(section, i))
    doc = GuideDoc(version, titles)
    doc.build(story)
    if doc.page != len(titles):
        raise ValueError(f"Expected {len(titles)} guide pages, got {doc.page}; review overflow")
    if args.audit:
        args.audit.parent.mkdir(parents=True, exist_ok=True)
        args.audit.write_text(json.dumps({"version": version, "pages": doc.page, "headings": doc.records}, indent=2), encoding="utf-8")
    print(f"Built {doc.page} pages: {OUTPUT}")


if __name__ == "__main__":
    main()
