"""Convert a BoldDesk canned-responses export into a Klippy import file.

Expects a folder containing:
  - <id>.md               one markdown file per canned response
  - Canned Responses.xlsx  columns: id | Title | Access Scope | Owner | Usage Count

Titles come from the spreadsheet (the .md files hold body text only). The numeric
filename becomes ExternalId and, together with Source, lets a later re-import update
snippets in place instead of duplicating them.

    python tools/bolddesk_to_klippy.py "<folder>" out.json --source "BoldDesk Aug 2026"

Then merge it in with:  dotnet run --project tools/Importer -- out.json
"""
import argparse, collections, datetime, json, os, re

import openpyxl  # pip install openpyxl


def build(folder: str, source: str) -> list[dict]:
    book = os.path.join(folder, "Canned Responses.xlsx")
    rows = list(openpyxl.load_workbook(book, data_only=True).worksheets[0].iter_rows(values_only=True))[1:]
    meta = {int(r[0]): {"title": str(r[1]).strip(), "usage": int(r[4] or 0)}
            for r in rows if r[0] is not None}

    files = {int(m.group(1)): f for f in os.listdir(folder)
             if (m := re.fullmatch(r"(\d+)\.md", f))}

    missing_title = sorted(set(files) - set(meta))
    missing_file = sorted(set(meta) - set(files))
    if missing_title:
        print(f"warning: no spreadsheet row for {missing_title} - skipped")
    if missing_file:
        print(f"warning: no .md file for {missing_file} - skipped")

    # There is no real "last used" date in the export, but Usage Count is real signal.
    # Order the batch by it and place it a day back, so heavily-used replies rank high
    # without any of them outranking snippets the user actually used today.
    base = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=1)
    usable = sorted(set(files) & set(meta))
    rank = {ext: n for n, ext in enumerate(
        sorted(usable, key=lambda i: (-meta[i]["usage"], meta[i]["title"].lower())))}

    out = []
    for ext in usable:
        content = open(os.path.join(folder, files[ext]), encoding="utf-8").read().strip()
        if not content:
            print(f"warning: {files[ext]} is empty - skipped")
            continue
        stamp = (base - datetime.timedelta(seconds=rank[ext])).isoformat()
        out.append({
            "Label": meta[ext]["title"], "Content": content,
            "Tag": "", "QuickCode": "", "IsMarkdown": True,
            "Source": source, "ExternalId": str(ext),
            "CreatedAt": stamp, "LastUsedAt": stamp,
        })
    return out


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("output")
    ap.add_argument("--source", required=True, help='e.g. "BoldDesk Aug 2026"')
    a = ap.parse_args()

    snippets = build(a.folder, a.source)
    json.dump(snippets, open(a.output, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    dupes = [k for k, n in collections.Counter(s["ExternalId"] for s in snippets).items() if n > 1]
    assert not dupes, f"duplicate external ids: {dupes}"
    print(f"wrote {len(snippets)} snippets to {a.output}")
