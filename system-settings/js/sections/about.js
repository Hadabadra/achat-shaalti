/* sections/about.js - plugin and bridge info, download link */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  function cmpVer(a, b) {
    var x = String(a || '').split('.').map(Number), y = String(b || '').split('.').map(Number);
    for (var i = 0; i < 3; i++) { var d = (x[i] || 0) - (y[i] || 0); if (d) return d; }
    return 0;
  }

  /* בדיקת עדכון: הגשר משווה לגרסה האחרונה בגיטהאב; ההורדה ידנית מעמוד ההוצאות */
  async function checkUpdate(slot, force) {
    if (!slot) return;
    slot.innerHTML = '<div class="row-actions"><button class="btn btn-tonal" data-act="chk">' + ui.icon('refresh') + 'בדיקת עדכון</button></div>';
    var btn = slot.querySelector('[data-act=chk]');
    btn.addEventListener('click', function () { checkUpdate(slot, true); });
    if (!force) return;
    btn.disabled = true;
    var r;
    try { r = await SS.bridge.call('/sys/update-check', { force: true }, { timeoutMs: 30000 }); }
    catch (e) {
      slot.innerHTML = '<div class="state-box error">' + ui.icon('warning') + '<div><b>בדיקת העדכון נכשלה</b><p class="muted">' + ui.esc(ui.errText(e)) + '</p></div></div>';
      return;
    }
    /* גם התוסף עצמו נבדק: גרסת התוסף ישנה מהגרסה האחרונה = צריך להתקין את התוספים מחדש */
    var pluginOld = r.latest && cmpVer(SS.VERSION, r.latest) < 0;
    var msg = r.newer
      ? '<b>יש גרסה חדשה: ' + ui.esc(r.latest) + '</b> (הגשר המותקן: ' + ui.esc(r.current) + ').' +
        (r.canInstall ? ' אפשר לעדכן בלחיצה: הקובץ יורד, נבדק מול חתימת ה-SHA256 שבהוצאה, והגשר יופעל מחדש.' : ' הורידו מעמוד ההוצאות והפעילו את הקובץ החדש.')
      : '<b>הגשר מעודכן</b> (גרסה ' + ui.esc(r.current) + ', האחרונה: ' + ui.esc(r.latest || '?') + ').';
    if (pluginOld) msg += '<br>התוסף הזה בגרסה ' + ui.esc(SS.VERSION) + '. אחרי עדכון הגשר: תפריט הגשר ליד השעון ← „התקן/עדכן את התוספים באוצריא”.';
    slot.innerHTML = '<div class="state-box">' + ui.icon(r.newer ? 'download' : 'check') + '<div>' + msg + '</div></div>' +
      '<div class="row-actions">' +
      (r.canInstall ? '<button class="btn btn-primary" data-act="install">' + ui.icon('download') + 'עדכון עכשיו</button>' : '') +
      (r.newer ? '<button class="btn ' + (r.canInstall ? 'btn-tonal' : 'btn-primary') + '" data-act="open">' + ui.icon('link') + 'לעמוד ההוצאה</button>' : '') +
      '<button class="btn btn-tonal" data-act="chk">' + ui.icon('refresh') + 'בדיקה חוזרת</button></div>';
    var o = slot.querySelector('[data-act=open]');
    if (o) o.addEventListener('click', function () { SS.openUrl(r.url || SS.BRIDGE_URL); });
    var ins = slot.querySelector('[data-act=install]');
    if (ins) ins.addEventListener('click', async function () {
      ins.disabled = true;
      ins.innerHTML = '<span class="spinner sm"></span>מוריד…';
      try {
        var x = await SS.bridge.call('/sys/update-install', {}, { timeoutMs: 180000 });
        ui.toast(x.started ? 'העדכון מותקן. הגשר יופעל מחדש בעוד רגע.' : (x.message || 'הגשר מעודכן'), 'success');
        setTimeout(function () { if (SS.reprobe) SS.reprobe(); }, 8000);
      } catch (e) { ui.error(e, 'עדכון'); ins.disabled = false; ins.innerHTML = ui.icon('download') + 'עדכון עכשיו'; }
    });
    slot.querySelector('[data-act=chk]').addEventListener('click', function () { checkUpdate(slot, true); });
  }

  SS.sections.push({
    id: 'about', title: 'אודות', icon: 'info',
    mount: function (body) {
      body.innerHTML =
        '<div class="card about-hero"><div class="about-ic">' + ui.icon('computer') + '</div><div>' +
        '<h3>הגדרות מערכת</h3><p class="muted">גרסה ' + ui.esc(SS.VERSION) + ' · מאת תורת אסתר</p>' +
        '<p>כל הגדרות המחשב מתוך אוצריא: מסך, שמע, חשמל, שעה ותאריך, מקלדת, עכבר, מדפסות, USB, בלוטות׳ ורשת — ומצב נעילה שהופך את המחשב לעמדת לימוד באוצריא ובתוכנות הלימוד בלבד.</p></div></div>' +
        '<div class="card about-memorial"><p>לעילוי נשמת הנערה הצדקנית</p><h3>אסתר גיטה דינר בת רבי מיכאל שלום הלוי שיבדלט"א</h3><p>תהא נשמתה צרורה בצרור החיים. תנצב"ה</p></div>' +
        '<div class="card"><div class="card-title">גשר אוצריא</div><div data-slot="bridge">' + ui.loading() + '</div>' +
        '<div data-slot="update"></div></div>' +
        '<div class="card"><div class="card-title">התקנת הגשר</div><ol class="steps">' +
        '<li>מורידים את <b dir="ltr">OtzariaBridge.exe</b> מהקישור.</li>' +
        '<li>מפעילים פעם אחת. הגשר מעתיק את עצמו ל-<span dir="ltr">%LOCALAPPDATA%\\OtzariaBridge</span>, נרשם להפעלה אוטומטית ומופיע כאייקון ליד השעון.</li>' +
        '<li>הגשר מאזין רק למחשב המקומי (127.0.0.1) ואינו נגיש מהרשת.</li>' +
        '<li>להסרה: <code dir="ltr">OtzariaBridge.exe --uninstall</code></li></ol>' +
        '<p class="muted small">עמוד ההוצאות (הגרסה האחרונה של הגשר): <bdi dir="ltr" class="path">' + ui.esc(SS.BRIDGE_URL) + '</bdi></p>' +
        '<div class="row-actions"><button class="btn btn-primary" data-act="dl">' + ui.icon('download') + 'הורדת הגשר</button>' +
        '<button class="btn btn-tonal" data-act="copy">' + ui.icon('link') + 'העתקת הקישור</button></div></div>';
      body.querySelector('[data-act=copy]').addEventListener('click', function () {
        SS.copyText(SS.BRIDGE_URL).then(function (ok) { ui.toast(ok ? 'הקישור הועתק' : 'ההעתקה נכשלה: ' + SS.BRIDGE_URL, ok ? 'success' : 'error'); });
      });
      body.querySelector('[data-act=dl]').addEventListener('click', function () { SS.openUrl(SS.BRIDGE_URL); });
    },
    load: async function (body) {
      var slot = body.querySelector('[data-slot=bridge]');
      var h = null;
      try { h = await SS.bridge.probe(); } catch (e) { h = null; }
      SS.setStatus(!!h);
      if (!h) {
        slot.innerHTML = '<div class="state-box error">' + ui.icon('warning') + '<div><b>הגשר אינו מחובר</b></div></div>';
        return;
      }
      slot.innerHTML = '<dl class="info-grid">' +
        '<dt>מצב</dt><dd><span class="chip ok">מחובר</span>' + (SS.bridge.isMock ? ' <span class="chip">תצוגה מקדימה</span>' : '') + '</dd>' +
        '<dt>גרסת הגשר</dt><dd dir="ltr">' + ui.esc(h.version) + '</dd>' +
        '<dt>פורט</dt><dd dir="ltr">127.0.0.1:' + ui.esc(h.port || SS.bridge.port) + '</dd>' +
        '<dt>מערכת</dt><dd dir="ltr">' + ui.esc(h.platform || '') + '</dd>' +
        '<dt>מצב נעילה</dt><dd>' + (h.kioskActive ? 'פעיל' : 'לא פעיל') + '</dd></dl>';
      checkUpdate(body.querySelector('[data-slot=update]'), true);
    }
  });
})();
