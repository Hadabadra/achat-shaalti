# תהליך אריזה ושחרור — לקחים ומלכודות

## סטטוס 3.5.0 (13.10.2026) — פתוח
- הגרסה, ה-CHANGELOG והתיקונים נמצאים ב-main וב-next (commit `b14b69f`). ה-CI ירוק (ריצה 38335895779).
- המהדורה `bridge-v3.5.0` פורסמה **בלי קבצים מצורפים**, ונמחקה ידנית. התג `bridge-v3.5.0` קיים ומצביע על `b14b69f`.
- **לתיקון בפעם הבאה:** להריץ את ה-workflow ידנית על main עם `store=false` (Actions → Run workflow, או `gh workflow run build.yml --ref main -f store=false`).
  המהדורה כבר לא קיימת, ולכן שלב Release ייצור אותה מחדש עם `OtzariaBridge.exe`, שני קובצי ה-`.otzplugin` ו-`SHA256SUMS.txt`. לבדוק שהקבצים אכן מצורפים.

## מלכודות שנתגלו
1. **`check-prerelease.py` קרס ב-Windows CI** (UnicodeEncodeError, cp1252) כשהדפיס עברית. תוקן: הפלט מוגדר ל-UTF-8 בתחילת הסקריפט.
2. **התיאור הקצר ב-manifest מוגבל ל-150 תווים** (ולידציית החנות). לבדוק את האורך לפני bump.
   בדיקה: `python -c "import json;print(len(json.load(open('file-explorer/manifest.json',encoding='utf-8'))['description']))"`
3. **מסנן הנתיבים ב-build.yml** לא כולל את `tools/`. שינוי שנוגע רק ב-`tools/` לא מפעיל CI, וצריך להריץ ידנית (workflow_dispatch).
4. **המהדורה נוצרת אוטומטית מדחיפה ל-main** כשה-`<Version>` ב-csproj חדש. אם הריצה הראשונה שיצרה את המהדורה לא צירפה קבצים, ריצות הבאות לא מעדכנות אותה, כי שלב ההשלמה פועל רק על טיוטה.
   פתרון: למחוק את המהדורה (לא את התג) ולהריץ שוב את ה-workflow, או להעביר את המהדורה לטיוטה ולהריץ שוב.
5. **מגבלות בסשנים של Claude בענן:** דחיפת תגים ב-git מחזירה 403, יצירה, עריכה ומחיקה של מהדורות חסומות, `gh` עם GraphQL חסום (צריך `gh api` ב-REST), והעלאה של קבצים בינאריים חסומה.
   את שלב המהדורה צריך להשאיר ל-CI או לעשות ידנית.

## סדר מומלץ לשחרור הבא
1. `python tools/check-prerelease.py`
2. bump ב-`bridge/OtzariaBridge.csproj` וב-`*/manifest.json`, ובדיקת אורך התיאור הקצר (≤150).
3. סעיף חדש ב-CHANGELOG מתוך `git log --no-merges main..next`.
4. commit, ‏push ל-next, ‏fast-forward ל-main. ה-CI יוצר את התג ואת המהדורה בעצמו, ואין צורך לדחוף תג ידנית.
5. לוודא שהמהדורה נוצרה עם 4 הקבצים, ורק אז לשקול פרסום לחנות (`store=true`, ידני).
