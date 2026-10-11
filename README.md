# כלי מערכת לאוצריא

> לעילוי נשמת הנערה הצדקנית **אסתר גיטה בת רבי מיכאל שלום הלוי שיבדלט"א** — תנצב"ה.

שלושה רכיבים שעובדים יחד, כדי שאפשר יהיה לעבוד עם המחשב מתוך [אוצריא](https://github.com/Otzaria/otzaria) — במיוחד במחשב לימוד נעול.

| רכיב | מה הוא עושה | תיקייה |
|---|---|---|
| **גשר אוצריא** (`OtzariaBridge.exe`) | תוכנה קטנה שרצה ברקע ב-Windows ומאזינה רק למחשב המקומי. נותנת לתוספים גישה לקבצים ולהגדרות המחשב, ומצב נעילה לאוצריא בלבד. | `bridge/` |
| **סייר קבצים** | עיון, העתקה, העברה, מחיקה, שינוי שם, חיפוש, וצפייה ב-PDF, Word, טקסט ותמונות — מתוך אוצריא. בלי הגשר: תיקיות שאישרתם בלבד. | `file-explorer/` |
| **הגדרות מערכת** | שמע, מסך, בלוטות׳, Wi-Fi, הפעלה וכיבוי, ומצב נעילה עם יציאה בסיסמה. | `system-settings/` |

## הורדה
[השחרור האחרון](https://github.com/e0548433917-gif/otzaria-system-tools/releases/latest): מורידים את `OtzariaBridge.exe` ומפעילים פעם אחת, ואז מתקינים מתוך אוצריא את שני קובצי ה-`.otzplugin`.

⚠️ גרסת ניסיון — עדיין לא נבדקה על מחשבי Windows אמיתיים.

## למפתחים
- דחיפה ל-`main`: בניית הגשר ב-CI ‏(`windows-latest`), ולידציה של שני התוספים מול ה-SDK של אוצריא, וצילומי מסך לסייר.
- העלאת `<Version>` ב-`bridge/OtzariaBridge.csproj` (ובמקומות שב-`docs/WORKPLAN.md`): ‏Release אוטומטי.
- פרסום לחנות אוצריא: Actions → Run workflow → `store`. דורש את הסודות `OTZARIA_USER` ו-`OTZARIA_PASSWORD`.
- מסמכים: `docs/WORKPLAN.md`, `docs/STATUS.md`, `docs/BRIDGE_API.md`, `docs/KNOWLEDGE.md`, `docs/SDK-998-OPPORTUNITIES.md`.

יומן שינויים: [CHANGELOG.md](CHANGELOG.md) · מפתח: חד בדרא
