// DLSS 5 Master community API (Cloudflare Worker + D1).
//
// Identity: no accounts. The app sends a random 32-hex install ID (header X-Install or body "install"); the server
// stores only SHA-256(id + PEPPER). Players choose a nickname; the developer's install is marked with is_dev.
// Text is plain text everywhere; the app never renders it as markup. Pictures are JPEG/PNG shrunk by the app.
//
// Public
//   GET  /v1/health
//   GET  /v1/me                          nickname, developer flag, ban, unread private replies
//   PUT  /v1/me                          { install, nickname }
//   GET  /v1/games                       list: ?route=&result=working|mixed|broken&q=&sort=recent|reports|name&offset=
//   GET  /v1/games/<key>                 one game: counts, per-route results, notes, your report
//   POST /v1/reports                     create/replace your report for a game
//   POST /v1/reports/<id>/flag           3 flags from different nicknamed installs hide a note
//   GET  /v1/private                     your private thread with the developer
//   POST /v1/private                     { install, text, image? }
//   GET  /v1/gallery                     approved pictures, newest first (?offset=)
//   GET  /v1/gallery/mine                your uploads and their status
//   POST /v1/gallery                     { install, caption, game, image }  -> pending until approved
//   GET  /v1/images/<id>                 a picture (approved gallery, or your own / private for its owner)
//   GET  /v1/chat                        ?after=<id> (newer) or ?before=<id> (older); 50 at a time
//   POST /v1/chat                        { install, text, replyTo? }
//   POST /v1/chat/<id>/react             { install, emoji, on }
//   DELETE /v1/chat/<id>                 your own message ({ install } in the body)
// Moderator (Authorization: Bearer <ADMIN_TOKEN>) under /v1/admin/... — see admin() below.

const RATINGS = new Set(['works', 'partly', 'broken']);
const ROUTES = new Set(['native', 'multipass', 'feeder', 'optiscaler']);
// Rendering API of the reported game ('' on reports sent before 0.8.0). "older" in the games filter means DX10 and below or OpenGL.
const APIS = new Set(['dx12', 'dx11', 'dx10', 'dx9', 'dx8', 'ddraw', 'vulkan', 'opengl']);
const OLDER_APIS = ['dx10', 'dx9', 'dx8', 'ddraw', 'opengl'];
const FAMILY_RE = /^[A-Za-z0-9 ]{1,20}$/;
const KEYS_MAX = 3000;
const EMOJI = new Set(['👍', '❤️', '😂', '😮', '😢', '🔥']);
const KEY_RE = /^(steam:\d{1,10}|name:[a-z0-9]{1,60})$/;
const INSTALL_RE = /^[0-9a-f]{32}$/;
const NICK_RE = /^[A-Za-z0-9][A-Za-z0-9 _.\-]{1,18}[A-Za-z0-9_.\-]$/;
// Names nobody but the developer may use: exact short words, or containing the longer ones.
const RESERVED_EXACT = ['dev', 'mod', 'staff', 'support', 'system', 'dlss5'];
const RESERVED_PART = ['developer', 'admin', 'moderator', 'official', 'dlss5master', 'liongooder'];
const NOTE_MAX = 280, CHAT_MAX = 500, PRIVATE_MAX = 2000, CAPTION_MAX = 140;
const IMAGE_MAX = 900 * 1024;
const FLAGS_TO_HIDE = 3;
const PAGE = 30;
// Per hour unless noted.
const LIMITS = { postPerInstall: 20, postPerIp: 120, readPerIp: 3000, chatPerInstall: 60, uploadsPerInstall: 10, privatePerInstall: 30,
  // Install IDs cost nothing to make, so these also count per connection (hashed IP): one script cannot stuff a game's
  // ratings, hide notes with a few throwaway IDs, or fill the picture store.
  reportsPerIpPerGame: 3, flagsPerIp: 10, imagesPerIp: 20 };

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
  const q = url.searchParams;
  let g;

  if (m === 'GET' && path === '/v1/health') return json({ ok: true });
  if (path.startsWith('/v1/admin/')) {
    await requireAdmin(request, env);
    return admin(request, env, path, m, q);
  }

  const ipHash = await sha256(`${request.headers.get('CF-Connecting-IP') || ''}|${env.PEPPER}|${dayNumber()}`);
  if (m === 'GET') await limit(env, `read:${ipHash}`, LIMITS.readPerIp);
  else await limit(env, `postip:${ipHash}`, LIMITS.postPerIp);

  if (m === 'GET' && path === '/v1/me') return getMe(env, await installFromHeader(request, env));
  if (m === 'PUT' && path === '/v1/me') return putMe(env, await readJson(request));

  if (m === 'GET' && path === '/v1/games') return listGames(env, q, await installFromHeader(request, env, true));
  // The same list limited to the games in the caller's library ({ keys: [...] }); too many keys for a query string.
  if (m === 'POST' && path === '/v1/games') {
    const body = await readJson(request, true);
    if (!Array.isArray(body.keys) || body.keys.length > KEYS_MAX) throw new HttpError(400, 'Bad keys');
    return listGames(env, q, await installFromHeader(request, env, true), new Set(body.keys.filter(k => typeof k === 'string' && KEY_RE.test(k))));
  }
  if (m === 'GET' && (g = path.match(/^\/v1\/games\/([^/]+)$/)))
    return getGame(env, decodeURIComponent(g[1]), await installFromHeader(request, env, true));
  if (m === 'POST' && path === '/v1/reports') return postReport(env, await readJson(request), ipHash);
  if (m === 'POST' && (g = path.match(/^\/v1\/reports\/(\d+)\/flag$/))) return flagReport(env, await readJson(request), Number(g[1]), ipHash);

  if (m === 'GET' && path === '/v1/private') return getPrivate(env, await installFromHeader(request, env));
  if (m === 'POST' && path === '/v1/private') return postPrivate(env, await readJson(request, true), ipHash);

  if (m === 'GET' && path === '/v1/gallery') return listGallery(env, q);
  if (m === 'GET' && path === '/v1/gallery/mine') return myGallery(env, await installFromHeader(request, env));
  if (m === 'POST' && path === '/v1/gallery') return postGallery(env, await readJson(request, true), ipHash);
  if (m === 'GET' && (g = path.match(/^\/v1\/images\/(\d+)$/)))
    return getImage(env, Number(g[1]), await installFromHeader(request, env, true));

  if (m === 'GET' && path === '/v1/chat') return listChat(env, q, await installFromHeader(request, env, true));
  if (m === 'POST' && path === '/v1/chat') return postChat(env, await readJson(request));
  if (m === 'POST' && (g = path.match(/^\/v1\/chat\/(\d+)\/react$/))) return reactChat(env, await readJson(request), Number(g[1]));
  if (m === 'DELETE' && (g = path.match(/^\/v1\/chat\/(\d+)$/))) return deleteOwnChat(env, await readJson(request), Number(g[1]));

  throw new HttpError(404, 'Not found');
}

// ---------------------------------------------------------------- identity

async function hashInstall(id, env) {
  if (typeof id !== 'string' || !INSTALL_RE.test(id.toLowerCase())) throw new HttpError(400, 'Bad install id');
  return sha256(id.toLowerCase() + env.PEPPER);
}

async function installFromHeader(request, env, optional = false) {
  const h = request.headers.get('X-Install');
  if (!h) { if (optional) return null; throw new HttpError(400, 'Missing install id'); }
  return hashInstall(h, env);
}

async function profile(env, install) {
  return install ? env.DB.prepare(`SELECT nickname, is_dev, banned FROM profiles WHERE install = ?`).bind(install).first() : null;
}

/** Posting needs a nickname and no ban. */
async function poster(env, install) {
  const p = await profile(env, install);
  if (!p) throw new HttpError(403, 'Choose a nickname first.');
  if (p.banned) throw new HttpError(403, 'You can no longer post in the community.');
  return p;
}

async function getMe(env, install) {
  const p = await profile(env, install);
  const unread = await env.DB.prepare(`SELECT COUNT(*) AS n FROM private_msgs WHERE install = ? AND from_dev = 1 AND read_by_user = 0`).bind(install).first();
  return json({ nickname: p?.nickname ?? null, isDev: !!p?.is_dev, banned: !!p?.banned, unreadPrivate: unread.n });
}

async function putMe(env, body) {
  const install = await hashInstall(body.install, env);
  await limit(env, `nick:${install}`, 10);
  const nickname = clean(str(body.nickname, 3, 40));
  if (!NICK_RE.test(nickname)) throw new HttpError(400, 'Use 3 to 20 letters, digits, spaces, dots, dashes or underscores.');
  const lc = nickname.toLowerCase();
  const p = await profile(env, install);
  const bare = lc.replace(/[ _.\-]/g, '');
  if (!p?.is_dev && (RESERVED_EXACT.includes(bare) || RESERVED_PART.some(r => bare.includes(r)))) throw new HttpError(400, 'That nickname is reserved.');
  if (p?.banned) throw new HttpError(403, 'You can no longer post in the community.');
  const taken = await env.DB.prepare(`SELECT install FROM profiles WHERE nickname_lc = ?`).bind(lc).first();
  if (taken && taken.install !== install) throw new HttpError(409, 'That nickname is taken.');
  const now = Date.now();
  await env.DB.prepare(
    `INSERT INTO profiles (install, nickname, nickname_lc, created_at, updated_at) VALUES (?, ?, ?, ?, ?)
     ON CONFLICT (install) DO UPDATE SET nickname = excluded.nickname, nickname_lc = excluded.nickname_lc, updated_at = excluded.updated_at`)
    .bind(install, nickname, lc, now, now).run();
  return json({ ok: true, nickname });
}

// ---------------------------------------------------------------- games and reports

function status(w, p, b) {
  if (w + p + b === 0) return 'none';
  if (b === 0 && w >= p) return 'working';
  if (w === 0 && p === 0) return 'broken';
  if (b > w + p) return 'broken';
  return 'mixed';
}

/**
 * Reported games with their totals. Filters: route, api (or "older"), gpu (family), result, q (name), mine=1 (games this
 * install reported; needs X-Install) and, for POST, the caller's library keys. Route, API and GPU narrow the counted reports.
 */
async function listGames(env, q, install = null, keys = null) {
  const where = [], binds = [];
  const routeName = q.get('route') || '';
  if (routeName) {
    if (!ROUTES.has(routeName)) throw new HttpError(400, 'Unknown route');
    where.push('route = ?'); binds.push(routeName);
  }
  const api = q.get('api') || '';
  if (api === 'older') { where.push(`api IN (${OLDER_APIS.map(() => '?').join(',')})`); binds.push(...OLDER_APIS); }
  else if (api) {
    if (!APIS.has(api)) throw new HttpError(400, 'Unknown API');
    where.push('api = ?'); binds.push(api);
  }
  const family = q.get('gpu') || '';
  if (family) {
    if (!FAMILY_RE.test(family)) throw new HttpError(400, 'Bad graphics card');
    where.push('gpu_family = ?'); binds.push(family);
  }
  if (q.get('mine') === '1') {
    if (!install) throw new HttpError(400, 'Missing install id');
    where.push('game_key IN (SELECT game_key FROM reports WHERE install = ?)'); binds.push(install);
  }
  const rows = (await env.DB.prepare(
    `SELECT game_key AS key, MAX(game_name) AS name,
            SUM(rating = 'works') AS works, SUM(rating = 'partly') AS partly, SUM(rating = 'broken') AS broken,
            SUM(note <> '' AND hidden = 0) AS notes, MAX(updated_at) AS updated,
            GROUP_CONCAT(DISTINCT NULLIF(api, '')) AS apis, MAX(install = ?) AS mine
       FROM reports ${where.length ? 'WHERE ' + where.join(' AND ') : ''} GROUP BY game_key`)
    .bind(install ?? '', ...binds).all()).results;
  const result = q.get('result') || '';
  const search = (q.get('q') || '').toLowerCase().slice(0, 60);
  let list = rows.map(r => ({ ...r, apis: r.apis ? r.apis.split(',') : [], mine: !!r.mine, total: r.works + r.partly + r.broken, status: status(r.works, r.partly, r.broken) }))
    .filter(r => (!result || r.status === result) && (!search || r.name.toLowerCase().includes(search)) && (!keys || keys.has(r.key)));
  const sort = q.get('sort') || 'recent';
  list.sort(sort === 'reports' ? (a, b) => b.total - a.total || b.updated - a.updated
    : sort === 'name' ? (a, b) => a.name.localeCompare(b.name)
    : (a, b) => b.updated - a.updated);
  const offset = Math.max(0, Number(q.get('offset')) || 0);
  return json({ total: list.length, games: list.slice(offset, offset + PAGE) });
}

async function getGame(env, key, install) {
  if (!KEY_RE.test(key)) throw new HttpError(400, 'Bad game key');
  const db = env.DB;
  const totals = await db.prepare(`SELECT rating, COUNT(*) AS n FROM reports WHERE game_key = ? GROUP BY rating`).bind(key).all();
  const byRoute = await db.prepare(
    `SELECT route, mfg, gpu_family,
            SUM(rating = 'works') AS works, SUM(rating = 'partly') AS partly, SUM(rating = 'broken') AS broken
       FROM reports WHERE game_key = ? GROUP BY route, mfg, gpu_family ORDER BY works DESC`).bind(key).all();
  const notes = await db.prepare(
    `SELECT r.id, r.rating, r.route, r.mfg, r.gpu, r.gpu_family, r.versions, r.app_version, r.note, r.updated_at,
            p.nickname, p.is_dev
       FROM reports r LEFT JOIN profiles p ON p.install = r.install
      WHERE r.game_key = ? AND r.hidden = 0 AND r.note <> ''
      ORDER BY r.updated_at DESC LIMIT 30`).bind(key).all();
  const counts = { works: 0, partly: 0, broken: 0 };
  for (const r of totals.results) counts[r.rating] = r.n;
  const mine = install ? await db.prepare(
    `SELECT id, rating, route, mfg, note, hidden, updated_at FROM reports WHERE install = ? AND game_key = ?`).bind(install, key).first() : null;
  return json({
    key, total: counts.works + counts.partly + counts.broken, counts, byRoute: byRoute.results,
    notes: notes.results.map(n => ({ ...n, mfg: !!n.mfg, is_dev: !!n.is_dev, versions: safeJson(n.versions) })),
    mine,
  });
}

async function postReport(env, body, ipHash) {
  const install = await hashInstall(body.install, env);
  const key = str(body.key, 1, 70);
  if (!KEY_RE.test(key)) throw new HttpError(400, 'Bad game key');
  const rating = str(body.rating, 1, 10);
  if (!RATINGS.has(rating)) throw new HttpError(400, 'Rating must be works, partly or broken');
  const routeName = str(body.route, 1, 20);
  if (!ROUTES.has(routeName)) throw new HttpError(400, 'Unknown route');
  const name = clean(str(body.name, 1, 100));
  const gpu = clean(str(body.gpu ?? 'Unknown', 1, 80));
  const family = clean(str(body.gpuFamily ?? 'Other', 1, 20));
  const api = APIS.has(body.api) ? body.api : '';
  const note = clean(str(body.note ?? '', 0, NOTE_MAX * 2)).slice(0, NOTE_MAX);
  const appVersion = clean(str(body.appVersion ?? '', 0, 20));
  const versions = JSON.stringify(body.versions && typeof body.versions === 'object' ? body.versions : {}).slice(0, 400);
  const p = await profile(env, install);
  if (p?.banned) throw new HttpError(403, 'You can no longer post in the community.');
  await limit(env, `post:${install}`, LIMITS.postPerInstall);
  await limit(env, `report:${ipHash}:${key}`, LIMITS.reportsPerIpPerGame);
  const now = Date.now();
  const before = await env.DB.prepare(`SELECT id, note FROM reports WHERE install = ? AND game_key = ?`).bind(install, key).first();
  await env.DB.prepare(
    `INSERT INTO reports (game_key, game_name, install, rating, route, mfg, gpu, gpu_family, api, versions, app_version, note, created_at, updated_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (install, game_key) DO UPDATE SET
       game_name = excluded.game_name, rating = excluded.rating, route = excluded.route, mfg = excluded.mfg,
       gpu = excluded.gpu, gpu_family = excluded.gpu_family, api = excluded.api, versions = excluded.versions,
       app_version = excluded.app_version, note = excluded.note, updated_at = excluded.updated_at,
       hidden = CASE WHEN reports.note = excluded.note THEN reports.hidden ELSE 0 END`)
    .bind(key, name, install, rating, routeName, body.mfg ? 1 : 0, gpu, family, api, versions, appVersion, note, now, now).run();
  if (before && before.note !== note) await env.DB.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(before.id).run();
  return json({ ok: true });
}

async function flagReport(env, body, id, ipHash) {
  const install = await hashInstall(body.install, env);
  // Three flags hide a note: flaggers need a nickname (not banned), and one connection can only flag so often.
  await poster(env, install);
  await limit(env, `flag:${ipHash}`, LIMITS.flagsPerIp);
  const report = await env.DB.prepare(`SELECT install FROM reports WHERE id = ?`).bind(id).first();
  if (!report) throw new HttpError(404, 'Not found');
  if (report.install === install) throw new HttpError(400, 'You cannot flag your own report');
  await env.DB.prepare(`INSERT OR IGNORE INTO flags (report_id, install, created_at) VALUES (?, ?, ?)`).bind(id, install, Date.now()).run();
  const { n } = await env.DB.prepare(`SELECT COUNT(*) AS n FROM flags WHERE report_id = ?`).bind(id).first();
  if (n >= FLAGS_TO_HIDE) await env.DB.prepare(`UPDATE reports SET hidden = 1 WHERE id = ?`).bind(id).run();
  return json({ ok: true });
}

// ---------------------------------------------------------------- pictures

/** Decodes a base64 JPEG/PNG from the app and stores it. Returns the image id. */
async function storeImage(env, install, kind, base64, ipHash) {
  await limit(env, `image:${ipHash}`, LIMITS.imagesPerIp);
  if (typeof base64 !== 'string' || base64.length > Math.ceil(IMAGE_MAX * 4 / 3) + 8) throw new HttpError(413, 'Picture too large');
  let bytes;
  try { bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0)); } catch { throw new HttpError(400, 'Bad picture'); }
  let mime;
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) mime = 'image/jpeg';
  else if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) mime = 'image/png';
  else throw new HttpError(400, 'Only JPEG or PNG pictures');
  const r = await env.DB.prepare(`INSERT INTO images (install, kind, mime, data, created_at) VALUES (?, ?, ?, ?, ?) RETURNING id`)
    .bind(install, kind, mime, bytes, Date.now()).first();
  return r.id;
}

async function getImage(env, id, install, adminView = false) {
  const img = await env.DB.prepare(`SELECT id, install, kind, mime, data FROM images WHERE id = ?`).bind(id).first();
  if (!img) throw new HttpError(404, 'Not found');
  let allowed = adminView || (install && install === img.install);
  if (!allowed && img.kind === 'gallery')
    allowed = !!(await env.DB.prepare(`SELECT 1 AS ok FROM gallery WHERE image_id = ? AND status = 'approved'`).bind(id).first());
  if (!allowed) throw new HttpError(404, 'Not found');
  const publicImage = img.kind === 'gallery' && !adminView && install !== img.install;
  // D1 hands BLOBs back as an array of byte values; turn it into real bytes.
  return new Response(new Uint8Array(img.data), {
    headers: { 'Content-Type': img.mime, 'Cache-Control': publicImage ? 'public, max-age=86400' : 'private, no-store' },
  });
}

// ---------------------------------------------------------------- private line

async function getPrivate(env, install) {
  const rows = await env.DB.prepare(
    `SELECT id, from_dev, text, image_id, created_at FROM private_msgs WHERE install = ? ORDER BY id DESC LIMIT 200`).bind(install).all();
  await env.DB.prepare(`UPDATE private_msgs SET read_by_user = 1 WHERE install = ? AND from_dev = 1 AND read_by_user = 0`).bind(install).run();
  return json(rows.results.reverse().map(r => ({ ...r, from_dev: !!r.from_dev })));
}

async function postPrivate(env, body, ipHash) {
  const install = await hashInstall(body.install, env);
  const p = await profile(env, install);
  if (p?.banned) throw new HttpError(403, 'You can no longer post in the community.');
  await limit(env, `private:${install}`, LIMITS.privatePerInstall);
  const text = clean(str(body.text ?? '', 0, PRIVATE_MAX * 2), true).slice(0, PRIVATE_MAX);
  if (!text && !body.image) throw new HttpError(400, 'Write something or attach a picture.');
  const imageId = body.image ? await storeImage(env, install, 'private', body.image, ipHash) : null;
  await env.DB.prepare(`INSERT INTO private_msgs (install, from_dev, text, image_id, read_by_user, created_at) VALUES (?, 0, ?, ?, 1, ?)`)
    .bind(install, text, imageId, Date.now()).run();
  return json({ ok: true });
}

// ---------------------------------------------------------------- gallery

async function listGallery(env, q) {
  const offset = Math.max(0, Number(q.get('offset')) || 0);
  const rows = await env.DB.prepare(
    `SELECT g.id, g.image_id, g.caption, g.game_name, g.created_at, p.nickname, p.is_dev
       FROM gallery g LEFT JOIN profiles p ON p.install = g.install
      WHERE g.status = 'approved' ORDER BY g.id DESC LIMIT ? OFFSET ?`).bind(PAGE, offset).all();
  return json(rows.results.map(r => ({ ...r, is_dev: !!r.is_dev })));
}

async function myGallery(env, install) {
  const rows = await env.DB.prepare(
    `SELECT id, image_id, caption, game_name, status, created_at FROM gallery WHERE install = ? ORDER BY id DESC LIMIT 50`).bind(install).all();
  return json(rows.results);
}

async function postGallery(env, body, ipHash) {
  const install = await hashInstall(body.install, env);
  await poster(env, install);
  await limit(env, `upload:${install}`, LIMITS.uploadsPerInstall);
  const caption = clean(str(body.caption ?? '', 0, CAPTION_MAX * 2)).slice(0, CAPTION_MAX);
  const game = clean(str(body.game ?? '', 0, 100));
  const imageId = await storeImage(env, install, 'gallery', body.image, ipHash);
  await env.DB.prepare(`INSERT INTO gallery (install, image_id, caption, game_name, created_at) VALUES (?, ?, ?, ?, ?)`)
    .bind(install, imageId, caption, game, Date.now()).run();
  return json({ ok: true, status: 'pending' });
}

// ---------------------------------------------------------------- chat

async function listChat(env, q, install) {
  const after = Number(q.get('after')) || 0;
  const before = Number(q.get('before')) || 0;
  const where = after ? 'c.id > ?' : before ? 'c.id < ?' : '1 = 1';
  const order = after ? 'ASC' : 'DESC';
  const args = after ? [after] : before ? [before] : [];
  let rows = (await env.DB.prepare(
    `SELECT c.id, c.text, c.reply_to, c.created_at, c.hidden, p.nickname, p.is_dev, (c.install = ?) AS mine
       FROM chat c LEFT JOIN profiles p ON p.install = c.install
      WHERE ${where} ORDER BY c.id ${order} LIMIT 50`).bind(install ?? '', ...args).all()).results;
  if (order === 'DESC') rows.reverse();
  rows = rows.filter(r => !r.hidden);
  const ids = rows.map(r => r.id);
  const replies = rows.filter(r => r.reply_to).map(r => r.reply_to);
  const reactions = ids.length ? (await env.DB.prepare(
    `SELECT chat_id, emoji, COUNT(*) AS n, SUM(install = ?) AS mine FROM reactions
      WHERE chat_id IN (${ids.map(() => '?').join(',')}) GROUP BY chat_id, emoji`).bind(install ?? '', ...ids).all()).results : [];
  const quoted = replies.length ? (await env.DB.prepare(
    `SELECT c.id, substr(c.text, 1, 140) AS text, c.hidden, p.nickname FROM chat c LEFT JOIN profiles p ON p.install = c.install
      WHERE c.id IN (${replies.map(() => '?').join(',')})`).bind(...replies).all()).results : [];
  return json(rows.map(r => ({
    id: r.id, text: r.text, created_at: r.created_at, nickname: r.nickname ?? 'Player', is_dev: !!r.is_dev, mine: !!r.mine,
    reply: r.reply_to ? (q2 => q2 && !q2.hidden ? { id: q2.id, nickname: q2.nickname ?? 'Player', text: q2.text } : { id: r.reply_to, nickname: '', text: '(message removed)' })(quoted.find(x => x.id === r.reply_to)) : null,
    reactions: reactions.filter(x => x.chat_id === r.id).map(x => ({ emoji: x.emoji, n: x.n, mine: !!x.mine })),
  })));
}

async function postChat(env, body) {
  const install = await hashInstall(body.install, env);
  await poster(env, install);
  await limit(env, `chat:${install}`, LIMITS.chatPerInstall);
  const text = clean(str(body.text, 1, CHAT_MAX * 2), true).slice(0, CHAT_MAX);
  if (!text) throw new HttpError(400, 'Empty message');
  const last = await env.DB.prepare(`SELECT text, created_at FROM chat WHERE install = ? ORDER BY id DESC LIMIT 1`).bind(install).first();
  if (last && Date.now() - last.created_at < 3000) throw new HttpError(429, 'Slow down a little.');
  if (last && last.text === text && Date.now() - last.created_at < 600000) throw new HttpError(429, 'You just sent that.');
  const replyTo = Number(body.replyTo) || null;
  const r = await env.DB.prepare(`INSERT INTO chat (install, text, reply_to, created_at) VALUES (?, ?, ?, ?) RETURNING id`)
    .bind(install, text, replyTo, Date.now()).first();
  return json({ ok: true, id: r.id });
}

async function reactChat(env, body, id) {
  const install = await hashInstall(body.install, env);
  await poster(env, install);
  const emoji = str(body.emoji, 1, 8);
  if (!EMOJI.has(emoji)) throw new HttpError(400, 'Unknown reaction');
  await limit(env, `chat:${install}`, LIMITS.chatPerInstall * 2);
  if (body.on === false) await env.DB.prepare(`DELETE FROM reactions WHERE chat_id = ? AND install = ? AND emoji = ?`).bind(id, install, emoji).run();
  else await env.DB.prepare(`INSERT OR IGNORE INTO reactions (chat_id, install, emoji) VALUES (?, ?, ?)`).bind(id, install, emoji).run();
  return json({ ok: true });
}

async function deleteOwnChat(env, body, id) {
  const install = await hashInstall(body.install, env);
  const r = await env.DB.prepare(`UPDATE chat SET hidden = 1 WHERE id = ? AND install = ?`).bind(id, install).run();
  if (!r.meta.changes) throw new HttpError(404, 'Not found');
  return json({ ok: true });
}

// ---------------------------------------------------------------- moderator

async function requireAdmin(request, env) {
  const auth = request.headers.get('Authorization') || '';
  const given = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  if (!env.ADMIN_TOKEN || given.length < 32 || !(await timingSafeEqual(given, env.ADMIN_TOKEN)))
    throw new HttpError(401, 'Unauthorized');
}

// "author" values in moderator lists are the hashed install, used to ban or to answer a private thread.
async function admin(request, env, path, m, q) {
  const db = env.DB;
  let g;
  // Make an install the developer (badge, reserved names allowed).
  if (m === 'POST' && path === '/v1/admin/claim-dev') {
    const body = await readJson(request);
    const install = await hashInstall(body.install, env);
    const nickname = clean(str(body.nickname, 3, 20));
    const now = Date.now();
    await db.prepare(
      `INSERT INTO profiles (install, nickname, nickname_lc, is_dev, created_at, updated_at) VALUES (?, ?, ?, 1, ?, ?)
       ON CONFLICT (install) DO UPDATE SET nickname = excluded.nickname, nickname_lc = excluded.nickname_lc, is_dev = 1, updated_at = excluded.updated_at`)
      .bind(install, nickname, nickname.toLowerCase(), now, now).run();
    return json({ ok: true });
  }
  if (m === 'GET' && path === '/v1/admin/summary') {
    const one = async sql => (await db.prepare(sql).first()).n;
    return json({
      pendingGallery: await one(`SELECT COUNT(*) AS n FROM gallery WHERE status = 'pending'`),
      unreadPrivate: await one(`SELECT COUNT(*) AS n FROM private_msgs WHERE from_dev = 0 AND read_by_dev = 0`),
      flaggedNotes: await one(`SELECT COUNT(DISTINCT report_id) AS n FROM flags`),
    });
  }
  // Reports / notes
  if (m === 'GET' && path === '/v1/admin/flagged') {
    const r = await db.prepare(
      `SELECT r.id, r.install AS author, r.game_name, r.rating, r.note, r.hidden, COUNT(f.install) AS flags, r.updated_at
         FROM reports r JOIN flags f ON f.report_id = r.id GROUP BY r.id ORDER BY r.updated_at DESC LIMIT 200`).all();
    return json(r.results);
  }
  if (m === 'GET' && path === '/v1/admin/recent') {
    const r = await db.prepare(
      `SELECT id, install AS author, game_key, game_name, rating, route, gpu, note, hidden, updated_at
         FROM reports WHERE note <> '' ORDER BY updated_at DESC LIMIT 100`).all();
    return json(r.results);
  }
  if (m === 'POST' && (g = path.match(/^\/v1\/admin\/reports\/(\d+)\/(hide|unhide)$/))) {
    await db.prepare(`UPDATE reports SET hidden = ? WHERE id = ?`).bind(g[2] === 'hide' ? 1 : 0, Number(g[1])).run();
    if (g[2] === 'unhide') await db.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(Number(g[1])).run();
    return json({ ok: true });
  }
  if (m === 'DELETE' && (g = path.match(/^\/v1\/admin\/reports\/(\d+)$/))) {
    await db.prepare(`DELETE FROM flags WHERE report_id = ?`).bind(Number(g[1])).run();
    await db.prepare(`DELETE FROM reports WHERE id = ?`).bind(Number(g[1])).run();
    return json({ ok: true });
  }
  // Private line
  if (m === 'GET' && path === '/v1/admin/private') {
    const r = await db.prepare(
      `SELECT pm.install AS author, p.nickname, MAX(pm.created_at) AS last_at,
              SUM(pm.from_dev = 0 AND pm.read_by_dev = 0) AS unread, COUNT(*) AS messages
         FROM private_msgs pm LEFT JOIN profiles p ON p.install = pm.install
        GROUP BY pm.install ORDER BY unread > 0 DESC, last_at DESC LIMIT 200`).all();
    return json(r.results);
  }
  if ((g = path.match(/^\/v1\/admin\/private\/([0-9a-f]{64})$/))) {
    if (m === 'GET') {
      const r = await db.prepare(`SELECT id, from_dev, text, image_id, created_at FROM private_msgs WHERE install = ? ORDER BY id`).bind(g[1]).all();
      await db.prepare(`UPDATE private_msgs SET read_by_dev = 1 WHERE install = ? AND from_dev = 0`).bind(g[1]).run();
      return json(r.results.map(x => ({ ...x, from_dev: !!x.from_dev })));
    }
    if (m === 'POST') {
      const body = await readJson(request);
      const text = clean(str(body.text, 1, PRIVATE_MAX * 2), true).slice(0, PRIVATE_MAX);
      await db.prepare(`INSERT INTO private_msgs (install, from_dev, text, read_by_dev, created_at) VALUES (?, 1, ?, 1, ?)`)
        .bind(g[1], text, Date.now()).run();
      return json({ ok: true });
    }
  }
  // Gallery
  if (m === 'GET' && path === '/v1/admin/gallery') {
    const st = q.get('status') || 'pending';
    const r = await db.prepare(
      `SELECT g.id, g.install AS author, g.image_id, g.caption, g.game_name, g.status, g.created_at, p.nickname
         FROM gallery g LEFT JOIN profiles p ON p.install = g.install WHERE g.status = ? ORDER BY g.id DESC LIMIT 100`).bind(st).all();
    return json(r.results);
  }
  if (m === 'POST' && (g = path.match(/^\/v1\/admin\/gallery\/(\d+)\/(approve|reject)$/))) {
    await db.prepare(`UPDATE gallery SET status = ?, decided_at = ? WHERE id = ?`)
      .bind(g[2] === 'approve' ? 'approved' : 'rejected', Date.now(), Number(g[1])).run();
    return json({ ok: true });
  }
  if (m === 'DELETE' && (g = path.match(/^\/v1\/admin\/gallery\/(\d+)$/))) {
    const row = await db.prepare(`SELECT image_id FROM gallery WHERE id = ?`).bind(Number(g[1])).first();
    await db.prepare(`DELETE FROM gallery WHERE id = ?`).bind(Number(g[1])).run();
    if (row) await db.prepare(`DELETE FROM images WHERE id = ?`).bind(row.image_id).run();
    return json({ ok: true });
  }
  if (m === 'GET' && (g = path.match(/^\/v1\/admin\/images\/(\d+)$/))) return getImage(env, Number(g[1]), null, true);
  // Chat
  if (m === 'GET' && path === '/v1/admin/chat') {
    const r = await db.prepare(
      `SELECT c.id, c.install AS author, c.text, c.hidden, c.created_at, p.nickname FROM chat c LEFT JOIN profiles p ON p.install = c.install
        ORDER BY c.id DESC LIMIT 200`).all();
    return json(r.results);
  }
  if (m === 'POST' && (g = path.match(/^\/v1\/admin\/chat\/(\d+)\/(hide|unhide)$/))) {
    await db.prepare(`UPDATE chat SET hidden = ? WHERE id = ?`).bind(g[2] === 'hide' ? 1 : 0, Number(g[1])).run();
    return json({ ok: true });
  }
  // Bans (by the "author" hash from any list above)
  if (m === 'POST' && (g = path.match(/^\/v1\/admin\/(ban|unban)\/([0-9a-f]{64})$/))) {
    const banned = g[1] === 'ban' ? 1 : 0;
    const now = Date.now();
    await db.prepare(
      `INSERT INTO profiles (install, nickname, nickname_lc, banned, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)
       ON CONFLICT (install) DO UPDATE SET banned = excluded.banned, updated_at = excluded.updated_at`)
      .bind(g[2], 'banned-' + g[2].slice(0, 8), 'banned-' + g[2].slice(0, 8), banned, now, now).run();
    if (banned) {
      await db.prepare(`UPDATE chat SET hidden = 1 WHERE install = ?`).bind(g[2]).run();
      await db.prepare(`UPDATE reports SET hidden = 1 WHERE install = ?`).bind(g[2]).run();
      await db.prepare(`UPDATE gallery SET status = 'rejected' WHERE install = ? AND status = 'pending'`).bind(g[2]).run();
    }
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

async function readJson(request, big = false) {
  const text = await request.text();
  if (text.length > (big ? 1300000 : 8192)) throw new HttpError(413, 'Too large');
  try { return JSON.parse(text); } catch { throw new HttpError(400, 'Bad JSON'); }
}

function str(v, min, max) {
  if (typeof v !== 'string' || v.length < min || v.length > max) throw new HttpError(400, 'Bad field');
  return v;
}

// Plain text only: drop control and direction-override characters; keep line breaks where allowed.
function clean(s, keepLines = false) {
  s = s.replace(/[​-‏‪-‮⁦-⁩\u0000-\u0009\u000b-\u001f\u007f-\u009f]/g, ' ');
  return keepLines
    ? s.replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n').split('\n').map(l => l.trim()).join('\n').trim()
    : s.replace(/\s+/g, ' ').trim();
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
