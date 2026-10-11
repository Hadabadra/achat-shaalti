# פרומפט להמשך עבודה (להדבקה בשיחה חדשה)

```
המשך כלי מערכת לאוצריא אחרי גרסה 2.0.0.
ריפו: e0548433917-gif/otzaria-system-tools (ענף main).

התחלה:
- שכפל עם --depth 1 וקרא docs/ROADMAP.md (הסעיף "להמשך"), docs/PROGRESS.md ו-docs/KNOWLEDGE.md.
- בנייה מקומית של הגשר: ראו KNOWLEDGE.md ("קומפילציה מקומית").

כללים:
- קומיטים רק בשם Claude <noreply@anthropic.com>; בלי השם או האימייל של המשתמשת.
- CSS רק עם טוקנים ו-em; בלי font-family: inherit. תיאור במניפסט עד 150 תווים.
- דחיפה ל-main עם שינוי ב-bridge/, system-settings/, file-explorer/ או CHANGELOG.md בונה; גרסה חדשה ב-OtzariaBridge.csproj = Release אוטומטי מהסעיף העליון ב-CHANGELOG.
- פרסום לחנות: Actions → Run workflow → store=true (רק באישור).
- להעלות גרסה בכל המקומות: bridge/OtzariaBridge.csproj, bridge/Program.cs (Version), שני manifest.json, SS.VERSION ב-system-settings/js/app.js, FX.VERSION ב-file-explorer/js/app.js, והגרסה בגשר המדומה (system-settings/js/bridge.js).
```
