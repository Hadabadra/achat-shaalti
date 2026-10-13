/* layout.js - פריסת הסייר: גרירת רוחב החלוניות, חלונית תצוגה מקדימה, מצבי תצוגה (פרטים/רשימה/סמלים) וגודל טקסט */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var KEY = 'fx.layout';
  var L = { side: 14, prev: 22, preview: false, details: false, mode: 'details', zoom: 0.9 };
  var ZOOMS = [0.8, 0.85, 0.9, 1, 1.1, 1.2, 1.35];
  /* סדר מצבי התצוגה: כמו בתפריט "תצוגה" של סייר הקבצים של Windows */
  var MODES = ['xlicons', 'licons', 'micons', 'sicons', 'list', 'details', 'tiles', 'content'];
  var MODE_META = {
    xlicons: { icon: 'grid', label: 'סמלים גדולים מאוד' },
    licons: { icon: 'grid', label: 'סמלים גדולים' },
    micons: { icon: 'grid', label: 'סמלים בינוניים' },
    sicons: { icon: 'grid', label: 'סמלים קטנים' },
    list: { icon: 'list', label: 'רשימה' },
    details: { icon: 'list', label: 'פרטים' },
    tiles: { icon: 'grid', label: 'אריחים' },
    content: { icon: 'list', label: 'תוכן' }
  };
  var $ = function (id) { return document.getElementById(id); };

  function load() {
    try { var v = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; Object.keys(v).forEach(function (k) { L[k] = v[k]; }); } catch (e) { /* כלום */ }
    if (L.mode === 'icons') L.mode = 'micons';   /* תאימות לאחור: שם המצב הישן "סמלים" (לפני גרסה עם 8 מצבי תצוגה) */
    if (MODES.indexOf(L.mode) < 0) L.mode = 'details';
  }
  function save() { try { localStorage.setItem(KEY, JSON.stringify(L)); } catch (e) { /* כלום */ } }
  function rtl() { return document.documentElement.dir !== 'ltr'; }
  function emPx(el) { return parseFloat(getComputedStyle(el).fontSize) || 16; }

  function apply() {
    var root = document.documentElement;
    root.style.setProperty('--fx-zoom', String(L.zoom));
    root.style.setProperty('--fx-side-w', L.side + 'em');
    root.style.setProperty('--fx-prev-w', L.prev + 'em');
    /* חלונית תצוגה מקדימה וחלונית פרטים חולקות אותו אזור בצד (בדיוק כמו בסייר הקבצים של Windows) */
    var paneOn = !!(L.preview || L.details);
    var pane = $('fx-preview');
    pane.hidden = !paneOn;
    pane.setAttribute('aria-label', L.details && !L.preview ? 'חלונית פרטים' : 'תצוגה מקדימה');
    $('fx-split-prev').hidden = !paneOn;
    var b = $('fx-preview-btn');
    if (b) { b.classList.toggle('on', !!L.preview); b.setAttribute('aria-pressed', L.preview ? 'true' : 'false'); }
    var db = $('fx-details-btn');
    if (db) { db.classList.toggle('on', !!L.details); db.setAttribute('aria-pressed', L.details ? 'true' : 'false'); }
    var list = $('fx-list'), main = $('fx-main');
    MODES.forEach(function (m) { list.classList.toggle('m-' + m, L.mode === m); main.classList.toggle('m-' + m, L.mode === m); });
    var mb = $('fx-mode-btn');
    if (mb) {
      var mm = MODE_META[L.mode] || MODE_META.details;
      mb.innerHTML = FX.icon(mm.icon);
      var t = 'תצוגה: ' + mm.label;
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
    if (!(L.preview || L.details) || !FX.app || !FX.app.selected) return;
    var sel = FX.app.selected();
    var e = sel.length === 1 ? sel[0] : null;
    var k = (L.details && !L.preview ? 'd:' : 'p:') + (e ? e.path + '|' + (e.modified || '') : 'n' + sel.length);
    if (k === lastKey) return;
    lastKey = k;
    clearTimeout(timer);
    var my = ++seq;
    if (!e) {
      previewBox('<div class="fx-empty">' + FX.icon('preview') + '<span>' + (sel.length ? FX.formatNumber(sel.length) + ' פריטים נבחרו' : (L.details && !L.preview ? 'בחרו פריט כדי לראות את פרטיו' : 'בחרו קובץ כדי לראות תצוגה מקדימה')) + '</span></div>');
      return;
    }
    /* חלונית פרטים בלבד (בלי תצוגה מקדימה פעילה): מידע קל בלבד, בלי לטעון את תוכן הקובץ */
    if (L.details && !L.preview) { previewBox(infoHtml(e)); return; }
    timer = setTimeout(function () { render(e, my); }, 180);
  };

  /* ---------- פעולות חיצוניות ---------- */
  FX.layout = {
    togglePreview: function () {
      L.preview = !L.preview;
      if (L.preview) L.details = false;   /* שתי החלוניות חולקות אותו אזור בצד: מפעילים אחת מכבים את השנייה */
      lastKey = ''; apply(); save();
      if (L.preview) FX.onSelection();
    },
    toggleDetailsPane: function () {
      L.details = !L.details;
      if (L.details) L.preview = false;
      lastKey = ''; apply(); save();
      if (L.details || L.preview) FX.onSelection();
    },
    setPanes: function (preview, details) { L.preview = !!preview; L.details = !!details; lastKey = ''; apply(); save(); },
    isPreview: function () { return !!L.preview; },
    isDetailsPane: function () { return !!L.details; },
    cycleMode: function () {
      L.mode = MODES[(MODES.indexOf(L.mode) + 1) % MODES.length];
      apply(); save();
    },
    setMode: function (m) { L.mode = MODES.indexOf(m) >= 0 ? m : L.mode; apply(); save(); },
    mode: function () { return L.mode; },
    modes: MODES,
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
      $('fx-details-btn').addEventListener('click', FX.layout.toggleDetailsPane);
      $('fx-mode-btn').addEventListener('click', FX.layout.cycleMode);
      $('fx-preview-btn').innerHTML = FX.icon('preview');
      $('fx-details-btn').innerHTML = FX.icon('info');
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
