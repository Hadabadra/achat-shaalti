/* sections/mouse.js - mouse and touchpad: pointer speed, left-handed buttons, double-click, wheel, touchpad */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  SS.sections.push({
    id: 'mouse', title: 'עכבר ומשטח מגע', icon: 'mouse', group: 'devices',
    keywords: 'עכבר משטח מגע מהירות סמן יד שמאל לחיצה כפולה גלגלת touchpad mouse',
    mount: function (body) {
      body.innerHTML =
        '<div class="card"><div class="card-title">עכבר</div><div data-slot="mouse">' + ui.loading() + '</div></div>' +
        '<div class="card" data-card="tp" hidden><div class="card-title">משטח מגע</div><div data-slot="tp"></div></div>';
    },
    load: async function (body) {
      var d;
      try { d = await SS.bridge.call('/sys/mouse/get'); }
      catch (e) { body.querySelector('[data-slot=mouse]').innerHTML = ui.errorBox(e); return; }
      render(body, d);
    }
  });

  function send(body, patch) {
    return SS.bridge.call('/sys/mouse/set', patch).then(function (d) { return d; })
      .catch(function (e) { ui.error(e, 'עכבר'); SS.sections.find(function (s) { return s.id === 'mouse'; }).load(body); });
  }

  function slider(key, label, min, max, val, fmt) {
    return '<div class="setting-text"><b>' + label + '</b></div>' +
      '<div class="slider-row"><input type="range" class="range" data-k="' + key + '" min="' + min + '" max="' + max + '" step="1" value="' + val + '" aria-label="' + label + '">' +
      '<output class="range-val" dir="ltr">' + fmt(val) + '</output></div>';
  }

  function wireSliders(slot, body, fmts, wrap) {
    slot.querySelectorAll('.range').forEach(function (r) {
      SS.paintRange(r);
      var out = r.parentNode.querySelector('.range-val');
      var push = ui.debounce(function (v) {
        var p = {}; p[r.dataset.k] = v;
        send(body, wrap ? wrap(p) : p);
      }, 250);
      r.addEventListener('input', function () {
        SS.paintRange(r);
        out.textContent = fmts[r.dataset.k](+r.value);
        push(+r.value);
      });
    });
  }

  function render(body, d) {
    var slot = body.querySelector('[data-slot=mouse]');
    var fm = {
      speed: function (v) { return v + ' / 20'; },
      doubleClickMs: function (v) { return v + ' ms'; },
      wheelLines: function (v) { return v + (v === 1 ? ' שורה' : ' שורות'); }
    };
    slot.innerHTML =
      '<div class="setting-row"><div class="setting-text"><b>לחצן ראשי שמאלי (יד שמאל)</b><span class="muted">מחליף בין הלחצן הימני והשמאלי</span></div>' +
      ui.switchHtml('sw-left', d.leftHanded, 'יד שמאל') + '</div>' +
      '<div class="setting-row"><div class="setting-text"><b>שיפור דיוק הסמן</b><span class="muted">הסמן זז רחוק יותר בתנועה מהירה</span></div>' +
      ui.switchHtml('sw-prec', d.precision, 'שיפור דיוק') + '</div>' +
      slider('speed', 'מהירות הסמן', 1, 20, d.speed, fm.speed) +
      slider('wheelLines', 'גלילה בגלגלת', 1, 20, d.wheelLines, fm.wheelLines) +
      slider('doubleClickMs', 'מהירות לחיצה כפולה (ערך נמוך = מהיר)', 200, 900, d.doubleClickMs, fm.doubleClickMs) +
      '<div class="row-actions"><button class="btn btn-tonal" data-act="reset">איפוס לברירת מחדל</button></div>';
    wireSliders(slot, body, fm);
    slot.querySelector('#sw-left').addEventListener('click', function () {
      d.leftHanded = !d.leftHanded; ui.setSwitch(this, d.leftHanded); send(body, { leftHanded: d.leftHanded });
    });
    slot.querySelector('#sw-prec').addEventListener('click', function () {
      d.precision = !d.precision; ui.setSwitch(this, d.precision); send(body, { precision: d.precision });
    });
    slot.querySelector('[data-act=reset]').addEventListener('click', function () {
      send(body, { speed: 10, leftHanded: false, doubleClickMs: 500, wheelLines: 3, precision: true }).then(function (x) { if (x) render(body, x); });
    });

    var card = body.querySelector('[data-card=tp]');
    var tp = d.touchpad;
    card.hidden = !tp;
    if (!tp) return;
    var ts = card.querySelector('[data-slot=tp]');
    var ROWS = [
      ['taps', 'הקשה ללחיצה', 'הקשה קלה על המשטח במקום לחיצה'],
      ['twoFingerRightClick', 'הקשה בשתי אצבעות = לחיצה ימנית', ''],
      ['naturalScroll', 'גלילה טבעית', 'התוכן זז לכיוון תנועת האצבעות']
    ];
    var SENS = [[0, 'רגיש מאוד'], [1, 'רגיש'], [2, 'בינוני'], [3, 'נמוך']];
    ts.innerHTML = ROWS.map(function (r) {
      return '<div class="setting-row"><div class="setting-text"><b>' + r[1] + '</b>' + (r[2] ? '<span class="muted">' + r[2] + '</span>' : '') + '</div>' +
        ui.switchHtml('tp-' + r[0], !!tp[r[0]], r[1]) + '</div>';
    }).join('') +
      '<div class="setting-text"><b>רגישות למגע</b></div><div class="preset-row">' + SENS.map(function (s) {
        return '<button class="btn ' + (s[0] === tp.sensitivity ? 'btn-primary' : 'btn-tonal') + '" data-sens="' + s[0] + '">' + s[1] + '</button>';
      }).join('') + '</div>' +
      '<p class="muted small">' + ui.icon('info') + ' הגדרות משטח המגע נשמרות מיד, ו-Windows מחיל אותן בכניסה הבאה למערכת.</p>';
    ROWS.forEach(function (r) {
      ts.querySelector('#tp-' + r[0]).addEventListener('click', function () {
        tp[r[0]] = !tp[r[0]]; ui.setSwitch(this, tp[r[0]]);
        var p = {}; p[r[0]] = tp[r[0]]; send(body, { touchpad: p });
      });
    });
    ts.querySelectorAll('[data-sens]').forEach(function (b) {
      b.addEventListener('click', function () {
        send(body, { touchpad: { sensitivity: +b.dataset.sens } }).then(function (x) { if (x) render(body, x); });
      });
    });
  }
})();
