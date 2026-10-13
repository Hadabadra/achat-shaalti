/* sections/printer.js - printers: list, default printer, test page */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var STATUS = { 3: 'מוכנה', 4: 'מדפיסה', 5: 'מתחממת', 6: 'עצרה', 7: 'לא מחוברת' };

  SS.sections.push({
    id: 'printer', title: 'מדפסות', icon: 'printer', group: 'devices',
    keywords: 'מדפסת הדפסה ברירת מחדל דף ניסיון printer print',
    mount: function (body) {
      body.innerHTML = '<div class="card"><div class="card-title">מדפסות מותקנות</div><div data-slot="pr">' + ui.loading() + '</div></div>';
    },
    load: load
  });

  async function load(body) {
    var slot = body.querySelector('[data-slot=pr]');
    var d;
    try { d = await SS.bridge.call('/sys/printers/list', {}, { timeoutMs: 30000 }); }
    catch (e) { slot.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    var list = d.printers || [];
    if (!list.length) { slot.innerHTML = '<div class="empty">לא נמצאו מדפסות במחשב.</div>'; return; }
    list.sort(function (a, b) { return (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0); });
    slot.innerHTML = '<ul class="list">' + list.map(function (p, i) {
      var st = p.offline ? 'לא מחוברת' : (STATUS[p.status] || '');
      return '<li class="list-item' + (p.isDefault ? ' selected' : '') + '"><span class="li-icon">' + ui.icon('printer') + '</span>' +
        '<span class="li-text"><b>' + ui.esc(p.name) + '</b><span class="muted">' +
        [p.isDefault ? 'ברירת מחדל' : '', st, p.network ? 'מדפסת רשת' : ''].filter(Boolean).join(' · ') + '</span></span>' +
        (p.isDefault ? '<span class="chip ok">' + ui.icon('check') + 'ברירת מחדל</span>'
          : '<button class="btn btn-tonal" data-def="' + i + '">קביעה כברירת מחדל</button>') +
        '<button class="btn btn-text" data-test="' + i + '" title="הדפסת דף ניסיון">דף ניסיון</button></li>';
    }).join('') + '</ul>' +
      (d.windowsManagesDefault ? '<p class="muted small">' + ui.icon('info') + ' כרגע Windows בוחר את מדפסת ברירת המחדל לבד. בחירה כאן מכבה את זה.</p>' : '');
    slot.querySelectorAll('[data-def]').forEach(function (b) {
      b.addEventListener('click', async function () {
        b.disabled = true;
        try { await SS.bridge.call('/sys/printers/set-default', { name: list[+b.dataset.def].name }, { timeoutMs: 30000 }); ui.toast('מדפסת ברירת המחדל עודכנה', 'success'); }
        catch (e) { ui.error(e, 'מדפסת'); }
        load(body);
      });
    });
    slot.querySelectorAll('[data-test]').forEach(function (b) {
      b.addEventListener('click', async function () {
        var p = list[+b.dataset.test];
        if (!(await ui.confirm('הדפסת דף ניסיון', '<p>לשלוח דף ניסיון אל <b>' + ui.isolateLtr(ui.esc(p.name)) + '</b>?</p>', 'הדפסה'))) return;
        try { await SS.bridge.call('/sys/printers/test-page', { name: p.name }, { timeoutMs: 30000 }); ui.toast('דף הניסיון נשלח', 'success'); }
        catch (e) { ui.error(e, 'דף ניסיון'); }
      });
    });
  }
})();
