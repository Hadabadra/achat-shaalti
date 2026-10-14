# שער אריזה: נכשל אם נשאר סעיף פתוח ("- [ ]") בסעיף "חובה לפני אריזה" ב-docs/ROADMAP.md
import sys, re
for _s in (sys.stdout, sys.stderr):
    try: _s.reconfigure(encoding="utf-8")
    except Exception: pass
s = open("docs/ROADMAP.md", encoding="utf-8").read()
m = re.search(r"^### ⛔ חובה לפני אריזה.*?(?=^## |\Z)", s, re.S | re.M)
if not m: sys.exit("לא נמצא הסעיף 'חובה לפני אריזה' ב-docs/ROADMAP.md")
open_items = [l.strip() for l in m.group(0).splitlines() if l.strip().startswith("- [ ]")]
if open_items:
    print(f"::error::{len(open_items)} סעיפים פתוחים ב'חובה לפני אריזה' — אין אריזה/שחרור עד שכולם [x]:")
    for l in open_items: print("  " + l)
    sys.exit(1)
print("שער אריזה: כל הסעיפים בוצעו ✅")
