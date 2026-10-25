#!/usr/bin/env python3
"""Export the college's demo course spreadsheet to JSON, one file per sheet.

Usage:
    python tools/export-demo-data.py ["path/to/Appendix 2 - Demo Course Data.xlsx"]

Writes college-content/source-data/{sheet}.json (relative to the repo root).
Each file is an array of objects keyed by the sheet's header row.

Values are kept raw, exactly as the college stores them, because the
integration layer is what cleans them. That means:
  - strings stay as they are, including the literal text 'NULL', raw HTML
    and the '_x000D_' carriage-return artefacts;
  - numbers stay as numbers;
  - date-times become "yyyy-MM-dd HH:mm:ss" strings, dates "yyyy-MM-dd",
    and time-only cells "HH:mm:ss".

Skipped: columns with a blank header, fully empty rows, and rows whose first
cell is empty or the text 'NULL'.

Requires openpyxl (pip install openpyxl).
"""

import datetime as dt
import json
import os
import sys
from pathlib import Path

try:
    import openpyxl
except ImportError:
    sys.exit("openpyxl is not installed. Run: pip install openpyxl")

SHEETS = [
    "courses",
    "course_sections",
    "course_levels",
    "course_level_link",
    "course_funding",
    "course_fund_link",
]

DEFAULT_XLSX = os.path.join(
    os.path.expanduser("~"), "Downloads", "Appendix 2 - Demo Course Data.xlsx"
)

REPO_ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = REPO_ROOT / "college-content" / "source-data"


def to_json_value(value):
    """Convert a cell value to something JSON can hold, without cleaning it."""
    if isinstance(value, dt.datetime):
        return value.strftime("%Y-%m-%d %H:%M:%S")
    if isinstance(value, dt.date):
        return value.strftime("%Y-%m-%d")
    if isinstance(value, dt.time):
        return value.strftime("%H:%M:%S")
    if isinstance(value, dt.timedelta):
        return str(value)
    return value


def is_blank(value):
    return value is None or (isinstance(value, str) and value.strip() == "")


def export_sheet(ws):
    rows = ws.iter_rows(values_only=True)
    try:
        header = next(rows)
    except StopIteration:
        return []

    # (column index, header name) for every column that has a header
    columns = [
        (i, str(h).strip()) for i, h in enumerate(header) if not is_blank(h)
    ]

    records = []
    for row in rows:
        if row is None or all(is_blank(v) for v in row):
            continue
        first = row[0] if row else None
        if first is None or (isinstance(first, str) and first.strip() == "NULL"):
            continue
        record = {}
        for i, name in columns:
            record[name] = to_json_value(row[i]) if i < len(row) else None
        records.append(record)
    return records


def main(argv):
    xlsx = Path(os.path.expandvars(argv[1])) if len(argv) > 1 else Path(DEFAULT_XLSX)
    if not xlsx.is_file():
        sys.exit(f"Spreadsheet not found: {xlsx}")

    # Not read_only: read-only mode can report stale sheet dimensions and
    # skip trailing columns on some files.
    wb = openpyxl.load_workbook(xlsx, data_only=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    missing = [s for s in SHEETS if s not in wb.sheetnames]
    if missing:
        sys.exit(f"Missing sheet(s) in {xlsx.name}: {', '.join(missing)}")

    print(f"Reading {xlsx}")
    for name in SHEETS:
        records = export_sheet(wb[name])
        out = OUT_DIR / f"{name}.json"
        with out.open("w", encoding="utf-8", newline="\n") as f:
            json.dump(records, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print(f"  {name:<20} {len(records):>4} rows -> {out.relative_to(REPO_ROOT)}")


if __name__ == "__main__":
    main(sys.argv)
