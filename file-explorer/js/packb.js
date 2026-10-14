/* packb.js — חבילה [ב] (מימוש מינימלי): ZIP, חילוץ, שליחה לכונן נייד, הוצאה בטוחה, גודל תיקייה */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var ui = FX.ui;
  var removable = [];

  function bridgeOn() { var p = FX.app && FX.app.prov(); return !!(p && p.kind === 'bridge'); }
  function isZip(e) { return e && e.type === 'file' && /\.zip$/i.test(e.name || e.path || ''); }
  function parentDir(p) { return FX.win.parent(p); }

  function loadRemovable() {
    if (!bridgeOn()) return;
    FX.Bridge.call('/fs/roots', {}).then(function (d) {
      removable = (d && d.drives || []).filter(function (x) { return x.type === 'removable' && x.totalBytes; });
    }).catch(function () { removable = []; });
  }

  function fail(e, prefix) { ui.toast(prefix + ': ' + ui.errText(e), 'error'); }
  function done(msg) { ui.toast(msg, 'success'); if (FX.app) FX.app.refresh(); }

  FX.packb = {
    items: function (sel, inFolder) {
      loadRemovable();
      if (!bridgeOn()) return [];
      var out = [];
      var files = sel.filter(function (e) { return e.type !== 'drive'; });
      if (files.length && files.length === sel.length) out.push('zip');
      if (sel.length === 1 && isZip(sel[0])) out.push('unzip');
      if (files.length && files.length === sel.length && removable.length) out.push('sendTo');
      if (sel.length === 1 && sel[0].type === 'drive' && sel[0].driveType === 'removable') out.push('eject');
      return out;
    },
    state: function (id) { return { on: ['zip', 'unzip', 'sendTo', 'eject'].indexOf(id) >= 0 && bridgeOn() }; },

    zip: async function (sel) {
      var paths = sel.map(function (e) { return e.path; });
      var dir = parentDir(paths[0]);
      var base = sel.length === 1 ? sel[0].name.replace(/\.[^.\\]+$/, '') || sel[0].name : 'ארכיון';
      try {
        var r = await FX.Bridge.call('/fs/zip', { paths: paths, dest: FX.win.join(dir, base + '.zip') }, { timeoutMs: 600000 });
        done('נוצר ' + FX.win.name(r.path));
      } catch (e) { fail(e, 'הדחיסה נכשלה'); }
    },

    unzip: async function (sel) {
      try {
        var r = await FX.Bridge.call('/fs/unzip', { path: sel[0].path }, { timeoutMs: 600000 });
        done('חולצו ' + r.files + ' קבצים אל ' + FX.win.name(r.path));
      } catch (e) { fail(e, 'החילוץ נכשל'); }
    },

    sendTo: async function (sel) {
      var dest = removable.length === 1 ? removable[0].path : await ui.dialog({
        title: 'שליחה לכונן נייד', body: 'לאיזה כונן?', icon: 'info',
        actions: [{ label: 'ביטול', value: null }].concat(removable.map(function (d) {
          return { label: (d.label ? d.label + ' ' : '') + '(' + d.path.slice(0, 2) + ')', value: d.path, kind: 'primary' };
        }))
      });
      if (!dest) return;
      try {
        await FX.BridgeProvider.copy(sel.map(function (e) { return e.path; }), dest, 'rename');
        ui.toast('נשלח אל ' + dest.slice(0, 2), 'success');
      } catch (e) { fail(e, 'השליחה נכשלה'); }
    },

    eject: async function (sel) {
      try {
        await FX.Bridge.call('/sys/usb/eject', { letter: sel[0].path.slice(0, 2) }, { timeoutMs: 30000 });
        done('אפשר לנתק את הכונן בבטחה');
      } catch (e) { fail(e, 'ההוצאה נכשלה'); }
    },

    dirSize: async function (path) {
      try { return await FX.Bridge.call('/fs/dirsize', { path: path }, { timeoutMs: 30000 }); }
      catch (e) { return null; }   // גשר ישן — נשאר הנתון הרגיל
    }
  };
})();
