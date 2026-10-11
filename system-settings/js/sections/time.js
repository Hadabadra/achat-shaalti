/* sections/time.js - date and time: live clock with Hebrew date, time zone, 24-hour clock, sync, manual time */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];
  var tick = null;

  /* תאריך עברי מהדפדפן עצמו (Intl עם לוח עברי), בלי ספריות */
  SS.hebrewDate = function (d) {
    try {
      return new Intl.DateTimeFormat('he-IL-u-ca-hebrew', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }).format(d);
    } catch (e) { return ''; }
  };

  SS.sections.push({
    id: 'time', title: 'שעה ותאריך', icon: 'clock', group: 'system',
    keywords: 'שעה תאריך שעון אזור זמן עברי לוח 24 סנכרון time clock zone',
    mount: function (body) {
      body.innerHTML =
        '<div class="card clock-hero"><div class="clock-big" data-slot="now">--:--</div>' +
        '<div class="clock-heb" data-slot="heb"></div><div class="muted" data-slot="greg"></div></div>' +
        '<div class="card"><div class="card-title">אזור זמן ותצוגה</div><div data-slot="tz">' + ui.loading() + '</div></div>';
      clearInterval(tick);
      var paint = function () {
        if (!body.isConnected) { clearInterval(tick); return; }
        var d = new Date();
        body.querySelector('[data-slot=now]').textContent = d.toLocaleTimeString('he-IL', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
        body.querySelector('[data-slot=heb]').textContent = SS.hebrewDate(d);
        body.querySelector('[data-slot=greg]').textContent = d.toLocaleDateString('he-IL', { day: 'numeric', month: 'long', year: 'numeric' });
      };
      paint();
      tick = setInterval(paint, 1000);
    },
    load: async function (body) {
      var d;
      try { d = await SS.bridge.call('/sys/time/get'); }
      catch (e) { body.querySelector('[data-slot=tz]').innerHTML = ui.errorBox(e); return; }
      render(body, d);
    }
  });

  async function apply(body, patch, label) {
    try { render(body, await SS.bridge.call('/sys/time/set', patch, { timeoutMs: 20000 })); ui.toast(label + ' — נשמר', 'success'); }
    catch (e) { ui.error(e, label); }
  }

  function render(body, d) {
    var slot = body.querySelector('[data-slot=tz]');
    var opts = (d.zones || []).map(function (z) {
      return '<option value="' + ui.esc(z.id) + '"' + (z.id === d.zone ? ' selected' : '') + '>' + ui.esc(z.name) + '</option>';
    }).join('');
    slot.innerHTML =
      '<label class="field"><span class="field-label">אזור זמן</span><select class="input" data-k="zone" dir="ltr">' + opts + '</select></label>' +
      '<div class="setting-row"><div class="setting-text"><b>שעון 24 שעות</b><span class="muted">' + (d.clock24 ? 'למשל 14:30' : 'למשל 2:30 PM') + '</span></div>' +
      ui.switchHtml('sw-24', d.clock24, 'שעון 24 שעות') + '</div>' +
      '<div class="setting-row"><div class="setting-text"><b>סנכרון השעה מול האינטרנט</b><span class="muted">' +
      (d.autoSync ? 'Windows מסנכרן אוטומטית' : 'הסנכרון האוטומטי כבוי') + '</span></div>' +
      '<button class="btn btn-tonal" data-act="sync">' + ui.icon('refresh') + 'סנכרון עכשיו</button></div>' +
      '<div class="setting-row"><div class="setting-text"><b>שינוי ידני של השעה</b><span class="muted">דורש הרשאות מנהל ב-Windows</span></div>' +
      '<button class="btn btn-tonal" data-act="manual">' + ui.icon('edit') + 'שינוי</button></div>' +
      '<p class="muted small">שעון המחשב: <bdi dir="ltr">' + ui.esc(String(d.now || '').replace('T', ' ')) + '</bdi>' + (d.dst ? ' · שעון קיץ' : '') + '</p>';
    slot.querySelector('[data-k=zone]').addEventListener('change', function () { apply(body, { zone: this.value }, 'אזור זמן'); });
    slot.querySelector('#sw-24').addEventListener('click', function () { apply(body, { clock24: !d.clock24 }, 'תצוגת השעה'); });
    slot.querySelector('[data-act=sync]').addEventListener('click', async function () {
      var b = this; b.disabled = true;
      try { render(body, await SS.bridge.call('/sys/time/sync', {}, { timeoutMs: 20000 })); ui.toast('בקשת הסנכרון נשלחה', 'success'); }
      catch (e) { ui.error(e, 'סנכרון'); b.disabled = false; }
    });
    slot.querySelector('[data-act=manual]').addEventListener('click', async function () {
      var now = new Date(), pad = function (n) { return (n < 10 ? '0' : '') + n; };
      var v = await ui.dialog({
        title: 'שינוי השעה והתאריך', icon: 'clock',
        fields: [
          { name: 'date', label: 'תאריך', type: 'date', value: now.getFullYear() + '-' + pad(now.getMonth() + 1) + '-' + pad(now.getDate()), dir: 'ltr' },
          { name: 'time', label: 'שעה', type: 'time', value: pad(now.getHours()) + ':' + pad(now.getMinutes()), dir: 'ltr' }
        ],
        actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
        validate: function (x) { return x.date && x.time ? '' : 'יש למלא תאריך ושעה'; },
        onSubmit: async function (x) { await SS.bridge.call('/sys/time/set', { dateTime: x.date + 'T' + x.time }); return true; }
      });
      if (v) { ui.toast('השעה עודכנה', 'success'); SS.sections.find(function (s) { return s.id === 'time'; }).load(body); }
    });
  }
})();
