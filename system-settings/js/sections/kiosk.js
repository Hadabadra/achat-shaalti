/* sections/kiosk.js - lock mode (kiosk): password, recovery code, enable, disable, settings */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var MIN_PW = 4;

  SS.sections.push({
    id: 'kiosk', title: 'מצב נעילה', icon: 'lock',
    mount: function (body) { body.innerHTML = ui.loading('טוען מצב נעילה…'); },
    load: load
  });

  async function load(body) {
    var st;
    try { st = await SS.bridge.call('/kiosk/status'); }
    catch (e) { body.innerHTML = ui.errorBox(e); return; }
    if (SS.bridge.health) { SS.bridge.health.kioskActive = !!st.active; SS.setStatus(true); if (SS.syncLock) SS.syncLock(SS.bridge.health); }
    var progs = [], found = [];
    try {
      var pr = await SS.bridge.call('/kiosk/programs');
      progs = pr.programs || [];
      st.autoTorah = !!pr.autoTorah;
    } catch (e) { progs = []; }
    try { found = (await SS.bridge.call('/kiosk/programs/detect')).found || []; } catch (e) { found = []; }
    found = found.filter(function (f) {
      return !progs.some(function (p) { return p.path.toLowerCase() === f.path.toLowerCase(); });
    });
    var pairs = [];
    try { pairs = (await SS.bridge.call('/pair/list', {})).clients || []; } catch (e) { pairs = []; }
    render(body, st, progs, found, pairs);
  }

  function render(body, st, progs, found, pairs) {
    var active = !!st.active;
    var html =
      '<div class="status-card ' + (active ? 'active' : '') + '">' +
      '<div class="status-ic">' + ui.icon(active ? 'lock' : 'unlock') + '</div>' +
      '<div class="status-text"><span class="eyebrow">מצב נעילה</span><h3>' + (active ? 'פעיל' : 'לא פעיל') + '</h3>' +
      '<p>' + (active
        ? 'המחשב נעול לאוצריא בלבד' + (st.strict ? ' · נעילה קפדנית' : '') + (st.blockTaskManager ? ' · מנהל המשימות חסום' : '') + '.'
        : 'המחשב פועל כרגיל. אפשר להפעיל נעילה כדי שישמש ללימוד בלבד.') + '</p></div>' +
      (st.hasPassword
        ? (active
          ? '<button class="btn btn-lg btn-on-status" data-act="disable">' + ui.icon('unlock') + 'ביטול נעילה</button>'
          : '<button class="btn btn-lg btn-primary" data-act="enable">' + ui.icon('lock') + 'הפעלת נעילה</button>')
        : '') +
      '</div>';

    html += '<div class="card explain"><div class="card-title">מה עושה מצב נעילה?</div><ul class="feature-list">' +
      '<li>' + ui.icon('shield') + '<span><b>רק אוצריא פתוחה.</b> אם מישהו סוגר אותה — היא נפתחת מחדש מעצמה ונשארת בחזית.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>שורת המשימות ותפריט התחל מוסתרים,</b> כך שאי אפשר לפתוח תוכנות אחרות.</span></li>' +
      '<li>' + ui.icon('key') + '<span><b>מקשי מעבר חסומים:</b> Win, ‏Alt+Tab, ‏Alt+F4, ‏Ctrl+Esc ומנהל המשימות.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>יציאה רק עם סיסמה</b> — מכאן, מתוך התוסף. הנעילה חוזרת אוטומטית גם אחרי הפעלה מחדש.</span></li>' +
      '</ul></div>';

    html += '<div class="card explain"><div class="card-title">מה קורה בכיבוי, בהפעלה מחדש ובחשבון Windows אחר?</div><ul class="feature-list">' +
      '<li>' + ui.icon('lock') + '<span><b>כיבוי או הפעלה מחדש:</b> כברירת מחדל הנעילה <b>נשמרת</b>. הגשר עולה עם Windows וננעל שוב, ואוצריא נפתחת. אם רוצים שבכיבוי הנעילה תתאפס, מסמנים "ביטול נעילה בכיבוי המחשב" בהגדרות הנעילה (לא פועל עם נעילה מוקשחת).</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>חשבון Windows אחר:</b> נעילה רגילה חלה רק בחשבון שבו הופעלה. מחשבון אחר אפשר לעצור את הגשר. <b>נעילה מוקשחת</b> (אופציונלית) מפעילה את הגשר גם בחשבונות אחרים וחוסמת בהם את מנהל המשימות.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>הכי בטוח לעמדת לימוד:</b> חשבון Windows <b>רגיל</b> (בלי הרשאות מנהל) ללא גישה להגדרות המחשב, שבו מפעילים את הנעילה; חשבון המנהל נשאר עם סיסמה נפרדת.</span></li>' +
      '<li>' + ui.icon('key') + '<span><b>חירום:</b> קיצור Ctrl+Alt+Shift+F12 פותח חלון קוד שחזור לביטול הנעילה כולה. בלי הקוד אי אפשר לבטל, כדי שלא יהיה קיצור שעוקף את הנעילה.</span></li>' +
      '</ul></div>';

    if ((st.warnings || []).indexOf('admin') >= 0) {
      html += '<div class="card"><div class="card-title">' + ui.icon('warning') + 'אזהרה: חשבון עם הרשאות מנהל</div>' +
        '<p class="muted">המחשב מופעל בחשבון Windows עם הרשאות מנהל, שממנו אפשר לעקוף את הנעילה. ' +
        'מומלץ ליצור חשבון משתמש <b>רגיל (ללא הרשאות מנהל)</b> לעמדת הלימוד, ולהפעיל בו את הנעילה.</p></div>';
    }

    html += '<div class="card subtle"><div class="card-title">זו נעילה "רכה" — להקשחה מלאה</div>' +
      '<p class="muted">הנעילה רצה בהרשאות המשתמש ומונעת שימוש מזדמן. מי שמבין במחשבים יכול לעקוף אותה. ' +
      'כדי שתעמוד גם מול ניסיון מכוון, השלימו שכבות שאינן תלויות בתוכנה:</p>' +
      '<ul class="feature-list">' +
      '<li>' + ui.icon('key') + '<span><b>סיסמת BIOS/UEFI</b> וביטול אתחול מ-USB — חוסם כניסה ל-Safe Mode ואתחול מהתקן חיצוני. נעשה ידנית בהגדרות ה-BIOS של כל מחשב; תוכנה אינה יכולה לקבוע אותה.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>חשבון משתמש רגיל</b> ללא הרשאות מנהל, וחשבון המנהל עם סיסמה שהתלמיד אינו יודע.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>סינון תוכן על המחשב עצמו</b> (למשל נטפרי), ולא רק ברמת הרשת — כך מעבר לרשת אחרת אינו עוקף את הסינון.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>Safe Mode:</b> במצב בטוח הגשר אינו עולה והנעילה אינה חלה. סיסמת BIOS שחוסמת את תפריט האתחול סוגרת גם את זה.</span></li>' +
      '</ul></div>';

    if (!st.hasPassword) {
      html += '<div class="card"><div class="card-title">שלב ראשון: הגדרת סיסמה</div>' +
        '<p class="muted">לפני הפעלת הנעילה צריך לבחור סיסמה. רק מי שיודע אותה יוכל לבטל את הנעילה או לשנות הגדרות.</p>' +
        '<form class="form-grid" data-form="setpw" novalidate>' +
        '<label class="field"><span class="field-label">סיסמה</span><input class="input" type="password" name="pw" autocomplete="new-password" dir="ltr"></label>' +
        '<label class="field"><span class="field-label">אימות סיסמה</span><input class="input" type="password" name="pw2" autocomplete="new-password" dir="ltr"></label>' +
        '<div class="form-msg" hidden></div>' +
        '<div class="row-actions"><button class="btn btn-primary btn-lg" type="submit">' + ui.icon('key') + 'שמירת סיסמה</button></div>' +
        '<p class="muted small">לפחות ' + MIN_PW + ' תווים. אחרי השמירה יוצג קוד שחזור חד-פעמי — חשוב לרשום אותו.</p>' +
        '</form></div>';
    } else {
      html += '<div class="card"><div class="card-title">סיסמה והגדרות</div>' +
        '<div class="setting-row"><div class="setting-text"><b>שינוי סיסמה</b><span class="muted">החלפת סיסמת הנעילה (נדרשת הסיסמה הנוכחית)</span></div>' +
        '<button class="btn btn-tonal" data-act="chpw">' + ui.icon('edit') + 'שינוי</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>מה פתוח בזמן נעילה</b><span class="muted">' +
        ui.esc((st.openSections || []).map(function (id) { return SECTION_NAMES[id] || id; }).join(', ') || 'רק תוכנות לימוד ואודות') +
        (st.readOnly ? ' · קבצים לקריאה בלבד' : '') + (st.clock !== false ? ' · שעון צף' : '') + '</span></div>' +
        '<button class="btn btn-tonal" data-act="settings">' + ui.icon('edit') + 'עריכה</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>קבצים שמותר לפתוח בזמן נעילה</b><span class="muted"><bdi dir="ltr">' +
        ui.esc((st.allowOpenExtensions || []).length ? st.allowOpenExtensions.map(function (x) { return '.' + x; }).join(', ') : '—') + '</bdi></span></div>' +
        '<button class="btn btn-tonal" data-act="settings2">' + ui.icon('edit') + 'עריכה</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>נתיב אוצריא</b><span class="muted path"><bdi dir="ltr">' + ui.esc(st.otzariaPath || 'זיהוי אוטומטי') + '</bdi></span></div></div>' +
        '</div>';
      html += programsCard(progs, found, !!st.autoTorah);
      html += fxCard();
      html += hardCard(st.hard || {});
      html += pairCard(pairs);
      html += '<div class="card subtle"><div class="card-title">שכחתם את הסיסמה?</div>' +
        '<p class="muted">בחלון „ביטול נעילה” אפשר להקליד גם את <b>קוד השחזור</b> שקיבלתם בהגדרת הסיסמה. במקרה חירום אפשר להריץ משורת הפקודה:</p>' +
        '<code class="cmd" dir="ltr">OtzariaBridge.exe --unlock &lt;קוד-שחזור&gt;</code>' +
        '<p class="muted small">אפשרות נוספת: הפעלה ב-Safe Mode, שבו הגשר אינו עולה והנעילה אינה חלה.</p></div>';
    }

    body.innerHTML = html;
    wire(body, st, progs, found);
    wireHard(body, st, pairs);
  }

  function wire(body, st, progs, found) {
    var f = body.querySelector('[data-form=setpw]');
    if (f) {
      setTimeout(function () { var i = f.querySelector('input'); if (i) i.focus(); }, 50);
      f.addEventListener('submit', async function (e) {
        e.preventDefault();
        var msg = f.querySelector('.form-msg');
        var pw = f.pw.value, pw2 = f.pw2.value;
        var bad = pw.length < MIN_PW ? 'הסיסמה חייבת להכיל לפחות ' + MIN_PW + ' תווים' : pw !== pw2 ? 'הסיסמאות אינן תואמות' : '';
        if (bad) { msg.textContent = bad; msg.hidden = false; return; }
        msg.hidden = true;
        var btn = f.querySelector('button[type=submit]');
        btn.disabled = true;
        try {
          var r = await SS.bridge.call('/kiosk/set-password', { newPassword: pw });
          if (r && r.recoveryCode) await showRecovery(r.recoveryCode);
          else ui.toast('הסיסמה נשמרה', 'success');
          load(body);
        } catch (err) {
          msg.textContent = ui.errText(err); msg.hidden = false; btn.disabled = false;
        }
      });
    }
    on('enable', function () { enable(body, st); });
    on('disable', function () { disable(body); });
    on('chpw', function () { changePw(body); });
    on('settings', function () { editSettings(body, st); });
    on('settings2', function () { editSettings(body, st); });
    on('prog-add', function () { addProgram(body, {}); });
    on('prog-auto', function () { toggleAuto(body, !!st.autoTorah); });
    found.forEach(function (f, i) {
      on('prog-found-' + i, function () { addProgram(body, f); });
    });
    progs.forEach(function (p) {
      on('prog-open-' + p.id, function () { openProgram(p); });
      on('prog-del-' + p.id, function () { removeProgram(body, p); });
    });

    function on(act, fn) {
      var b = body.querySelector('[data-act=' + act + ']');
      if (b) b.addEventListener('click', fn);
    }
  }

  function showRecovery(code) {
    var pretty = code.replace(/(.{4})(?=.)/g, '$1-');
    return ui.dialog({
      title: 'קוד השחזור שלכם', icon: 'key', tone: 'recovery', dismissible: false,
      body:
        '<p>זהו <b>קוד שחזור חד-פעמי</b>. הוא יוצג <b>רק עכשיו</b> ולא יהיה אפשר לראות אותו שוב.</p>' +
        '<div class="recovery-code" dir="ltr">' + ui.esc(pretty) + '</div>' +
        '<p>רשמו אותו על דף ושמרו במקום בטוח, <b>לא ליד המחשב</b>. אם תשכחו את הסיסמה, אפשר לבטל את הנעילה עם הקוד הזה בחלון „ביטול נעילה”, או מחלון פקודה:</p>' +
        '<code class="cmd" dir="ltr">OtzariaBridge.exe --unlock ' + ui.esc(code) + '</code>',
      fields: [{ name: 'ack', type: 'checkbox', label: 'רשמתי את הקוד' }],
      actions: [{ label: 'סיום', value: true, kind: 'primary' }],
      validate: function (v) { return v.ack ? '' : 'יש לסמן „רשמתי את הקוד” לפני הסגירה'; },
      onOpen: function (form) {
        var btn = form.querySelector('button[type=submit]');
        var cb = form.querySelector('input[name=ack]');
        btn.disabled = true;
        cb.addEventListener('change', function () { btn.disabled = !cb.checked; });
      }
    });
  }

  async function enable(body, st) {
    var v = await ui.dialog({
      title: 'הפעלת מצב נעילה', icon: 'lock',
      body: '<p>הקלידו את סיסמת הנעילה ובחרו אפשרויות.</p>',
      fields: [
        { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' },
        { name: 'tm', type: 'checkbox', label: 'חסום מנהל משימות', value: st.blockTaskManager !== false, hint: 'מונע סגירה של אוצריא דרך Ctrl+Shift+Esc' },
        { name: 'strict', type: 'checkbox', label: 'נעילה קפדנית (החלפת מעטפת Windows — מהכניסה הבאה)', value: false,
          hint: '<span class="warn-text">' + ui.icon('warning') + 'אוצריא תחליף את שולחן העבודה של Windows למשתמש זה. השתמשו רק אם רשמתם את קוד השחזור.</span>' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'המשך', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; }
    });
    if (!v) return;
    var summary = '<p>לאחר האישור:</p><ul class="bullets">' +
      '<li>שורת המשימות ותפריט התחל ייעלמו.</li><li>רק אוצריא תישאר פתוחה, ומקשי המעבר ייחסמו.</li>' +
      (v.tm ? '<li>מנהל המשימות ייחסם.</li>' : '') +
      (v.strict ? '<li><b>נעילה קפדנית:</b> מהכניסה הבאה ל-Windows תיפתח אוצריא במקום שולחן העבודה.</li>' : '') +
      '</ul><p>כדי לצאת — חזרו למסך זה ובחרו „ביטול נעילה”.</p>';
    var ok = await ui.dialog({
      title: 'לנעול את המחשב?', icon: 'warning', tone: v.strict ? 'danger' : '', body: summary,
      actions: [{ label: 'חזרה', value: false }, { label: 'נעל עכשיו', value: true, kind: v.strict ? 'danger' : 'primary' }],
      onSubmit: async function () {
        await SS.bridge.call('/kiosk/enable', { password: v.pw, strict: !!v.strict, blockTaskManager: !!v.tm }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('מצב נעילה הופעל', 'success'); load(body); }
  }

  async function disable(body) {
    var ok = await ui.dialog({
      title: 'ביטול מצב נעילה', icon: 'unlock',
      body: '<p>הקלידו את סיסמת הנעילה, או את קוד השחזור.</p>',
      fields: [{ name: 'pw', label: 'סיסמה או קוד שחזור', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'בטל נעילה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) {
        var pw = x.pw.trim();
        await SS.bridge.call('/kiosk/disable', { password: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){2}$/.test(pw) ? pw.replace(/-/g, '') : x.pw }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('מצב נעילה בוטל', 'success'); load(body); }
  }

  async function changePw(body) {
    var ok = await ui.dialog({
      title: 'שינוי סיסמת נעילה', icon: 'key',
      fields: [
        { name: 'old', label: 'סיסמה נוכחית', type: 'password', dir: 'ltr' },
        { name: 'n1', label: 'סיסמה חדשה', type: 'password', dir: 'ltr' },
        { name: 'n2', label: 'אימות סיסמה חדשה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.old) return 'יש להקליד את הסיסמה הנוכחית';
        if (x.n1.length < MIN_PW) return 'הסיסמה החדשה חייבת להכיל לפחות ' + MIN_PW + ' תווים';
        if (x.n1 !== x.n2) return 'הסיסמאות החדשות אינן תואמות';
        return '';
      },
      onSubmit: async function (x) {
        var r = await SS.bridge.call('/kiosk/set-password', { oldPassword: x.old, newPassword: x.n1 });
        return r || true;
      }
    });
    if (ok) {
      if (ok.recoveryCode) await showRecovery(ok.recoveryCode);
      ui.toast('הסיסמה עודכנה', 'success');
      load(body);
    }
  }

  /* ---------- נעילה מוקשחת (3.0.0, בעיה #1) ---------- */
  function hardCard(h) {
    var state = !h.installed ? 'לא מותקן' : !h.running ? 'מותקן אבל לא רץ' :
      (h.active ? 'פעיל — הנעילה מוקשחת' : 'מותקן, ממתין להפעלת הנעילה') + (h.version ? ' · גרסה ' + ui.esc(h.version) : '');
    var actions = !h.installed
      ? '<button class="btn btn-primary" data-act="hard-install">' + ui.icon('shield') + 'התקנה</button>'
      : (h.needsUpdate || !h.running ? '<button class="btn btn-tonal" data-act="hard-install">' + ui.icon('shield') + 'עדכון</button>' : '') +
        '<button class="btn btn-tonal" data-act="hard-settings">' + ui.icon('edit') + 'הגדרות</button>' +
        '<button class="btn btn-tonal" data-act="hard-remove">' + ui.icon('close') + 'הסרה</button>';
    return '<div class="card"><div class="card-title">נעילה מוקשחת (רכיב שירות)</div>' +
      '<p class="muted">רכיב אופציונלי שרץ ברקע בהרשאות מערכת. הוא שומר את הסיסמה ואת מצב הנעילה במקום שהתלמיד אינו יכול לשנות, ' +
      'מפעיל את הגשר מחדש אם נסגר (גם בחשבון Windows אחר), מחזיר את חסימת מנהל המשימות ועורך הרישום אם נמחקה, ויכול לחסום התקני אחסון USB. ' +
      'ההתקנה דורשת אישור מנהל פעם אחת.</p>' +
      '<div class="setting-row"><div class="setting-text"><b>מצב</b><span class="muted">' + state +
      (h.blockUsbStorage ? ' · אחסון USB חסום' : '') + '</span></div>' + actions + '</div>' +
      '<p class="muted small">הרכיב אינו משנה את הכניסה ל-Windows ואינו רץ במצב בטוח, כך שאינו יכול להשבית את המחשב. ' +
      'גם הוא אינו מחליף סיסמת BIOS. פרטים: docs/HARDLOCK.md במאגר.</p></div>';
  }

  function pairCard(pairs) {
    var rows = pairs.map(function (c) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(c.name || c.client) + '</b><span class="muted">חובר: ' +
        ui.esc((c.created || '').replace('T', ' ')) + ' · שימוש אחרון: ' + ui.esc((c.lastUsed || '').replace('T', ' ')) + '</span></div>' +
        '<button class="btn btn-tonal" data-act="unpair-' + ui.esc(c.id) + '">' + ui.icon('close') + 'ניתוק</button></div>';
    }).join('');
    return '<div class="card"><div class="card-title">תוספים מחוברים לגשר</div>' +
      '<p class="muted">מגרסה 3.0 רק תוסף שאושר בחלון של הגשר יכול להשתמש בו. תוכנה אחרת במחשב לא יכולה לגשת לקבצים ולהגדרות בלי אישור כזה.</p>' +
      (rows || '<p class="muted">אין תוספים מחוברים.</p>') + '</div>';
  }

  function wireHard(body, st, pairs) {
    function on(act, fn) { var b = body.querySelector('[data-act="' + act + '"]'); if (b) b.addEventListener('click', fn); }
    var h = st.hard || {};
    on('hard-install', async function () {
      var ok = await ui.dialog({
        title: h.installed ? 'עדכון רכיב ההקשחה' : 'התקנת נעילה מוקשחת', icon: 'shield',
        body: '<p class="muted">אחרי ההקלדה Windows תבקש אישור מנהל. אחרי האישור הרכיב יותקן ויפעל ברקע. הסיסמה וקוד השחזור הנוכחיים יישארו בתוקף.</p>',
        fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
        actions: [{ label: 'ביטול', value: false }, { label: h.installed ? 'עדכון' : 'התקנה', value: true, kind: 'primary' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/install', { password: x.pw }, { timeoutMs: 200000 }); return true; }
      });
      if (ok) { ui.toast('רכיב ההקשחה מותקן', 'success'); load(body); }
    });
    on('hard-settings', async function () {
      var ok = await ui.dialog({
        title: 'הגדרות נעילה מוקשחת', icon: 'shield',
        fields: [
          { name: 'usb', type: 'checkbox', label: 'חסימת התקני אחסון USB בזמן נעילה', value: !!h.blockUsbStorage, hint: 'דיסק-און-קי וכוננים חיצוניים לא ייטענו. עכבר, מקלדת ומדפסת ממשיכים לעבוד. התקן שכבר מחובר נשאר עד שמוציאים אותו' },
          { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }
        ],
        actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/settings', { password: x.pw, blockUsbStorage: !!x.usb }); return true; }
      });
      if (ok) { ui.toast('ההגדרות נשמרו', 'success'); load(body); }
    });
    on('hard-remove', async function () {
      var ok = await ui.dialog({
        title: 'הסרת רכיב ההקשחה', icon: 'close',
        body: '<p class="muted">הנעילה הרגילה תמשיך לעבוד. החסימות שהרכיב הוסיף יבוטלו.</p>',
        fields: [{ name: 'pw', label: 'סיסמת נעילה או קוד שחזור', type: 'password', dir: 'ltr' }],
        actions: [{ label: 'ביטול', value: false }, { label: 'הסרה', value: true, kind: 'danger' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/uninstall', { password: x.pw }); return true; }
      });
      if (ok) { ui.toast('רכיב ההקשחה הוסר', 'success'); load(body); }
    });
    (pairs || []).forEach(function (c) {
      on('unpair-' + c.id, async function () {
        var x = { pw: '' };
        if (st.active) {
          var ok = await ui.dialog({
            title: 'ניתוק תוסף', icon: 'close', fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
            actions: [{ label: 'ביטול', value: false }, { label: 'ניתוק', value: true, kind: 'danger' }],
            onSubmit: async function (v) { x = v; return true; }
          });
          if (!ok) return;
        } else if (!(await ui.confirm('ניתוק תוסף', 'בפתיחה הבאה התוסף יבקש אישור מחדש.', 'ניתוק'))) return;
        try { await SS.bridge.call('/pair/revoke', { id: c.id, password: x.pw }); load(body); }
        catch (e) { ui.error(e); }
      });
    });
  }

  var SECTION_NAMES = {
    audio: 'שמע', display: 'מסך', energy: 'חשמל וסוללה', keyboard: 'מקלדת', mouse: 'עכבר ומשטח מגע',
    printer: 'מדפסות', time: 'שעה ותאריך', wifi: 'רשת אלחוטית', bluetooth: 'בלוטות׳', airplane: 'מצב טיסה', usb: 'התקני USB'
  };

  async function editSettings(body, st) {
    var all = st.allSections || Object.keys(SECTION_NAMES);
    var open = st.openSections || [];
    var fields = [
      { name: 'ext', label: 'סיומות קבצים שמותר לפתוח בתוכנות אחרות', value: (st.allowOpenExtensions || []).join(', '), dir: 'ltr', placeholder: 'pdf, txt' },
      { name: 'path', label: 'נתיב אוצריא (ריק = זיהוי אוטומטי)', value: st.otzariaPath || '', dir: 'ltr', placeholder: 'C:\\Program Files\\Otzaria\\otzaria.exe' },
      { name: 'clock', type: 'checkbox', label: 'שעון ותאריך עברי צפים', value: st.clock !== false, hint: 'שורת המשימות מוסתרת בנעילה, ולכן מוצג שעון קטן בפינת המסך. לחיצה עליו מחזירה לאוצריא' },
      { name: 'rest', type: 'checkbox', label: 'מסך מנוחה כשאוצריא ממוזערת', value: st.restScreen !== false, hint: 'סמל אוצריא, שעה ותאריך במסך מלא. לחיצה בכל מקום מחזירה לאוצריא' },
      { name: 'idle', label: 'שומר מסך אחרי דקות ללא פעילות (0 = כבוי)', value: String(st.restIdleMinutes || 0), dir: 'ltr', placeholder: '0', hint: 'מסך המנוחה מופיע גם כשאוצריא פתוחה, אחרי הזמן הזה בלי מקלדת ועכבר. עד 240 דקות' },
      { name: 'zloc', label: 'מיקום לזמני היום בשומר המסך (קו רוחב, קו אורך)', value: st.zmanimLocation || '', dir: 'ltr', placeholder: '31.78, 35.22', hint: 'ריק = בלי זמנים. מוצגים עלות השחר, הנץ, סוף זמן ק"ש (מג"א וגר"א), חצות, שקיעה וצאת הכוכבים, לפי חישוב אסטרונומי בגובה פני הים — להצגה בלבד' },
      { name: 'side', type: 'checkbox', label: 'סרגל תוכנות בצד המסך', value: st.sideBar !== false, hint: 'פס צר בשולי המסך שמתרחב כשמתקרבים אליו: אוצריא והתוכנות התורניות המותרות, עם סימון מה פתוח. לחיצה מעבירה לחזית, או פותחת אם סגורה' },
      { name: 'reset', type: 'checkbox', label: 'ביטול נעילה בכיבוי המחשב', value: !!st.resetOnShutdown, hint: 'כבוי (ברירת מחדל): הנעילה נשמרת וחוזרת אחרי הפעלה מחדש. מסומן: בכיבוי או התנתקות הנעילה מתבטלת והמחשב עולה רגיל. לא פועל עם נעילה מוקשחת' },
      { name: 'fs', type: 'checkbox', label: 'אוצריא במסך מלא', value: st.fullscreenOtzaria !== false, hint: 'בלי כותרת ומסגרת חלון' },
      { name: 'ro', type: 'checkbox', label: 'מצב קריאה בלבד לקבצים', value: !!st.readOnly, hint: 'חוסם שינוי, מחיקה והעברה של קבצים בסייר בזמן נעילה' }
    ];
    all.forEach(function (id) {
      fields.push({ name: 'sec_' + id, type: 'checkbox', label: 'פתוח בנעילה: ' + (SECTION_NAMES[id] || id), value: open.indexOf(id) >= 0 });
    });
    fields.push({ name: 'pos', label: 'מיקום השעון (bottom-left / bottom-right / top-left / top-right)', value: st.clockPosition || 'bottom-left', dir: 'ltr' });
    fields.push({ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' });
    var ok = await ui.dialog({
      title: 'הגדרות נעילה', icon: 'edit',
      body: '<p class="muted">מה פתוח לתלמיד בזמן נעילה, ואיך נראה המסך. הגדרות שלא סומנו מוסתרות ונחסמות גם בגשר.</p>',
      fields: fields,
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.pw) return 'יש להקליד את סיסמת הנעילה';
        if (!/^\d{1,3}$/.test(String(x.idle).trim()) || +x.idle > 240) return 'זמן שומר המסך: מספר דקות בין 0 ל-240';
        if (x.zloc.trim() && !/^-?\d{1,2}(\.\d+)?\s*,\s*-?\d{1,3}(\.\d+)?$/.test(x.zloc.trim())) return 'מיקום לא תקין: קו רוחב, קו אורך (למשל 31.78, 35.22)';
        if (['bottom-left', 'bottom-right', 'top-left', 'top-right'].indexOf(x.pos.trim()) < 0) return 'מיקום שעון לא תקין';
        var bad = parseExt(x.ext).filter(function (e) { return !/^[a-z0-9]{1,10}$/.test(e); });
        return bad.length ? 'סיומת לא תקינה: ' + bad[0] : '';
      },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/settings', {
          password: x.pw, allowOpenExtensions: parseExt(x.ext), otzariaPath: x.path.trim(),
          readOnly: !!x.ro, clock: !!x.clock, restScreen: !!x.rest, fullscreenOtzaria: !!x.fs, sideBar: !!x.side, resetOnShutdown: !!x.reset, clockPosition: x.pos.trim(),
          restIdleMinutes: +String(x.idle).trim() || 0, zmanimLocation: x.zloc.trim(),
          openSections: all.filter(function (id) { return x['sec_' + id]; })
        });
        return true;
      }
    });
    if (ok) { ui.toast('ההגדרות נשמרו', 'success'); load(body); }
  }

  /* מדיניות סייר הקבצים נערכת בתוסף "סייר קבצים" עצמו; כאן רק מפנים אליה */
  function fxCard() {
    return '<div class="card subtle"><div class="card-title">נעילה והגבלה של סייר הקבצים</div>' +
      '<p class="muted">אפשר לנעול את סייר הקבצים בסיסמת המנהל, או להגביל אותו לתיקיות מסוימות (למשל שולחן העבודה). ההגדרה נמצאת בתוסף „סייר קבצים”: הגדרות ← נעילה והגבלה, ונאכפת בגשר.</p></div>';
  }

  function programsCard(progs, found, autoTorah) {
    var rows = progs.map(function (p) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(p.name) + '</b>' + (p.auto ? ' <span class="muted small">(זוהתה אוטומטית)</span>' : '') + (p.running ? ' <span class="muted small">· פתוחה עכשיו</span>' : '') +
        '<span class="muted path"><bdi dir="ltr">' + ui.esc(p.path) + '</bdi>' + (p.exists ? '' : ' · הקובץ לא נמצא') + '</span></div>' +
        '<button class="btn btn-tonal" data-act="prog-open-' + ui.esc(p.id) + '"' + (p.exists ? '' : ' disabled') + '>' + ui.icon('computer') + (p.running ? 'העבר לחזית' : 'פתיחה') + '</button>' +
        (p.auto ? '' : '<button class="btn btn-tonal" data-act="prog-del-' + ui.esc(p.id) + '" aria-label="הסרה">' + ui.icon('close') + '</button>') + '</div>';
    }).join('');
    var suggest = found.map(function (f, i) {
      return '<button class="btn btn-tonal" data-act="prog-found-' + i + '">' + ui.icon('edit') + 'הוספת ' + ui.esc(f.name) + '</button>';
    }).join('');
    return '<div class="card"><div class="card-title">תוכנות מותרות לפתיחה</div>' +
      '<p class="muted">תוכנות לימוד (כתבי הקודש, אוצר החכמה, בר אילן, Word ועוד) שאפשר לפתוח מכאן גם בזמן נעילה. ההוספה וההסרה דורשות את סיסמת הנעילה. בסיום העבודה סוגרים את התוכנה וחוזרים לאוצריא.</p>' +
      '<div class="setting-row"><div class="setting-text"><b>זיהוי אוטומטי של תוכנות תורניות</b><span class="muted">' +
        (autoTorah ? 'דולק: תוכנות תורניות ו-Word שמותקנות במחשב נוספות לרשימה מעצמן.' : 'כבוי: אפשר להדליק כדי שהתוכנות התורניות ו-Word יתווספו לרשימה אוטומטית.') + '</span></div>' +
        '<button class="btn ' + (autoTorah ? 'btn-tonal' : 'btn-primary') + '" data-act="prog-auto">' + ui.icon(autoTorah ? 'unlock' : 'check') + (autoTorah ? 'כיבוי הזיהוי' : 'הדלקה') + '</button></div>' +
      (rows || '<p class="muted">עדיין לא הוגדרו תוכנות.</p>') +
      '<div class="row-actions"><button class="btn btn-tonal" data-act="prog-add">' + ui.icon('edit') + 'הוספת תוכנה</button>' + suggest + '</div></div>';
  }

  async function openProgram(p) {
    try {
      await SS.bridge.call('/kiosk/programs/launch', { id: p.id }, { timeoutMs: 30000 });
      ui.toast('התוכנה נפתחת…', 'success');
    } catch (err) { ui.toast(ui.errText(err), 'error'); }
  }

  async function addProgram(body, preset) {
    var ok = await ui.dialog({
      title: 'הוספת תוכנה מותרת', icon: 'computer',
      body: '<p class="muted">התוכנה תופיע ברשימה ותיפתח מתוך אוצריא גם בזמן נעילה. מומלץ להוסיף רק תוכנות לימוד. אם התוכנה נפתחת דרך תהליך נוסף (מפעיל נפרד), הוסיפו את שמו בשדה „תהליכים נוספים”, כדי שהחלון לא יוחזר לרקע.</p>',
      fields: [
        { name: 'name', label: 'שם להצגה', value: preset.name || '' },
        { name: 'path', label: 'נתיב קובץ ההפעלה (exe)', value: preset.path || '', dir: 'ltr', placeholder: 'C:\\Program Files\\...\\program.exe' },
        { name: 'procs', label: 'תהליכים נוספים (לא חובה)', value: preset.procs || '', dir: 'ltr', placeholder: 'launcher, helper' },
        { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'הוספה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.name.trim()) return 'יש להקליד שם';
        if (!/^[A-Za-z]:\\.+\.exe$/i.test(x.path.trim().replace(/^"|"$/g, ''))) return 'יש להקליד נתיב מלא לקובץ exe';
        return x.pw ? '' : 'יש להקליד את סיסמת הנעילה';
      },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/programs/add', { password: x.pw, name: x.name.trim(), path: x.path.trim(), procs: (x.procs || '').trim() });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה נוספה', 'success'); load(body); }
  }

  async function toggleAuto(body, current) {
    var ok = await ui.dialog({
      title: current ? 'כיבוי הזיהוי האוטומטי' : 'הדלקת הזיהוי האוטומטי', icon: 'computer',
      body: '<p>' + (current
        ? 'התוכנות שזוהו אוטומטית יוסרו מהרשימה ולא יהיה אפשר לפתוח אותן בזמן נעילה (תוכנות שהוספתם ידנית יישארו).'
        : 'הגשר יחפש במחשב תוכנות תורניות (אוצר החכמה, בר אילן, כתבי הקודש ועוד) ואת Word, ויוסיף אותן לרשימה המותרת בזמן נעילה.') + '</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: current ? 'כיבוי הזיהוי' : 'הדלקה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/programs/auto', { password: x.pw, enabled: !current }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast(current ? 'הזיהוי האוטומטי כובה' : 'הזיהוי האוטומטי הודלק', 'success'); load(body); }
  }

  async function removeProgram(body, p) {
    var ok = await ui.dialog({
      title: 'הסרת תוכנה מהרשימה', icon: 'warning',
      body: '<p>להסיר את <b>' + ui.esc(p.name) + '</b> מרשימת התוכנות המותרות? התוכנה עצמה לא תימחק.</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'הסרה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/programs/remove', { password: x.pw, id: p.id });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה הוסרה', 'success'); load(body); }
  }

  function parseExt(s) {
    return String(s || '').split(/[,;\s]+/).map(function (e) { return e.trim().toLowerCase().replace(/^\*?\./, ''); })
      .filter(Boolean).filter(function (e, i, a) { return a.indexOf(e) === i; });
  }
})();
