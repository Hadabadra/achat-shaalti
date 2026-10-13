/* sdk-extras.js - הרשאות SDK של אוצריא 0.9.97+ (ROADMAP "חובה לפני אריזה").
   כל קריאה עטופה ב-try: באוצריא ישנה, או כשהמשתמש כיבה הרשאה, פשוט לא קורה כלום.
   משותף לשני התוספים (עותק זהה ב-system-settings/js/sdk-extras.js). */
(function () {
  'use strict';
  var O = window.Otzaria;
  var X = window.SdkX = {};
  var SELF = document.documentElement.getAttribute('data-plugin-id') || '';

  function call(m, a) { try { return O ? Promise.resolve(O.call(m, a || {})).catch(function () { return null; }) : Promise.resolve(null); } catch (e) { return Promise.resolve(null); } }
  function on(ev, fn) { try { if (O && O.on) O.on(ev, fn); } catch (e) { /* ignore */ } }
  function un(r) { return r && typeof r === 'object' && 'data' in r ? r.data : r; }
  X.call = call;

  /* התראות: notifications.system, ואם אין - הודעה בתוך האפליקציה */
  X.notify = async function (title, body) {
    var r = await call('notifications.system', { title: title, body: body || '' });
    if (r == null) await call('notifications.showInApp', { message: title + (body ? ' — ' + body : '') });
  };

  /* לוח: clipboard.read, ואם אין - ה-API של הדפדפן */
  X.readClipboard = async function () {
    var r = un(await call('clipboard.read', {}));
    var t = r && (r.text != null ? r.text : (typeof r === 'string' ? r : ''));
    if (!t && navigator.clipboard && navigator.clipboard.readText) { try { t = await navigator.clipboard.readText(); } catch (e) { t = ''; } }
    return (t || '').trim();
  };

  /* גופנים מקומיים (fonts.local.read) לצופה Word: מחזיר את הגופן הראשון מהרשימה שמותקן */
  var fontsCache = null;
  X.matchFont = async function (families) {
    if (!fontsCache) {
      var r = un(await call('fonts.listInstalled', {}));
      fontsCache = (Array.isArray(r) ? r : (r && r.fonts) || []).map(function (f) { return String(f.family || f.name || f).toLowerCase(); });
    }
    for (var i = 0; i < (families || []).length; i++) if (fontsCache.indexOf(String(families[i]).toLowerCase()) >= 0) return families[i];
    return null;
  };

  /* שינוי שפה/ערכת נושא חי (events.subscribe:settings.changed) */
  on('settings.changed', function (p) {
    if (window.SS && SS.syncOffline) SS.syncOffline(); /* מצב מנותק השתנה באוצריא — מסתירים/מחזירים רשת מיד */
    var lang = p && (p.locale || p.language || (p.settings && p.settings.locale));
    if (lang && window.I18N && window.I18N.set && (!window.I18N.pref || window.I18N.pref() === 'auto')) {
      window.I18N.set(/^en/i.test(lang) ? 'en' : 'he');
    }
  });

  /* מעקב אחר הרשאות שכובו (plugin.permissions_changed + app.getGrantedPermissions) */
  var granted = null;
  async function checkPerms(first) {
    var r = un(await call('app.getGrantedPermissions', {}));
    var list = Array.isArray(r) ? r : (r && r.permissions);
    if (!Array.isArray(list)) return;
    if (granted && !first) {
      var lost = granted.filter(function (p) { return list.indexOf(p) < 0; });
      if (lost.length) X.notify('הרשאה כובתה', 'חלק מהיכולות לא יעבדו: ' + lost.join(', ') + '. אפשר להפעיל מחדש בהגדרות התוספים של אוצריא.');
    }
    granted = list;
  }
  on('plugin.permissions_changed', function () { checkPerms(false); });
  checkPerms(true);

  /* קיצור מקלדת (app.shortcuts): Ctrl+Shift+E פותח את הסייר. ניתן לשינוי: storage 'shortcut.explorer' */
  X.registerShortcut = async function (id, keys, onCmd) {
    await call('app.registerShortcut', { id: id, keys: keys, shortcut: keys });
    on('app.command', function (p) { if (p && (p.id === id || p.command === id)) onCmd(); });
  };

  /* ספרים מתוסף בחיפוש הספרייה (library.books.provide): קובצי PDF/Word מתיקיות שנבחרו */
  X.setLibraryBooks = function (books) { return call('library.setProviderBooks', { books: books }); };
  X.onLibraryBookOpen = function (fn) { on('library.providerBook.openRequested', fn); };

  X.self = SELF;
})();
