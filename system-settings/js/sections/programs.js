/* sections/programs.js - quick launcher for the allowed study programs (works in lock mode too) */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  SS.sections.push({
    id: 'programs', title: 'תוכנות לימוד', icon: 'computer',
    mount: function (body) { body.innerHTML = ui.loading('טוען תוכנות…'); },
    load: load
  });

  async function load(body) {
    var r;
    try { r = await SS.bridge.call('/kiosk/programs'); }
    catch (e) { body.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    var progs = r.programs || [];
    var html = '<div class="card"><div class="card-title">פתיחת תוכנה</div>' +
      '<p class="muted">תוכנות לימוד שאפשר לפתוח מכאן גם כשהמחשב נעול. בסיום סוגרים את התוכנה וחוזרים לאוצריא.</p>';
    if (!progs.length) {
      html += '<p class="muted">עדיין לא הוגדרו תוכנות. אפשר להוסיף אותן, או להדליק זיהוי אוטומטי, במסך „מצב נעילה” (נדרשת סיסמה).</p>';
    } else {
      html += progs.map(function (p) {
        return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(p.name) + '</b>' +
          (p.exists ? '' : '<span class="muted">הקובץ לא נמצא</span>') + '</div>' +
          '<button class="btn btn-primary" data-open="' + ui.esc(p.id) + '"' + (p.exists ? '' : ' disabled') + '>' + ui.icon('computer') + 'פתיחה</button></div>';
      }).join('');
    }
    html += '<div class="row-actions"><button class="btn btn-tonal" data-act="manage">' + ui.icon('edit') + 'ניהול הרשימה</button></div></div>';
    body.innerHTML = html;
    body.querySelector('[data-act=manage]').addEventListener('click', function () { SS.show('kiosk'); });
    body.querySelectorAll('[data-open]').forEach(function (b) {
      b.addEventListener('click', async function () {
        b.disabled = true;
        try {
          await SS.bridge.call('/kiosk/programs/launch', { id: b.dataset.open }, { timeoutMs: 30000 });
          ui.toast('התוכנה נפתחת…', 'success');
        } catch (err) { ui.toast(ui.errText(err), 'error'); }
        setTimeout(function () { b.disabled = false; }, 3000);
      });
    });
  }
})();
