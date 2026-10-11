/* sections/usb.js - safe eject of USB drives */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var timer = null, busy = false, lastSig = '';

  function fmt(n) {
    if (!n) return '';
    var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i >= 3 ? n.toFixed(1) : Math.round(n)) + ' ' + u[i];
  }

  function row(d) {
    var title = (d.label ? d.label : 'כונן נתיק') + ' (' + d.letter + ')';
    var bits = [];
    if (d.model) bits.push(d.model);
    if (d.fs) bits.push(d.fs);
    if (d.sizeBytes) bits.push(fmt(d.freeBytes) + ' פנויים מתוך ' + fmt(d.sizeBytes));
    return '<li class="list-item">' +
      '<span class="li-icon">' + ui.icon('usb') + '</span>' +
      '<span class="li-text"><b dir="ltr">' + ui.esc(title) + '</b><span class="muted small" dir="ltr">' + ui.esc(bits.join(' · ')) + '</span></span>' +
      '<button class="btn btn-tonal" data-eject="' + ui.esc(d.letter) + '">הוצא</button></li>';
  }

  async function load(body, quiet) {
    var slot = body.querySelector('[data-slot=usb]');
    if (!slot || busy) return;
    var d;
    try { d = await SS.bridge.call('/sys/usb/list'); }
    catch (e) { if (!quiet) slot.innerHTML = ui.errorBox(e); return; }
    var drives = d.drives || [];
    var sig = JSON.stringify(drives);
    if (quiet && sig === lastSig) return;
    lastSig = sig;
    if (!drives.length) {
      slot.innerHTML = '<div class="empty">לא נמצאו כונני USB מחוברים. הרשימה מתעדכנת אוטומטית.</div>';
      return;
    }
    slot.innerHTML = '<ul class="list">' + drives.map(row).join('') + '</ul>' +
      '<p class="muted small">לפני ההוצאה סגרו קבצים ותוכנות שפתוחים מהכונן. אחרי ההודעה אפשר לנתק אותו.</p>';
    slot.querySelectorAll('[data-eject]').forEach(function (b) {
      b.addEventListener('click', async function () {
        var letter = b.dataset.eject;
        busy = true;
        b.disabled = true;
        b.innerHTML = '<span class="spinner sm"></span>';
        try {
          var r = await SS.bridge.call('/sys/usb/eject', { letter: letter }, { timeoutMs: 30000 });
          ui.toast((r && r.poweredOff ? 'ההתקן כובה. ' : '') + 'אפשר לנתק את הכונן ' + letter + ' בבטחה', 'success');
        } catch (e) { ui.error(e, 'הוצאת כונן'); }
        busy = false;
        lastSig = '';
        load(body);
      });
    });
  }

  SS.sections.push({
    id: 'usb', title: 'התקני USB', icon: 'usb',
    mount: function (body) {
      body.innerHTML = '<div class="card"><div class="card-title">כונני USB מחוברים</div><div data-slot="usb">' + ui.loading() + '</div></div>';
      clearInterval(timer);
      timer = setInterval(function () {
        if (!body.isConnected) { clearInterval(timer); return; }
        if (!document.hidden) load(body, true);
      }, 5000);
    },
    load: function (body) { lastSig = ''; return load(body); }
  });
})();
