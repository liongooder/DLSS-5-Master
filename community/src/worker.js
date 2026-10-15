// DLSS 5 Master community API (Cloudflare Worker + D1).
//
// Public:
//   GET  /v1/health
//   GET  /v1/games/<key>            summary + latest notes for one game  (header X-Install optional: adds "mine")
//   POST /v1/reports                create or replace this install's report for a game
//   POST /v1/reports/<id>/flag      flag a note; 3 flags from different installs hide it
// Moderator (Authorization: Bearer <ADMIN_TOKEN>):
//   GET    /v1/admin/flagged        reports with flags, newest first
//   GET    /v1/admin/recent         latest 100 reports with notes
//   POST   /v1/admin/reports/<id>/hide | /unhide
//   DELETE /v1/admin/reports/<id>
//
// Privacy: no accounts. The app sends a random install ID that is stored only as SHA-256(id + PEPPER).
// The client IP is used only for rate limiting (hashed per day) and is never stored with a report.

const RATINGS = new Set(['works', 'partly', 'broken']);
const ROUTES = new Set(['native', 'multipass', 'feeder', 'optiscaler']);
const KEY_RE = /^(steam:\d{1,10}|name:[a-z0-9]{1,60})$/;
const NOTE_MAX = 280;
const FLAGS_TO_HIDE = 3;
const LIMITS = { postPerInstall: 20, postPerIp: 60, readPerIp: 1200 };   // per hour

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.message }, e.status);
      console.error(e);
      return json({ error: 'Server error' }, 500);
    }
  },
};

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, '');
  const m = request.method;

  if (m === 'GET' && path === '/v1/health') return json({ ok: true });

  if (path.startsWith('/v1/admin/')) {
    await requireAdmin(request, env);
    return admin(request, env, path, m);
  }

  const ipHash = await sha256(`${request.headers.get('CF-Connecting-IP') || ''}|${env.PEPPER}|${dayNumber()}`);

  let g;
  if (m === 'GET' && (g = path.match(/^\/v1\/games\/([^/]+)$/))) {
    await limit(env, `read:${ipHash}`, LIMITS.readPerIp);
    return getGame(env, decodeURIComponent(g[1]), request.headers.get('X-Install'));
  }
  if (m === 'POST' && path === '/v1/reports') {
    await limit(env, `postip:${ipHash}`, LIMITS.postPerIp);
    return postReport(request, env);
  }
  if (m === 'POST' && (g = path.match(/^\/v1\/reports\/(\d+)\/flag$/))) {
    await limit(env, `postip:${ipHash}`, LIMITS.postPerIp);
    return flagReport(request, env, Number(g[1]));
  }
  throw new HttpError(404, 'Not found');
}

// ---------------------------------------------------------------- public

async function getGame(env, key, installHeader) {
  if (!KEY_RE.test(key)) throw new HttpError(400, 'Bad game key');
  const db = env.DB;
  const totals = await db.prepare(
    `SELECT rating, COUNT(*) AS n FROM reports WHERE game_key = ? GROUP BY rating`).bind(key).all();
  const byRoute = await db.prepare(
    `SELECT route, mfg, gpu_family,
            SUM(rating = 'works') AS works, SUM(rating = 'partly') AS partly, SUM(rating = 'broken') AS broken
       FROM reports WHERE game_key = ? GROUP BY route, mfg, gpu_family ORDER BY works DESC`).bind(key).all();
  const notes = await db.prepare(
    `SELECT id, rating, route, mfg, gpu, gpu_family, versions, app_version, note, updated_at
       FROM reports WHERE game_key = ? AND hidden = 0 AND note <> ''
      ORDER BY updated_at DESC LIMIT 30`).bind(key).all();

  const counts = { works: 0, partly: 0, broken: 0 };
  for (const r of totals.results) counts[r.rating] = r.n;

  let mine = null;
  if (installHeader && /^[0-9a-f]{32}$/i.test(installHeader)) {
    const install = await sha256(installHeader.toLowerCase() + env.PEPPER);
    mine = await db.prepare(
      `SELECT id, rating, route, mfg, note, hidden, updated_at FROM reports WHERE install = ? AND game_key = ?`)
      .bind(install, key).first();
  }
  return json({
    key,
    total: counts.works + counts.partly + counts.broken,
    counts,
    byRoute: byRoute.results,
    notes: notes.results.map(n => ({ ...n, mfg: !!n.mfg, versions: safeJson(n.versions) })),
    mine,
  });
}

async function postReport(request, env) {
  const body = await readJson(request);
  const installId = str(body.install, 32, 32).toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(installId)) throw new HttpError(400, 'Bad install id');
  const key = str(body.key, 1, 70);
  if (!KEY_RE.test(key)) throw new HttpError(400, 'Bad game key');
  const rating = str(body.rating, 1, 10);
  if (!RATINGS.has(rating)) throw new HttpError(400, 'Rating must be works, partly or broken');
  const routeName = str(body.route, 1, 20);
  if (!ROUTES.has(routeName)) throw new HttpError(400, 'Unknown route');
  const name = clean(str(body.name, 1, 100));
  const gpu = clean(str(body.gpu ?? 'Unknown', 1, 80));
  const family = clean(str(body.gpuFamily ?? 'Other', 1, 20));
  const note = clean(str(body.note ?? '', 0, NOTE_MAX * 2)).slice(0, NOTE_MAX);
  const appVersion = clean(str(body.appVersion ?? '', 0, 20));
  const versions = JSON.stringify(body.versions && typeof body.versions === 'object' ? body.versions : {}).slice(0, 400);
  const mfg = body.mfg ? 1 : 0;

  const install = await sha256(installId + env.PEPPER);
  await limit(env, `post:${install}`, LIMITS.postPerInstall);
  const now = Date.now();
  const before = await env.DB.prepare(`SELECT id, note FROM reports WHERE install = ? AND game_key = ?`).bind(install, key).first();
  await env.DB.prepare(
    `INSERT INTO reports (game_key, game_name, install, rating, route, mfg, gpu, gpu_family, versions, app_version, note, created_at, updated_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (install, game_key) DO UPDATE SET
       game_name = excluded.game_name, rating = excluded.rating, route = excluded.route, mfg = excluded.mfg,
       gpu = excluded.gpu, gpu_family = excluded.gpu_family, versions = excluded.versions,
       app_version = excluded.app_version, note = excluded.note, updated_at = excluded.updated_at,
       hidden = CASE WHEN reports.note = excluded.note THEN reports.hidden ELSE 0 END`)
    .bind(key, name, install, rating, routeName, mfg, gpu, family, versions, appVersion, note, now, now).run();
  // A changed note starts with a clean slate (the upsert above also un-hides it).
  if (before && before.note !== note) await env.DB.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(before.id).run();
  return json({ ok: true });
}

async function flagReport(request, env, id) {
  const body = await readJson(request);
  const installId = str(body.install, 32, 32).toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(installId)) throw new HttpError(400, 'Bad install id');
  const install = await sha256(installId + env.PEPPER);
  const report = await env.DB.prepare(`SELECT install FROM reports WHERE id = ?`).bind(id).first();
  if (!report) throw new HttpError(404, 'Not found');
  if (report.install === install) throw new HttpError(400, 'You cannot flag your own report');
  await env.DB.prepare(`INSERT OR IGNORE INTO flags (report_id, install, created_at) VALUES (?, ?, ?)`)
    .bind(id, install, Date.now()).run();
  const { n } = await env.DB.prepare(`SELECT COUNT(*) AS n FROM flags WHERE report_id = ?`).bind(id).first();
  if (n >= FLAGS_TO_HIDE) await env.DB.prepare(`UPDATE reports SET hidden = 1 WHERE id = ?`).bind(id).run();
  return json({ ok: true });
}

// ---------------------------------------------------------------- moderator

async function requireAdmin(request, env) {
  const auth = request.headers.get('Authorization') || '';
  const given = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  if (!env.ADMIN_TOKEN || given.length < 32 || !(await timingSafeEqual(given, env.ADMIN_TOKEN)))
    throw new HttpError(401, 'Unauthorized');
}

async function admin(request, env, path, m) {
  let g;
  if (m === 'GET' && path === '/v1/admin/flagged') {
    const r = await env.DB.prepare(
      `SELECT r.id, r.game_name, r.rating, r.note, r.hidden, COUNT(f.install) AS flags, r.updated_at
         FROM reports r JOIN flags f ON f.report_id = r.id GROUP BY r.id ORDER BY r.updated_at DESC LIMIT 200`).all();
    return json(r.results);
  }
  if (m === 'GET' && path === '/v1/admin/recent') {
    const r = await env.DB.prepare(
      `SELECT id, game_key, game_name, rating, route, gpu, note, hidden, updated_at
         FROM reports WHERE note <> '' ORDER BY updated_at DESC LIMIT 100`).all();
    return json(r.results);
  }
  if (m === 'POST' && (g = path.match(/^\/v1\/admin\/reports\/(\d+)\/(hide|unhide)$/))) {
    await env.DB.prepare(`UPDATE reports SET hidden = ? WHERE id = ?`).bind(g[2] === 'hide' ? 1 : 0, Number(g[1])).run();
    if (g[2] === 'unhide') await env.DB.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(Number(g[1])).run();
    return json({ ok: true });
  }
  if (m === 'DELETE' && (g = path.match(/^\/v1\/admin\/reports\/(\d+)$/))) {
    await env.DB.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(Number(g[1])).run();
    await env.DB.prepare(`DELETE FROM reports WHERE id = ?`).bind(Number(g[1])).run();
    return json({ ok: true });
  }
  throw new HttpError(404, 'Not found');
}

// ---------------------------------------------------------------- helpers

async function limit(env, bucket, max) {
  const win = Math.floor(Date.now() / 3600000);
  const row = await env.DB.prepare(
    `INSERT INTO hits (bucket, win, n) VALUES (?, ?, 1)
     ON CONFLICT (bucket, win) DO UPDATE SET n = n + 1 RETURNING n`).bind(bucket, win).first();
  if (row && row.n > max) throw new HttpError(429, 'Too many requests. Try again later.');
  if (Math.random() < 0.01) await env.DB.prepare(`DELETE FROM hits WHERE win < ?`).bind(win - 2).run();
}

async function readJson(request) {
  const text = await request.text();
  if (text.length > 4096) throw new HttpError(413, 'Too large');
  try { return JSON.parse(text); } catch { throw new HttpError(400, 'Bad JSON'); }
}

function str(v, min, max) {
  if (typeof v !== 'string' || v.length < min || v.length > max) throw new HttpError(400, 'Bad field');
  return v;
}

// Plain text only: drop control characters and collapse whitespace.
function clean(s) {
  return s.replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮⁦-⁩]/g, ' ').replace(/\s+/g, ' ').trim();
}

function safeJson(s) { try { return JSON.parse(s); } catch { return {}; } }
function dayNumber() { return Math.floor(Date.now() / 86400000); }

async function sha256(text) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, '0')).join('');
}

async function timingSafeEqual(a, b) {
  const [x, y] = await Promise.all([sha256(a), sha256(b)]);
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x.charCodeAt(i) ^ y.charCodeAt(i);
  return diff === 0;
}

function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' },
  });
}
