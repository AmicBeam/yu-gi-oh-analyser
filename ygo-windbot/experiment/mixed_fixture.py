"""Supply the released Eradicator ID to an older local SRVPro2 fixture if needed."""

from pathlib import Path
import shutil
import sqlite3

CARD = 30316053
PREVIEW = 101307021
SCRIPT = f"c{CARD}.lua"


def add_card(database):
    with sqlite3.connect(database) as db:
        for table in ("datas", "texts"):
            fields = [row[1] for row in db.execute(f"PRAGMA table_info({table})") if row[1] != "id"]
            columns = ", ".join(fields)
            db.execute(f"INSERT OR IGNORE INTO {table}(id, {columns}) SELECT ?, {columns} FROM {table} WHERE id=?",
                       (CARD, PREVIEW))
            if not db.execute(f"SELECT 1 FROM {table} WHERE id=?", (CARD,)).fetchone():
                raise ValueError(f"{database} contains neither Eradicator ID")


def with_eradicator(source, dest):
    if (source / "ygopro" / "script" / SCRIPT).is_file():
        return source, "native"
    dest.mkdir()
    for name in ("Dialogs", "data", "dist", "node_modules", "resource"):
        (dest / name).symlink_to((source / name).resolve())
    ygopro = dest / "ygopro"
    ygopro.mkdir()
    for child in (source / "ygopro").iterdir():
        if child.name not in ("script", "cards.cdb"):
            (ygopro / child.name).symlink_to(child.resolve())
    scripts = ygopro / "script"
    scripts.mkdir()
    for script in (source / "ygopro" / "script").iterdir():
        (scripts / script.name).symlink_to(script.resolve())
    for file in (dest / "cards.cdb", ygopro / "cards.cdb"):
        shutil.copy2(source / file.relative_to(dest), file)
        add_card(file)
    shutil.copy2(Path(__file__).resolve().parent / "fixtures" / SCRIPT, scripts / SCRIPT)
    return dest, "local-summon-negation-fixture"
