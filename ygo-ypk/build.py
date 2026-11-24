"""Build a YGOPro expansion package from a cards.json and its assets."""

import argparse
import json
import sqlite3
from contextlib import closing
from pathlib import Path
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile


def integer(card, key, default=0):
    value = card.get(key, default)
    if type(value) is not int or not -(1 << 63) <= value < (1 << 63):
        raise ValueError(f"Card {card.get('code')}: {key} must be a SQLite integer")
    return value


def card_rows(card):
    code = integer(card, "code")
    if not 0 < code <= 0xFFFFFFFF:
        raise ValueError(f"Invalid card code: {code}")

    name = card.get("name")
    desc = card.get("desc", "")
    strings = card.get("strings", [])
    if not isinstance(name, str) or not name.strip() or not isinstance(desc, str):
        raise ValueError(f"Card {code}: expected a name and a text description")
    if not isinstance(strings, list) or len(strings) > 16 or any(
        not isinstance(value, str) for value in strings
    ):
        raise ValueError(f"Card {code}: strings must contain at most 16 text entries")

    sets = card.get("setcode", [])
    if not isinstance(sets, list) or len(sets) > 4 or any(
        type(value) is not int or not 0 < value <= 0xFFFF for value in sets
    ):
        raise ValueError(f"Card {code}: setcode must contain up to four 16-bit set codes")
    setcode = sum(value << (16 * index) for index, value in enumerate(sets))
    if setcode >= 1 << 63:
        setcode -= 1 << 64  # SQLite INTEGER is signed, but the client reads the bits.

    card_type = integer(card, "type")
    defense = integer(card, "defense")
    if "linkMarker" in card:
        marker = integer(card, "linkMarker")
        if not card_type & 0x4000000 or not 0 <= marker <= 0x1EF or marker & 0x10:
            raise ValueError(f"Card {code}: invalid Link marker mask")
        defense = marker

    data = (
        code, integer(card, "ot", 1), integer(card, "alias"), setcode, card_type,
        integer(card, "attack"), defense, integer(card, "level"),
        integer(card, "race"), integer(card, "attribute"), integer(card, "category"),
    )
    return data, (code, name, desc, *strings, *([""] * (16 - len(strings))))


def load_cards(source, base_databases):
    cards = json.loads((source / "cards.json").read_text(encoding="utf-8"))
    if not isinstance(cards, list) or not cards or any(
        not isinstance(card, dict) for card in cards
    ):
        raise ValueError("cards.json must contain a nonempty list of card objects")

    rows = [card_rows(card) for card in cards]
    codes = [data[0] for data, _ in rows]
    if len(codes) != len(set(codes)):
        raise ValueError("cards.json contains duplicate card codes")

    for base in base_databases:
        uri = base.resolve().as_uri() + "?mode=ro"
        with closing(sqlite3.connect(uri, uri=True)) as db:
            for code in codes:
                if db.execute("SELECT 1 FROM datas WHERE id = ?", (code,)).fetchone():
                    raise ValueError(f"Card code {code} already exists in {base}")
    return rows


def write_database(path, rows):
    prompts = ", ".join(f"str{index} TEXT" for index in range(1, 17))
    text_values = ", ".join(["?"] * 19)
    with closing(sqlite3.connect(path)) as db, db:
        db.execute("""
            CREATE TABLE datas (
                id INTEGER PRIMARY KEY, ot INTEGER, alias INTEGER, setcode INTEGER,
                type INTEGER, atk INTEGER, def INTEGER, level INTEGER, race INTEGER,
                attribute INTEGER, category INTEGER
            )
        """)
        db.execute(f"""
            CREATE TABLE texts (
                id INTEGER PRIMARY KEY, name TEXT, desc TEXT, {prompts}
            )
        """)
        db.executemany(
            "INSERT INTO datas VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (data for data, _ in rows),
        )
        db.executemany(f"INSERT INTO texts VALUES ({text_values})", (text for _, text in rows))


def build(source, output, base_databases):
    rows = load_cards(source, base_databases)
    assets = [source / "README.md", source / "strings.conf"]
    for pattern in ("script/**/*.lua", "pics/**/*.jpg", "pics/**/*.png", "pack/**/*.ydk"):
        assets.extend(source.glob(pattern))

    output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=output.parent) as work:
        database = Path(work) / f"{source.name}.cdb"
        archive = Path(work) / "expansion.ypk"
        write_database(database, rows)
        with ZipFile(archive, "w", compression=ZIP_DEFLATED) as package:
            package.write(database, database.name)
            for asset in sorted(assets):
                if asset.is_file():
                    package.write(asset, asset.relative_to(source).as_posix())
        archive.replace(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="directory containing cards.json")
    parser.add_argument("output", type=Path, help="output .ypk file")
    parser.add_argument(
        "--base-cdb", type=Path, action="append", default=[],
        help="check for card code collisions against this CDB (repeatable)",
    )
    args = parser.parse_args()
    if args.output.suffix.lower() != ".ypk":
        parser.error("output must end in .ypk")
    try:
        build(args.source.resolve(), args.output, args.base_cdb)
    except (OSError, ValueError, sqlite3.Error, RuntimeError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")
    print(args.output)


if __name__ == "__main__":
    main()
