# מתווך דיווחי תקלות (#65)

הגשר שולח דיווחי תקלות (אחרי הסתרת שם משתמש ושמות קבצים) לכתובת שבהגדרה `diag.relayUrl`. המתווך פותח issue בריפו עם התווית `auto-report`. הטוקן נשמר רק במתווך. בגשר אין טוקן.

## הקמה (פעם אחת, כ-10 דקות, חינם)
1. **טוקן GitHub:** ב-github.com → Settings → Developer settings → Fine-grained tokens → Generate.
   - Repository access: רק `achat-shaalti`.
   - Permissions → Issues: **Read and write**. לא לסמן שום הרשאה נוספת.
   - תוקף: שנה. להעתיק את הטוקן.
2. **תווית:** בריפו → Issues → Labels → New label בשם `auto-report`.
3. **Cloudflare:** להירשם ב-dash.cloudflare.com (חינם).
   - Workers & Pages → Create → Worker → שם `achat-relay` → Deploy.
   - Edit code: למחוק הכול, להדביק את `worker.js`, ואז Deploy.
4. **KV:** Storage & Databases → KV → Create namespace בשם `relay`.
   - ב-Worker → Settings → Bindings → Add → KV namespace: Variable name `RELAY_KV`, לבחור `relay`.
5. **משתנים** (Worker → Settings → Variables and Secrets):
   - `GH_REPO` (Text) = `e0548433917-gif/achat-shaalti`
   - `GH_TOKEN` (**Secret**) = הטוקן משלב 1
6. להעתיק את כתובת ה-Worker (`https://achat-relay.<שם>.workers.dev`) ולמסור אותה ל-Claude. היא תוכנס כברירת מחדל ל-`diag.relayUrl` בגשר.
   עד אז אפשר להוסיף ידנית ל-`%LOCALAPPDATA%\OtzariaBridge\config.json`: `"diag.relayUrl": "https://..."`.

## בדיקה
```
curl -X POST https://achat-relay.<שם>.workers.dev -H "content-type: application/json" \
  -d '{"title":"test","body":"test","fingerprint":"0123456789abcdef","version":"3.9.0"}'
```
אמור לחזור `ok` ולהיפתח issue. שליחה חוזרת תחזיר `duplicate`.

## הגנות
- 5 דיווחים לשעה לכל IP, ו-30 ליום בסך הכול.
- כפילות לפי fingerprint חסומה ל-7 ימים.
- גוף עד 8000 תווים, ו-`@` מנוטרל כדי שלא יהיו תיוגים.
- משתמש יכול לכבות את הדיווח האוטומטי (`diag.autoReport`). זה מכבה גם את השליחה לכאן.
