# בקשת חתימת קוד — SignPath Foundation

**סטטוס (14.10.2026): נדחה.** SignPath Foundation דחו את הבקשה — התוכנית מיועדת לפרויקטים עם
כבר "אות אמון ציבורי" (כוכבים/פיצולים/תורמים ב-GitHub, אזכורים חיצוניים, פעילות מתמשכת),
שעדיין אין לפרויקט הזה מספיק מהם. לא פסילה איכותית — אפשר להגיש שוב כשתהיה יותר חשיפה
ציבורית. חלופה זמינה כבר עכשיו: מנוי בתשלום רגיל של SignPath (לא דרך תוכנית ה-Foundation
החינמית) — https://docs.signpath.io/change-subscription. בלי חתימה: ה-installer עדיין
עובד לגמרי, רק Windows SmartScreen יציג אזהרת "מו"פ לא מוכר" שדורשת לחיצת "עוד מידע ← הפעל
בכל זאת" — נפוץ מאוד בתוכנות קוד-פתוח קטנות, לא חוסם התקנה.

מגישים בטופס: https://signpath.org/apply.html (המתחזקת, מחשבון GitHub שלה; קודם לוודא שיש ב-GitHub אימות דו-שלבי).

| שדה | תשובה |
|---|---|
| Project name | Otzaria System Tools |
| Homepage | https://github.com/YairDaniel11/otzaria-system-tools |
| Third-party components | pdf.js (Apache-2.0), JSZip (MIT) |
| Repository | https://github.com/YairDaniel11/otzaria-system-tools |
| License | MIT (קובץ LICENSE בריפו) |
| Description | A local Windows bridge and two plugins for Otzaria, a free open-source Jewish texts library: file explorer, system settings and an optional study-only lock mode. |
| Artifacts to sign | OtzariaBridge.exe (single-file .NET 8, win-x64) and the Inno Setup installer (OtzariaBridge-Setup-*.exe) built from it, built by GitHub Actions (.github/workflows/build.yml) |
| Build system | GitHub Actions, windows-latest, dotnet publish + Inno Setup |
| Releases | https://github.com/YairDaniel11/otzaria-system-tools/releases |
| Why | Users get SmartScreen warnings; the bridge installs per-user and auto-starts, so a trusted signature matters. |

## דרישות SignPath שצריך לוודא לפני ההגשה
- ✅ רישיון MIT בריפו (LICENSE); רכיבי צד שלישי: pdf.js (Apache-2.0), JSZip (MIT).
- הבנייה רק ב-CI (לא מהמחשב האישי) — כבר כך.
- דף פרויקט (README) שמסביר מה התוכנה עושה וכולל מדיניות פרטיות קצרה (הגשר לא שולח מידע; דיווח תקלות רק דרך feedback.report באישור).
- כל חברי הצוות עם הרשאת כתיבה משתמשים ב-2FA ב-GitHub.

## אחרי אישור
SignPath נותנים organization id, project slug ו-API token. מוסיפים כסודות בריפו (SIGNPATH_API_TOKEN וכו') ושלב `signpath/github-action-submit-signing-request` ב-build.yml אחרי ה-publish.
