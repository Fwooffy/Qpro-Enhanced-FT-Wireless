"""Build the Qpro Enhanced Face Tracking PDF from the current beginner guide."""

from __future__ import annotations

import html
import re
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.platypus import (
    HRFlowable,
    KeepTogether,
    LongTable,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "RELEASE_INSTRUCTIONS.md"
OUTPUT = ROOT / "Quest_Pro_Enhanced_Face_Tracking_Guide.pdf"

PAGE_W, PAGE_H = A4
DEEP = colors.HexColor("#213942")
PANEL = colors.HexColor("#EDF5F4")
ACCENT = colors.HexColor("#177F83")
TEXT = colors.HexColor("#20323A")
MUTED = colors.HexColor("#52676B")
LINE = colors.HexColor("#C9DAD8")
WHITE = colors.white
ORANGE = colors.HexColor("#FFF1DC")

BODY = ParagraphStyle("body", fontName="Helvetica", fontSize=9.5, leading=12.9,
                      textColor=TEXT, spaceAfter=4.5, allowWidows=0, allowOrphans=0)
SECTION_LEAD = ParagraphStyle("section lead", parent=BODY, keepWithNext=True)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=8.4, leading=11.3)
TITLE = ParagraphStyle("title", parent=BODY, fontName="Helvetica-Bold", fontSize=23,
                       leading=27, textColor=DEEP, spaceAfter=9)
SUBTITLE = ParagraphStyle("subtitle", parent=BODY, fontSize=10.2, leading=14,
                          textColor=MUTED, spaceAfter=13)
H2 = ParagraphStyle("section", parent=BODY, fontName="Helvetica-Bold", fontSize=14,
                    leading=18, textColor=DEEP, spaceBefore=11, spaceAfter=6,
                    keepWithNext=True)
H3 = ParagraphStyle("subsection", parent=BODY, fontName="Helvetica-Bold", fontSize=10.8,
                    leading=14, textColor=ACCENT, spaceBefore=8, spaceAfter=4,
                    keepWithNext=True)
STEP_NO = ParagraphStyle("step number", parent=BODY, fontName="Helvetica-Bold",
                         fontSize=10, textColor=ACCENT, spaceAfter=0)
TABLE_HEAD = ParagraphStyle("table head", parent=SMALL, fontName="Helvetica-Bold",
                            textColor=WHITE, spaceAfter=0)
TABLE_BODY = ParagraphStyle("table body", parent=SMALL, spaceAfter=0)
CALLOUT = ParagraphStyle("callout", parent=BODY, fontSize=9.4, leading=13.8, spaceAfter=0)


def normalized(text: str) -> str:
    return (text.replace("\u2011", "-").replace("\u2013", "-")
            .replace("\u2014", "-").replace("\u2018", "'").replace("\u2019", "'")
            .replace("\u201c", '"').replace("\u201d", '"')
            .replace("\u2026", "...").replace("\u00d7", "x"))


TOKEN = re.compile(r"\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|`([^`]+)`|\*([^*]+)\*")


def inline(text: str) -> str:
    text = normalized(text)
    output: list[str] = []
    pos = 0
    for match in TOKEN.finditer(text):
        output.append(html.escape(text[pos:match.start()]))
        if match.group(1) is not None:
            label, url = match.group(1), match.group(2)
            label = html.escape(label)
            if url.startswith("#"):
                output.append(f"<b>{label}</b>")
            else:
                output.append(f'<link href="{html.escape(url, quote=True)}" color="#177F83"><u>{label}</u></link>')
        elif match.group(3) is not None:
            output.append(f"<b>{html.escape(match.group(3))}</b>")
        elif match.group(4) is not None:
            output.append(f'<font face="Courier">{html.escape(match.group(4))}</font>')
        else:
            output.append(f"<i>{html.escape(match.group(5))}</i>")
        pos = match.end()
    output.append(html.escape(text[pos:]))
    return "".join(output)


def callout(text: str, background=PANEL):
    table = Table([[Paragraph(inline(text), CALLOUT)]], colWidths=[PAGE_W - 101])
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), background),
        ("BOX", (0, 0), (-1, -1), 0.55, LINE),
        ("LEFTPADDING", (0, 0), (-1, -1), 12),
        ("RIGHTPADDING", (0, 0), (-1, -1), 12),
        ("TOPPADDING", (0, 0), (-1, -1), 10),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 10),
    ]))
    return table


def step(number: str, text: str):
    row = Table([[Paragraph(number, STEP_NO), Paragraph(inline(text), BODY)]],
                colWidths=[21, PAGE_W - 121], hAlign="LEFT")
    row.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (0, 0), 0),
        ("RIGHTPADDING", (0, 0), (0, 0), 0),
        ("LEFTPADDING", (1, 0), (1, 0), 0),
        ("RIGHTPADDING", (1, 0), (1, 0), 0),
        ("TOPPADDING", (0, 0), (-1, -1), 1),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 1),
    ]))
    return row


def issue_table(rows: list[list[str]]):
    formatted = []
    for index, row in enumerate(rows):
        style = TABLE_HEAD if index == 0 else TABLE_BODY
        formatted.append([Paragraph(inline(cell), style) for cell in row])
    table = LongTable(formatted, colWidths=[174, PAGE_W - 274], repeatRows=1,
                      hAlign="LEFT")
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), DEEP),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [WHITE, PANEL]),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.4, LINE),
        ("LEFTPADDING", (0, 0), (-1, -1), 8),
        ("RIGHTPADDING", (0, 0), (-1, -1), 8),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]))
    return table


def page_frame(canvas, doc):
    canvas.saveState()
    canvas.setFillColor(DEEP)
    canvas.rect(0, PAGE_H - 49, PAGE_W, 49, fill=1, stroke=0)
    canvas.setFillColor(WHITE)
    canvas.setFont("Helvetica-Bold", 9)
    canvas.drawString(50, PAGE_H - 29, "QPRO ENHANCED FACE TRACKING")
    canvas.setFont("Helvetica", 8)
    canvas.drawRightString(PAGE_W - 50, PAGE_H - 29, "BEGINNER GUIDE")
    canvas.setStrokeColor(LINE)
    canvas.line(50, 39, PAGE_W - 50, 39)
    contact = "Community Discord: discord.gg/ghvuJTpRu4"
    canvas.setFillColor(MUTED)
    canvas.setFont("Helvetica", 8)
    canvas.drawString(50, 25, contact)
    canvas.linkURL("https://discord.gg/ghvuJTpRu4",
                   (50, 21, 50 + stringWidth(contact, "Helvetica", 8), 36), relative=0)
    canvas.drawRightString(PAGE_W - 50, 25, str(doc.page))
    canvas.restoreState()


def story_from_guide(markdown: str):
    story = [
        Spacer(1, 5),
        Paragraph("Quest Pro enhanced face tracking", TITLE),
        Paragraph("V2.1.2 release candidate  |  USB or wireless ADB  |  8 October 2026", SUBTITLE),
        callout("**A rooted Meta Quest Pro and the latest VRCFaceTracking from Steam are required.** "
                "If you used a version before V2.0, record and train a new tongue model. "
                "Working V2.0 through V2.0.2 models can be exported and imported into V2.1.2. "
                "Developer v8 remains the default. Mustachio is optional and highly experimental.", ORANGE),
        Spacer(1, 10),
        Paragraph('Based on <link href="https://github.com/n0tmast3r/Qpro-Enhanced-FT/releases" color="#177F83"><u>Qpro-Enhanced-FT by n0tmast3r</u></link>. '
                  'Fwooffy maintains the <link href="https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless" color="#177F83"><u>AMD/NVIDIA wireless edition</u></link>. '
                  'Fwooffy wrote the original <link href="https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md" color="#177F83"><u>Quest Pro root guide</u></link>; glorpette made and hosts the linked GitHub version.', SMALL),
        HRFlowable(width="100%", thickness=0.8, color=LINE, spaceBefore=4, spaceAfter=7),
    ]

    lines = markdown.splitlines()
    in_intro_quote = False
    gaze_fallback_note = None
    index = 0
    while index < len(lines):
        line = lines[index].strip()
        if index == 0 and line.startswith("# "):
            index += 1
            continue
        if line.startswith(">"):
            in_intro_quote = True
            index += 1
            continue
        if in_intro_quote and not line:
            in_intro_quote = False
            index += 1
            continue
        if not line:
            index += 1
            continue
        if line.startswith("If Independent Eye Gaze fails to start or stops unexpectedly"):
            gaze_fallback_note = line
            index += 1
            continue
        if line.startswith("## "):
            heading = Paragraph(html.escape(normalized(line[3:])), H2)
            if line == "## Common problems" and gaze_fallback_note:
                story.append(KeepTogether([
                    heading, Spacer(1, 2), callout(gaze_fallback_note), Spacer(1, 7)
                ]))
            else:
                story.append(heading)
        elif line.startswith("### "):
            story.append(Paragraph(html.escape(normalized(line[4:])), H3))
        elif line.startswith("| "):
            rows = []
            while index < len(lines) and lines[index].strip().startswith("|"):
                cells = [cell.strip() for cell in lines[index].strip().strip("|").split("|")]
                if not all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells):
                    rows.append(cells)
                index += 1
            story.append(issue_table(rows))
            story.append(Spacer(1, 8))
            continue
        elif match := re.match(r"^(\d+)\.\s+(.*)", line):
            row = step(match.group(1) + ".", match.group(2))
            # Keep the first two setup steps together so the required workflow
            # does not start with one short item at the foot of a page.
            if line.startswith("1. **Connect your Quest Pro:**"):
                row.keepWithNext = True
            story.append(row)
        elif line.startswith("- "):
            story.append(Paragraph("<font color='#177F83'><b>-</b></font>  " + inline(line[2:]), BODY))
        elif line.startswith(("The Hub's gaze method can fail", "**Preview tracking cameras**",
                              "Eyebrows use the selected source's live face values",
                              "The window saves your profile after all three poses pass",
                              "Qpro installs [AMD TheRock ROCm ", "**AMD GPU:**")):
            story.append(KeepTogether([Paragraph(inline(line), BODY)]))
        else:
            style = SECTION_LEAD if line.startswith((
                "On **First-time setup**, work down",
                "**Train if you have no personal model",
            )) else BODY
            story.append(Paragraph(inline(line), style))
        index += 1

    story.extend([
        Spacer(1, 9),
        Paragraph("Tested PC example", H3),
        Paragraph("The owner reported good performance with few issues on Windows 11 Pro, "
                  "an Intel Core i7-13700KF, AMD Radeon RX 7900 XTX, and 32 GB RAM. "
                  "This is an experience report, not a benchmark or a guarantee for other PCs.", SMALL),
    ])
    return story


def main():
    doc = SimpleDocTemplate(
        str(OUTPUT), pagesize=A4, rightMargin=50, leftMargin=50,
        topMargin=65, bottomMargin=50, title="Quest Pro Enhanced Face Tracking V2.1.2 - Beginner Guide",
        author="Fwooffy", subject="Rooted Quest Pro face tracking setup",
        pageCompression=1,
    )
    doc.build(story_from_guide(SOURCE.read_text(encoding="utf-8")),
              onFirstPage=page_frame, onLaterPages=page_frame)
    print(OUTPUT)


if __name__ == "__main__":
    main()
