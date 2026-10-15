// #65 — מתווך דיווחי תקלות: מקבל דיווח מהגשר ופותח issue ב-GitHub. הטוקן נשמר רק כאן (secret).
// הגבלות: גוף ≤ 8000 תווים, 5 דיווחים לשעה לכל IP, 30 ליום בסך הכול, כפילות לפי fingerprint (7 ימים) → 409.
export default {
  async fetch(req, env) {
    if (req.method !== 'POST') return new Response('POST only', { status: 405 });
    let d;
    try { d = await req.json(); } catch { return new Response('bad json', { status: 400 }); }
    const { title, body, fingerprint, version } = d || {};
    if (typeof title !== 'string' || typeof body !== 'string' || !/^[0-9a-f]{16}$/.test(fingerprint || '') ||
        !/^\d+\.\d+\.\d+/.test(version || '') || body.length > 8000 || title.length > 120)
      return new Response('invalid', { status: 400 });

    const kv = env.RELAY_KV, ip = req.headers.get('cf-connecting-ip') || 'x';
    const hour = Math.floor(Date.now() / 3600e3), day = Math.floor(hour / 24);
    if (await kv.get('fp:' + fingerprint)) return new Response('duplicate', { status: 409 });
    const ipKey = `ip:${ip}:${hour}`, dayKey = `day:${day}`;
    const ipN = +(await kv.get(ipKey) || 0), dayN = +(await kv.get(dayKey) || 0);
    if (ipN >= 5 || dayN >= 30) return new Response('rate', { status: 429 });
    await kv.put(ipKey, String(ipN + 1), { expirationTtl: 7200 });
    await kv.put(dayKey, String(dayN + 1), { expirationTtl: 172800 });

    const r = await fetch(`https://api.github.com/repos/${env.GH_REPO}/issues`, {
      method: 'POST',
      headers: { authorization: 'Bearer ' + env.GH_TOKEN, 'user-agent': 'achat-shaalti-relay', accept: 'application/vnd.github+json' },
      body: JSON.stringify({
        title: title.replace(/[@#]/g, ''),
        body: 'דיווח תקלה אוטומטי מהגשר (גרסה ' + version + ')\n\n```\n' + body.replace(/```/g, "'''").replace(/@/g, '(at)') + '\n```\n\nfingerprint: `' + fingerprint + '`',
        labels: ['auto-report']
      })
    });
    if (!r.ok) return new Response('github ' + r.status, { status: 502 });
    await kv.put('fp:' + fingerprint, '1', { expirationTtl: 7 * 86400 });
    return new Response('ok', { status: 201 });
  }
};
