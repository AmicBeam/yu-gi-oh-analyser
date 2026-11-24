"""Build WindBot's cards.cdb from the server's card database and expansion packs."""

import argparse
from contextlib import closing
from pathlib import Path
import shutil
import sqlite3
import tempfile
from zipfile import ZipFile


def merge_cards(base, expansions, deck, output):
    # Keep the existing database intact until both the merge and deck check succeed.
    with tempfile.TemporaryDirectory(dir=output.parent) as temp:
        work = Path(temp) / "cards.cdb"
        shutil.copyfile(base, work)
        with closing(sqlite3.connect(work)) as cards:
            for expansion in expansions:
                with ZipFile(expansion) as package:
                    for member in sorted(package.namelist()):
                        if not member.endswith(".cdb"):
                            continue
                        extension = Path(temp) / "extension.cdb"
                        extension.write_bytes(package.read(member))
                        cards.execute("ATTACH DATABASE ? AS extension", (str(extension),))
                        try:
                            with cards:
                                cards.execute("INSERT OR REPLACE INTO datas SELECT * FROM extension.datas")
                                cards.execute("INSERT OR REPLACE INTO texts SELECT * FROM extension.texts")
                        finally:
                            cards.execute("DETACH DATABASE extension")
            ids = {int(line) for line in deck.read_text(encoding="utf-8-sig").splitlines() if line.isdecimal()}
            known = {row[0] for row in cards.execute("SELECT datas.id FROM datas JOIN texts USING (id)")}
            if ids - known:
                raise ValueError(f"Missing card IDs: {sorted(ids - known)}")
            print(f"{len(known)} cards, all {len(ids)} distinct deck card IDs available")
        work.replace(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--deck", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("expansions", type=Path, nargs="+", help="YPKs in increasing priority order")
    args = parser.parse_args()
    merge_cards(args.base, args.expansions, args.deck, args.output)
