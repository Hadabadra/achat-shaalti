/* layout.js - פריסת הסייר: גרירת רוחב החלוניות, חלונית תצוגה מקדימה, מצבי תצוגה (פרטים/רשימה/סמלים) וגודל טקסט */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var KEY = 'fx.layout';
  var L = { side: 14, prev: 22, preview: false, mode: 'details', zoom: 0.9 };
  var ZOOMS = [0.8, 0.85, 0.9, 1, 1.1, 1.2, 1.35];
  var $ = function (id) { return document.getElementById(id); };

  function load() {
    try { var v = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; Object.keys(v).forEach(function (k) { L[k] = v[k]; }); } catch (e) { /* כלום */ }
  }
  function save() { try { localStorage.setItem(KEY, JSON.stringify(L)); } catch (e) { /* כלום */ } }
  function rtl() { return document.documentElement.dir !== 'ltr'; }
  function emPx(el) { return parseFloat(getComputedStyle(el).fontSize) || 16; }

  function apply() {
    var root = document.documentElement;
    root.style.setProperty('--fx-zoom', String(L.zoom));
    root.style.setProperty('--fx-side-w', L.side + 'em');
    root.style.setProperty('--fx-prev-w', L.prev + 'em');
    $('fx-preview').hidden = !L.preview;
    $('fx-split-prev').hidden = !L.preview;
    var b = $('fx-preview-btn');
    if (b) { b.classList.toggle('on', !!L.preview); b.setAttribute('aria-pressed', L.preview ? 'true' : 'false'); }
    var list = $('fx-list'), main = $('fx-main');
    ['details', 'list', 'icons'].forEach(function (m) { list.classList.toggle('m-' + m, L.mode === m); main.classList.toggle('m-' + m, L.mode === m); });
    var mb = $('fx-mode-btn');
    if (mb) {
      mb.innerHTML = FX.icon(L.mode === 'icons' ? 'grid' : 'list');
      var t = { details: 'תצוגה: פרטים', list: 'תצוגה: רשימה', icons: 'תצוגה: סמלים' }[L.mode];
      mb.title = t; mb.setAttribute('aria-label', t);
    }
  }

  /* ---------- גרירת מפרידים ---------- */
  function splitter(el, key, min, max, sideOfMain) {
    el.addEventListener('pointerdown', function (ev) {
      ev.preventDefault();
      el.setPointerCapture(ev.pointerId);
      el.classList.add('drag');
      var startX = ev.clientX, start = L[key], px = emPx($('fx-body'));
      function move(e) {
        var dx = (e.clientX - startX) / px;
        /* החלונית בצד ימין (ב-RTL: הצד) גדלה כשגוררים שמאלה; בצד שמאל — כשגוררים ימינה */
        var onRight = rtl() ? sideOfMain === 'start' : sideOfMain === 'end';
        var w = start + (onRight ? -dx : dx);
        L[key] = Math.round(Math.max(min, Math.min(max, w)) * 10) / 10;
        apply();
      }
      function up() {
        el.removeEventListener('pointermove', move);
        el.removeEventListener('pointerup', up);
        el.removeEventListener('pointercancel', up);
        el.classList.remove('drag');
        save();
      }
      el.addEventListener('pointermove', move);
      el.addEventListener('pointerup', up);
      el.addEventListener('pointercancel', up);
    });
    el.addEventListener('dblclick', function () { L[key] = key === 'side' ? 14 : 22; apply(); save(); });
    el.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      e.preventDefault();
      var onRight = rtl() ? sideOfMain === 'start' : sideOfMain === 'end';
      var grow = (e.key === 'ArrowLeft') === onRight;
      L[key] = Math.max(min, Math.min(max, L[key] + (grow ? 1 : -1)));
      apply(); save();
    });
  }

  /* ---------- תצוגה מקדימה ---------- */
  var seq = 0, timer = null, lastKey = '';
  function previewBox(html) { $('fx-preview').innerHTML = html; }
  function infoHtml(e) {
    var kind = FX.kindOf(e);
    var rows = [
      ['סוג', FX.typeLabel(e)],
      ['גודל', e.type === 'file' && e.size != null ? FX.formatSize(e.size) : ''],
      ['שונה', e.modified ? FX.formatDate(e.modified) : '']
    ].filter(function (r) { return r[1]; });
    return '<div class="fx-pv-info"><span class="fx-ic k-' + kind + '">' + FX.icon(e.icon || FX.kindInfo(kind).icon) + '</span>' +
      '<b class="fx-pv-name" data-noi18n>' + FX.esc(e.name) + '</b>' +
      '<dl class="info-grid">' + rows.map(function (r) { return '<dt>' + r[0] + '</dt><dd>' + FX.esc(r[1]) + '</dd>'; }).join('') + '</dl></div>';
  }
  async function render(e, my) {
    var prov = FX.app.prov();
    var ext = FX.extOf(e.name);
    var head = infoHtml(e);
    if (e.type !== 'file') { previewBox(head); return; }
    var big = e.size != null && e.size > 20 * 1024 * 1024;
    if (big) { previewBox(head + '<p class="muted small fx-pv-note">הקובץ גדול לתצוגה מקדימה</p>'); return; }
    try {
      if (FX.isImageExt(ext)) {
        var d = await prov.read(e);
        if (my !== seq) return;
        previewBox('<div class="fx-pv-media"><img alt="" src="' + FX.esc(d.dataUrl || d.url || '') + '"></div>' + head);
      } else if (FX.isTextExt(ext)) {
        var t = await prov.read(e);
        if (my !== seq) return;
        previewBox(head + '<pre class="fx-pv-text" dir="auto" data-noi18n></pre>');
        $('fx-preview').querySelector('pre').textContent = String(t.text || '').slice(0, 20000);
      } else if (ext === 'pdf' && window.pdfjsLib) {
        previewBox(head + FX.ui.loading('טוען…'));
        window.pdfjsLib.GlobalWorkerOptions.workerSrc = 'vendor/pdf.worker.min.js';
        var bytes = await FX.readBytes(prov, e);
        if (my !== seq) return;
        var doc = await window.pdfjsLib.getDocument({ data: bytes, isEvalSupported: false }).promise;
        var page = await doc.getPage(1);
        if (my !== seq) { doc.destroy(); return; }
        var box = $('fx-preview');
        var w = Math.max(120, box.clientWidth - 24);
        var vp1 = page.getViewport({ scale: 1 });
        var dpr = window.devicePixelRatio || 1;
        var vp = page.getViewport({ scale: (w / vp1.width) * dpr });
        var c = document.createElement('canvas');
        c.width = Math.floor(vp.width); c.height = Math.floor(vp.height);
        c.style.width = '100%';
        await page.render({ canvasContext: c.getContext('2d'), viewport: vp }).promise;
        if (my !== seq) { doc.destroy(); return; }
        previewBox('<div class="fx-pv-media fx-pv-page"></div>' + head + '<p class="muted small fx-pv-note">' + FX.formatNumber(doc.numPages) + ' עמודים</p>');
        box.querySelector('.fx-pv-page').appendChild(c);
        doc.destroy();
      } else if (ext === 'docx' && window.JSZip) {
        previewBox(head + FX.ui.loading('טוען…'));
        var html = await FX.docxToHtml(await FX.readBytes(prov, e));
        if (my !== seq) return;
        previewBox(head + '<article class="fx-doc fx-pv-doc" data-noi18n>' + html.slice(0, 60000) + '</article>');
      } else {
        previewBox(head);
      }
    } catch (err) {
      if (my !== seq) return;
      previewBox(head + '<p class="muted small fx-pv-note">' + FX.esc(FX.errorText(err)) + '</p>');
    }
  }
  FX.onSelection = function () {
    if (!L.preview || !FX.app || !FX.app.selected) return;
    var sel = FX.app.selected();
    var e = sel.length === 1 ? sel[0] : null;
    var k = e ? e.path + '|' + (e.modified || '') : 'n' + sel.length;
    if (k === lastKey) return;
    lastKey = k;
    clearTimeout(timer);
    var my = ++seq;
    if (!e) {
      previewBox('<div class="fx-empty">' + FX.icon('preview') + '<span>' + (sel.length ? FX.formatNumber(sel.length) + ' פריטים נבחרו' : 'בחרו קובץ כדי לראות תצוגה מקדימה') + '</span></div>');
      return;
    }
    timer = setTimeout(function () { render(e, my); }, 180);
  };

  /* ---------- פעולות חיצוניות ---------- */
  FX.layout = {
    togglePreview: function () { L.preview = !L.preview; lastKey = ''; apply(); save(); if (L.preview) FX.onSelection(); },
    cycleMode: function () {
      var order = ['details', 'list', 'icons'];
      L.mode = order[(order.indexOf(L.mode) + 1) % order.length];
      apply(); save();
    },
    setMode: function (m) { L.mode = m; apply(); save(); },
    mode: function () { return L.mode; },
    zoom: function () { return L.zoom; },
    setZoom: function (z) { L.zoom = Math.max(0.7, Math.min(1.5, Math.round(z * 100) / 100)); apply(); save(); },
    zoomStep: function (d) {
      var i = ZOOMS.indexOf(L.zoom); if (i < 0) i = 2;
      L.zoom = ZOOMS[Math.max(0, Math.min(ZOOMS.length - 1, i + d))];
      apply(); save();
    },
    zooms: ZOOMS,
    init: function () {
      load();
      splitter($('fx-split-side'), 'side', 9, 30, 'start');
      splitter($('fx-split-prev'), 'prev', 14, 48, 'end');
      $('fx-preview-btn').addEventListener('click', FX.layout.togglePreview);
      $('fx-mode-btn').addEventListener('click', FX.layout.cycleMode);
      $('fx-preview-btn').innerHTML = FX.icon('preview');
      document.addEventListener('keydown', function (e) {
        if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        if (e.key === '+' || e.key === '=') { e.preventDefault(); FX.layout.zoomStep(1); }
        else if (e.key === '-') { e.preventDefault(); FX.layout.zoomStep(-1); }
        else if (e.key === '0') { e.preventDefault(); FX.layout.setZoom(0.9); }
      });
      apply();
    }
  };
})();
