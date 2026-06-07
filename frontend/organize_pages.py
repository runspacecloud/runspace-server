from pathlib import Path
import shutil

root = Path(".")

skip = {
    "index.html",
}

html_files = [
    p for p in root.glob("*.html")
    if p.name not in skip and ".bak" not in p.name
]

for html in html_files:
    name = html.stem

    folder = root / name
    folder.mkdir(exist_ok=True)

    target = folder / "index.html"

    if target.exists():
        backup = folder / "index.old.html"
        print(f"{target} exists, backing it up to {backup}")
        shutil.move(str(target), str(backup))

    print(f"Moving {html} -> {target}")
    shutil.move(str(html), str(target))

    css = folder / "style.css"
    js = folder / "script.js"

    css.touch(exist_ok=True)
    js.touch(exist_ok=True)

print("Done.")
