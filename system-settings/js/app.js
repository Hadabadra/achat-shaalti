/* app.js - shell, navigation, bridge probing and offline screen */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];
  SS.VERSION = '3.4.0';
  SS.BRIDGE_URL = 'https://github.com/e0548433917-gif/otzaria-system-tools/releases/latest';

  var state = { current: null, connected: false, probeTimer: null, suspended: false, booted: false };

  SS.openUrl = async function (url) {
    if (window.Otzaria) {
      /* אם הפתיחה נכשלת — מעתיקים את הקישור ללוח, כדי שאפשר להדביק אותו בדפדפן */
      var failed = async function () {
        var ok = await SS.copyText(url);
        ui.toast(ok ? 'לא ניתן לפתוח את הקישור, הוא הועתק ללוח' : 'לא ניתן לפתוח את הקישור: ' + ui.isolateLtr(url), 'error');
      };
      /* שם הקריאה המתועד הוא app.openUrl; app.open_url הוא גיבוי למקרה שהגרסה של אוצריא מכירה רק אותו */
      var names = ['app.openUrl', 'app.open_url'], lastErr = '';
      for (var i = 0; i < names.length; i++) {
        try {
          var r = await window.Otzaria.call(names[i], { url: url });
          if (!(r && r.success === false)) return;
          lastErr = names[i] + ': ' + ((r && (r.error || r.message)) || 'success=false');
        } catch (e) { lastErr = names[i] + ': ' + ((e && e.message) || e); }
      }
      try { console.warn('openUrl failed', lastErr); } catch (e) { /* אין קונסול */ }
      await failed();
    } else {
      ui.toast('בתוך אוצריא הקישור ייפתח בדפדפן', 'info');
    }
  };

  SS.copyText = async function (text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) { await navigator.clipboard.writeText(text); return true; }
    } catch (e) { /* נעבור לשיטה הישנה */ }
    var ta = document.createElement('textarea');
    ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    ta.remove();
    return ok;
  };

  /* ---------- ממסר זמני היום לשומר המסך (#33): רק כאן (בתוך אוצריא) אפשר לקרוא ל-calendar.*;
     הגשר (תהליך Windows נפרד) לא יכול. שולחים לו מדי דקה POST ‏/kiosk/zmanim-cache עם מה שהתקבל,
     והוא רק שומר ומציג. בלי window.Otzaria (תצוגה מקדימה בדפדפן) זה פשוט לא רץ. */
  var zmanimTimer = null;
  function unwrapOtzaria(r) { return r && r.data !== undefined ? r.data : r; }
  async function pushZmanimCache() {
    if (!window.Otzaria || !state.connected) return;
    var st;
    try { st = await SS.bridge.call('/kiosk/status'); } catch (e) { return; }
    var city = (st.zmanimCity || '').trim();
    var loc = (st.zmanimLocation || '').trim();
    var args = { inIsrael: !st.zmanimDiaspora };
    if (city) args.city = city;
    else {
      var m = /^(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)$/.exec(loc);
      if (m) { args.lat = +m[1]; args.lng = +m[2]; }
      else return; /* אין עיר ואין קואורדינטות — אין מה לשלוח */
    }
    var dailyTimes = null, jewishDate = null, jewishDateTomorrow = null;
    try { dailyTimes = unwrapOtzaria(await window.Otzaria.call('calendar.getDailyTimes', args)); } catch (e) { dailyTimes = null; }
    try { jewishDate = unwrapOtzaria(await window.Otzaria.call('calendar.getJewishDate', {})); } catch (e) { jewishDate = null; }
    try {
      var t = new Date(); t.setDate(t.getDate() + 1);
      jewishDateTomorrow = unwrapOtzaria(await window.Otzaria.call('calendar.getJewishDate', { date: t.toISOString().slice(0, 10) }));
    } catch (e) { jewishDateTomorrow = null; }
    if (!dailyTimes && !jewishDate) return;
    try {
      await SS.bridge.call('/kiosk/zmanim-cache', {
        dailyTimes: dailyTimes || {}, jewishDate: jewishDate || {}, jewishDateTomorrow: jewishDateTomorrow || {}
      });
    } catch (e) { /* המטמון אינו קריטי; ננסה שוב בפעם הבאה */ }
  }
  function scheduleZmanim() {
    clearTimeout(zmanimTimer);
    if (state.suspended || !state.connected) return;
    zmanimTimer = setTimeout(function () { pushZmanimCache().finally(scheduleZmanim); }, 60000);
  }
  /* נקרא גם מיד אחרי חיבור/שחזור, וגם אחרי שמירת הגדרות הנעילה, כדי שהתצוגה תתעדכן בלי לחכות דקה */
  SS.pushZmanimNow = function () { clearTimeout(zmanimTimer); pushZmanimCache().finally(scheduleZmanim); };

  function sectionById(id) {
    return SS.sections.find(function (s) { return s.id === id; });
  }

  /* סדר וקבוצות כמו בהגדרות של Windows */
  var ORDER = ['display', 'audio', 'energy', 'time', 'keyboard', 'mouse', 'printer', 'usb', 'bluetooth', 'wifi', 'airplane', 'programs', 'kiosk', 'fxpolicy', 'screensaver', 'system', 'about'];
  var GROUPS = [
    ['system', 'מערכת', ['display', 'audio', 'energy', 'time', 'system']],
    ['devices', 'התקנים', ['keyboard', 'mouse', 'printer', 'usb', 'bluetooth']],
    ['network', 'רשת', ['wifi', 'airplane']],
    ['study', 'לימוד ונעילה', ['programs', 'kiosk', 'fxpolicy', 'screensaver']],
    ['about', '', ['about']]
  ];
  /* הגדרות שאפשר לסגור בנעילה (חייב להתאים ל-Kiosk.AllSections בגשר) */
  var LOCKABLE = ['audio', 'display', 'energy', 'keyboard', 'mouse', 'printer', 'time', 'wifi', 'bluetooth', 'airplane', 'usb'];
  var NETWORK = ['wifi', 'bluetooth', 'airplane'];

  SS.prefs = {
    get: function (k, def) { try { var v = localStorage.getItem('ss.' + k); return v == null ? def : JSON.parse(v); } catch (e) { return def; } },
    set: function (k, v) { try { localStorage.setItem('ss.' + k, JSON.stringify(v)); } catch (e) { /* ignore */ } }
  };
  /* מצב מנותק: מחשב בלי רשת — מסתירים Wi-Fi, בלוטות׳ ומצב טיסה */
  /* #8 (13.10.2026): נלקח ממצב המנותק של אוצריא עצמה דרך הגשר — אין הגדרה נפרדת בתוסף */
  /* #13: מצב קומפקטי (אייקונים בלבד), נשמר בהעדפות */
  function applyCompact() {
    var on = !!SS.prefs.get('compact', false);
    document.body.classList.toggle('ss-compact', on);
    var b = document.getElementById('top-compact');
    if (b) { b.setAttribute('aria-pressed', on ? 'true' : 'false'); b.innerHTML = ui.icon('display'); }
  }
  document.addEventListener('DOMContentLoaded', function () {
    var b = document.getElementById('top-compact');
    if (b) b.addEventListener('click', function () { SS.prefs.set('compact', !SS.prefs.get('compact', false)); applyCompact(); });
    applyCompact();
  });
  setTimeout(applyCompact, 0);
  SS._offline = false;
  SS.isOfflineMode = function () { return !!SS._offline; };
  async function syncOffline() {
    try { var r = await SS.bridge.call('/sys/offline', {}); var on = !!(r && r.offline); if (on !== SS._offline) { SS._offline = on; refreshNav(); } } catch (e) { }
  }
  SS.lockOpen = null; /* רשימת ההגדרות הפתוחות בנעילה, כשהנעילה פעילה */

  function visible(s) {
    if (SS.isOfflineMode() && NETWORK.indexOf(s.id) >= 0) return false;
    if (SS.lockOpen && LOCKABLE.indexOf(s.id) >= 0 && SS.lockOpen.indexOf(s.id) < 0) return false;
    return true;
  }
  SS.sectionVisible = function (id) { var s = sectionById(id); return !!s && visible(s); };

  function buildNav() {
    SS.sections.sort(function (a, b) {
      var x = ORDER.indexOf(a.id), y = ORDER.indexOf(b.id);
      return (x < 0 ? 99 : x) - (y < 0 ? 99 : y);
    });
    var nav = document.getElementById('nav');
    nav.innerHTML = '<label class="nav-search">' + ui.icon('search') +
      '<input type="search" id="nav-q" placeholder="חיפוש הגדרה" aria-label="חיפוש הגדרה" autocomplete="off"></label><div id="nav-list"></div>';
    nav.addEventListener('click', function (e) {
      var b = e.target.closest('.nav-item');
      if (b) show(b.dataset.id);
    });
    var q = nav.querySelector('#nav-q');
    q.addEventListener('input', function () { refreshNav(); });
    q.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { var first = nav.querySelector('.nav-item'); if (first) show(first.dataset.id); }
      if (e.key === 'Escape') { q.value = ''; refreshNav(); }
    });
    refreshNav();
  }

  function refreshNav() {
    var list = document.getElementById('nav-list');
    if (!list) return;
    var qEl = document.getElementById('nav-q');
    var q = (qEl && qEl.value || '').trim().toLowerCase();
    var html = '';
    GROUPS.forEach(function (g) {
      var items = g[2].map(sectionById).filter(function (s) {
        if (!s || !visible(s)) return false;
        if (!q) return true;
        return (s.title + ' ' + (s.keywords || '') + ' ' + (window.I18N ? window.I18N.t(s.title) : '')).toLowerCase().indexOf(q) >= 0;
      });
      if (!items.length) return;
      if (g[1]) html += '<div class="nav-group">' + ui.esc(g[1]) + '</div>';
      html += items.map(function (s) {
        return '<button class="nav-item' + (s.id === state.current ? ' active' : '') + '" data-id="' + s.id + '">' + ui.icon(s.icon) + '<span>' + ui.esc(s.title) + '</span></button>';
      }).join('');
    });
    list.innerHTML = html || '<div class="empty small">לא נמצאו הגדרות</div>';
    if (state.current && !SS.sectionVisible(state.current) && !q) show('about');
  }
  SS.refreshNav = refreshNav;

  /* בזמן נעילה: מביאים מהגשר אילו הגדרות פתוחות */
  async function syncLock(h) {
    var prev = JSON.stringify(SS.lockOpen);
    if (h && h.kioskActive) {
      try { SS.lockOpen = (await SS.bridge.call('/kiosk/status')).openSections || []; } catch (e) { SS.lockOpen = SS.lockOpen || []; }
    } else SS.lockOpen = null;
    if (JSON.stringify(SS.lockOpen) !== prev) refreshNav();
  }
  SS.syncLock = syncLock;

  function show(id) {
    var s = sectionById(id);
    if (!s || !visible(s)) s = SS.sections.find(visible) || SS.sections[0];
    state.current = s.id;
    document.querySelectorAll('.nav-item').forEach(function (b) {
      b.classList.toggle('active', b.dataset.id === s.id);
      b.setAttribute('aria-current', b.dataset.id === s.id ? 'page' : 'false');
    });
    var main = document.getElementById('content');
    main.innerHTML = '';
    var wrap = ui.el('<section class="section fade-in" data-section="' + s.id + '">' +
      '<div class="section-head"><div class="section-title">' + ui.icon(s.icon) + '<h2>' + ui.esc(s.title) + '</h2></div>' +
      '<button class="btn btn-tonal btn-icon-text" data-act="refresh" title="רענון">' + ui.icon('refresh') + '<span>רענון</span></button></div>' +
      '<div class="section-body"></div></section>');
    main.appendChild(wrap);
    main.scrollTop = 0;
    var body = wrap.querySelector('.section-body');
    wrap.querySelector('[data-act=refresh]').addEventListener('click', function () {
      var btn = this;
      btn.classList.add('spinning');
      Promise.resolve(s.load(body)).finally(function () { btn.classList.remove('spinning'); });
    });
    s.mount(body);
    s.load(body);
    try { localStorage.setItem('ss.section', s.id); } catch (e) { /* ignore */ }
  }
  SS.show = show;

  function setStatus(connected) {
    var chip = document.getElementById('bridge-chip');
    var h = SS.bridge.health;
    chip.className = 'topbar-chip ' + (connected ? (h && h.kioskActive ? 'warn' : 'ok') : 'bad');
    chip.innerHTML = '<i class="dot"></i>' + (connected
      ? (h && h.kioskActive ? 'מצב נעילה פעיל' : 'הגשר מחובר') + (SS.bridge.isMock ? ' · תצוגה מקדימה' : '')
      : 'הגשר אינו מחובר');
  }
  SS.setStatus = setStatus;

  function showOffline() {
    /* notifications.system: התראה כשהגשר נופל (רק במעבר ממחובר למנותק) */
    if (state.connected && window.SdkX) SdkX.notify('גשר אוצריא אינו מחובר', 'הגדרות המערכת ומצב הנעילה אינם זמינים עד שהגשר יחזור');
    state.connected = false;
    setStatus(false);
    document.getElementById('shell-body').hidden = true;
    var off = document.getElementById('offline');
    off.hidden = false;
    if (!off.dataset.built) {
      off.dataset.built = '1';
      off.innerHTML =
        '<div class="offline-card fade-in">' +
        '<div class="offline-art">' + ui.icon('bridge') + '</div>' +
        '<h2>צריך את אחת שאלתי</h2>' +
        '<p>כדי לשנות הגדרות של המחשב (שמע, מסך, בלוטות׳, רשת ונעילה), התוסף נעזר בתוכנה קטנה בשם <b>אחת שאלתי</b> שרצה ברקע במחשב שלכם. נראה שהיא עדיין לא מותקנת או לא פועלת.</p>' +
        '<ol class="steps">' +
        '<li>מורידים את <b>OtzariaBridge.exe</b> מהקישור למטה.</li>' +
        '<li>מפעילים אותו פעם אחת — הוא מתקין את עצמו ויעלה אוטומטית בכל הפעלה של Windows.</li>' +
        '<li>חוזרים לכאן — החיבור יזוהה מעצמו.</li></ol>' +
        '<div class="row-actions center">' +
        '<button class="btn btn-primary btn-lg" id="off-dl">' + ui.icon('download') + 'פתח בדפדפן</button>' +
        '<button class="btn btn-tonal btn-lg" id="off-copy">' + ui.icon('link') + 'העתק קישור</button>' +
        '<button class="btn btn-tonal btn-lg" id="off-retry">' + ui.icon('refresh') + 'נסה שוב</button></div>' +
        '<p class="muted small" id="off-note">בודק שוב אוטומטית כל 10 שניות…</p></div>';
      off.querySelector('#off-dl').addEventListener('click', function () { SS.openUrl(SS.BRIDGE_URL); });
      off.querySelector('#off-copy').addEventListener('click', function () {
        SS.copyText(SS.BRIDGE_URL).then(function (ok) {
          ui.toast(ok ? 'הקישור הועתק' : 'ההעתקה נכשלה: ' + ui.isolateLtr(SS.BRIDGE_URL), ok ? 'success' : 'error');
        });
      });
      off.querySelector('#off-retry').addEventListener('click', function () {
        var b = this; b.disabled = true;
        var note = off.querySelector('#off-note'); note.textContent = 'מחפש את הגשר…';
        connect().then(function (ok) {
          b.disabled = false;
          if (!ok) note.textContent = 'הגשר לא נמצא. בודק שוב אוטומטית כל 10 שניות…';
        });
      });
    }
    scheduleProbe();
  }

  function scheduleProbe() {
    clearTimeout(state.probeTimer);
    if (state.suspended) return;
    state.probeTimer = setTimeout(function () {
      connect().then(function (ok) { if (!ok) scheduleProbe(); });
    }, 10000);
  }

  async function connect() {
    var h = null;
    try { h = await SS.bridge.probe(); } catch (e) { h = null; }
    if (!h) { if (state.connected || !document.getElementById('offline').dataset.built) showOffline(); return false; }
    clearTimeout(state.probeTimer);
    var wasConnected = state.connected;
    state.connected = true;
    setStatus(true);
    document.getElementById('offline').hidden = true;
    document.getElementById('shell-body').hidden = false;
    await syncLock(h);
    syncOffline();
    if (!wasConnected) setTimeout(sendPendingReports, 4000);
    if (!wasConnected) {
      var start = null;
      try { start = localStorage.getItem('ss.section'); } catch (e) { start = null; }
      var hash = (location.hash || '').replace('#', '');
      show(hash || start || (h.kioskActive ? 'kiosk' : 'audio'));
    }
    SS.pushZmanimNow();
    return true;
  }
  SS.reprobe = connect;

  /* תקלות שהגשר אסף אוטומטית (קריסות ושגיאות פנימיות) נשלחות בדיווח אחד דרך feedback.report.
     אוצריא מבקשת אישור לפני שליחה, ואם אין חיבור היא שומרת ושולחת כשיחזור. */
  var reportsSent = false;
  async function sendPendingReports() {
    if (reportsSent || !window.Otzaria || SS.bridge.isMock) return;
    var r = null;
    try { r = await SS.bridge.call('/diag/pending', {}, { timeoutMs: 15000 }); } catch (e) { return; }
    if (!r || r.enabled === false || !r.items || !r.items.length) return;
    reportsSent = true;
    var tail = '';
    try { tail = ((await SS.bridge.call('/diag/logTail', {}, { timeoutMs: 15000 })) || {}).text || ''; } catch (e) { tail = ''; }
    var details = ('דיווח תקלות אוטומטי מהגשר (' + r.items.length + ')\nתוסף: הגדרות מערכת ' + SS.VERSION + '\n\n' +
      r.items.map(function (x) { return x.text; }).join('\n\n=====\n\n') + (tail ? '\n\n===== יומן מקוצר =====\n' + tail : '')).slice(0, 5000);
    var res = null;
    try { res = await window.Otzaria.call('feedback.report', { details: details, reportType: 'bug' }); } catch (e) { res = null; }
    var val = res && (res.data !== undefined ? res.data : res);
    if (val === 'sent' || val === 'queued') {
      var ids = r.items.map(function (x) { return x.id; });
      try { await SS.bridge.call('/diag/markSent', { ids: ids, details: details }, { timeoutMs: 15000 }); }
      catch (e) { try { await SS.bridge.call('/diag/ack', { ids: ids }, { timeoutMs: 15000 }); } catch (e2) { /* ננסה בפעם הבאה */ } }
    }
    reportsSent = false; /* מינימלי: מאפשר שליחה חוזרת בסבב הבא */
  }
  setInterval(function () { if (state.connected) sendPendingReports(); }, 30 * 60 * 1000);
  SS.sendPendingReports = sendPendingReports;

  SS.bridge.onPairing = function (on) {
    if (on) SS.ui.toast(window.I18N && window.I18N.isEn() ? 'Approve the connection in the window that opened on the computer' : 'יש לאשר את החיבור בחלון שנפתח במחשב', 'info');
  };
  SS.bridge.onLost = function () {
    if (!state.connected) return;
    SS.bridge.probe().then(function (h) { if (!h) showOffline(); else setStatus(true); });
  };

  async function boot(payload) {
    if (state.booted) return;
    state.booted = true;
    if (payload && payload.theme) ui.applyTheme(payload.theme);
    try { await window.I18N.init(payload); } catch (e) { /* נשארים בעברית */ }
    var lb = document.getElementById('top-lang');
    lb.textContent = window.I18N.toggleLabel();
    lb.addEventListener('click', function () { window.I18N.toggle(); });
    var fb = document.getElementById('top-feedback');
    fb.innerHTML = window.Feedback.ICON;
    fb.addEventListener('click', function () {
      window.Feedback.open({
        ui: ui, plugin: 'הגדרות מערכת', version: SS.VERSION,
        extra: function () { return ['גשר: ' + (state.connected ? 'מחובר' : 'לא מחובר')]; }
      });
    });
    if (payload && payload.plugin && payload.plugin.version) SS.VERSION = payload.plugin.version;
    buildNav();
    document.getElementById('top-refresh').addEventListener('click', function () {
      if (!state.connected) { connect(); return; }
      var s = sectionById(state.current);
      var body = document.querySelector('.section-body');
      if (s && body) s.load(body);
      SS.bridge.probe().then(function (h) { setStatus(!!h); if (!h) showOffline(); });
    });
    connect();
  }

  function init() {
    if (window.Otzaria) {
      window.Otzaria.on('plugin.boot', boot);
      window.Otzaria.on('theme.changed', ui.applyTheme);
      window.Otzaria.on('calendar.city_changed', function () { pushZmanimCache(); });
      window.Otzaria.on('plugin.suspended', function () { state.suspended = true; clearTimeout(state.probeTimer); clearTimeout(zmanimTimer); });
      window.Otzaria.on('plugin.resumed', function () {
        state.suspended = false;
        if (!state.connected) connect();
        else SS.bridge.probe().then(function (h) { if (!h) showOffline(); else { setStatus(true); SS.pushZmanimNow(); } });
      });
    } else {
      document.body.classList.add('preview');
      if (/[?&]dark=1/.test(location.search)) document.body.classList.add('dark-mode');
      boot(null);
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
