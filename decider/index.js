/**
 * StS2 决策器（视图层）
 *
 * 分工：
 *   - 模组：读知识库、算分、按卡组调整，把「事实 + 分数」直接发过来
 *   - 本插件：只做视图整形（裁字数、挑字段），转给核心模型，再把答案回给模组
 *
 * 不使用 fs / 不读写文件；只有 http。
 */


// ================= 路径（可配置，别再硬编码） =================
import _osMod from 'node:os';
import _pathMod from 'node:path';
const AGENT_DATA = process.env.STS2_AGENT_DATA || _pathMod.join(process.env.APPDATA || _osMod.homedir(), 'Sts2Agent');
const MEM_DIR = _pathMod.join(AGENT_DATA, 'memory');
const NEXT_CHAR = _pathMod.join(AGENT_DATA, 'next-character.txt');
const AUTOPLAY_FLAG = _pathMod.join(AGENT_DATA, 'autoplay.flag');
const AUTOPLAY_ACTIVE = _pathMod.join(AGENT_DATA, 'autoplay.active');

import http from 'node:http';
import fs from 'node:fs';
import { exec } from 'node:child_process';
import { chatCompletionWithRetry, llmSource } from './llm.js';

let api = null;
console.log('[sts2-decider] 模型调用来源:', llmSource);
let server = null;
let cfg = () => ({});

const stats = {
  startedAt: 0,
  requests: 0,
  ok: 0,
  failed: 0,
  lastMs: 0,
  lastKind: '',
  lastError: '',
  lastAnswer: null,
  lastStateAt: 0,
  lastFinish: '',
  lastReasoning: 0,
  lastPromptChars: 0
};

const STAR_HINT = '你还有星星资源：payload 里的 stars 是你当前星星，卡牌的 star 是它的星星消耗（星星不够就打不出去）。';

const SYSTEM_HEAD = `你在玩《杀戮尖塔2》，玩得过去就行，不追求最优解。
只输出单个 JSON，不写任何自由文字解释；需要说明时用 JSON 里的 "why" 字段（≤12 字）。
伤害、格挡都已经给你算好了，直接用。
【必须遵守】不带 why 的自由文字、注释、代码块一律不允许；不确定时也**只能**输出一个 JSON。

示例1（出牌）：
输入 {"kind":"combat_turn","energy":3,"turn":2,"hp":60,"threat":"将受到 12 点伤害，现有护甲 0，还需挡 12 点","hand":[{"i":0,"title":"打击","cost":"1","dmg":6,"hits":1,"eff":6},{"i":1,"title":"华丽收场","cost":"2","dmg":12,"eff":6,"playable":false,"noPlay":"BlockedByCardLogic"}],"enemies":[{"i":0,"name":"劫掠者刺客","hp":8,"intent":{"type":"attack","total":6},"killGain":"打掉它需 8 点伤害，之后少挨 6 点"}]}
输出 {"i":0,"t":0}

示例2（这回合够了）：
输入 {"kind":"combat_turn","energy":0,"threat":"敌人这回合不攻击","hand":[{"i":0,"title":"防御","cost":"1","block":5}],"enemies":[{"i":0,"name":"史莱姆","hp":20,"intent":{"type":"none","total":0,"hits":0}}]}
输出 {"end":true}

示例3（地图选路）：
输入 {"kind":"pick_map","nodes":[{"i":0,"coord":"(2,11)","type":"Monster","score":70},{"i":1,"coord":"(3,10)","type":"RestSite","score":90},{"i":2,"coord":"(1,12)","type":"Elite","score":50}]}
输出 {"index":1}

示例4（事件/火堆选项）：
输入 {"kind":"pick_option","event":"涅奥","options":[{"i":0,"label":"获得100金币"},{"i":1,"label":"奥术卷轴：获得一张随机稀有牌"}]}
输出 {"index":1}

示例5（商店）：
输入 {"kind":"pick_shop","gold":120,"options":[{"i":0,"info":"52金/匕首雨","score":48,"tier":"B"},{"i":1,"info":"75金/恶魔形态","score":72,"tier":"A","needsEnergy":true}]}
输出 {"index":1}

示例9（喝药水 / 丢药水）：
输入 {"kind":"combat_turn","energy":1,"threat":"将受到 18 点伤害，还需挡 18 点","potions":[{"i":0,"name":"格挡药水","desc":"获得12点格挡"},{"i":1,"name":"力量药水","desc":"获得2点力量"}],"hand":[...],"enemies":[...]}
输出 {"potion":0,"t":-1}

示例10（药水满了，丢掉没用的）：
输出 {"dropPotion":1}

示例6（选牌：拿牌 / 丢牌 / 锻造共用）：
输入 {"kind":"pick_cards","min":1,"max":1,"purpose":"reward","pick":"highest","options":[{"i":0,"title":"打击","score":20,"tier":"D"},{"i":1,"title":"猎手反击","score":75,"tier":"A"}]}
输出 {"picks":[1]}
`;
const SYSTEM_RULES_DEFAULT = `规则：
- combat_turn: 一次一个动作 {"i":手牌索引,"t":敌人索引}；不需要目标时 t 填 -1；不想再出牌给 {"end":true}
  手牌里带 "playable":false 的牌现在打不出去（noPlay 写了原因，比如 BlockedByCardLogic 表示条件不满足），不要选它。
  尽量把能量用掉：还有能量、手里还有能打的牌，就继续出，别因为"挡够了"就 end——攻击牌打出去也是白赚的伤害。
  只有能量为 0、或所有牌都打不出去时，才 {"end":true}。
  例外：payload 里有 "energyKeeps":true（你有冰淇淋，能量不会随回合重置）时，可以留 1~2 点攒下回合，但 why 里要说明为什么留。
- pick_cards: {"picks":[索引...]}（数量在 min..max 之间）
- pick_map: {"index":索引}（score 越大越推荐）
- pick_character: {"index":索引}
- pick_option（事件/火堆）: {"index":索引}
- pick_shop: {"index":索引} 或 {"skip":true}
  战斗中可以用药水：{"potion":槽位,"t":目标索引}（不需要目标填 -1）；想丢掉填 {"dropPotion":槽位}。
  敌人有 poison 字段时：hp 仍是**当前真实血量**；poison.endTurnDamage 是**这回合结束**才会掉的毒伤，
  不能当成已经掉了——想"回合前斩杀"就按 hp 算，想"等毒死"就按 hp - endTurnDamage 算。
  手牌里的 dmg 是"不对着具体敌人"算的（含力量/附魔，不含目标易伤）；
  真正打到某敌人身上 = dmg（敌人有易伤再 ×1.5，有护甲先扣护甲）。
  药水别乱用，留给关键时刻（血线危险、精英、Boss 战）。
  商店按 prio 排序：4=遗物/核心卡(≥85分)　3=删牌或其他卡　2=药水。先看 prio 高的，同一档再比 score。

选牌方向（pick 只是建议，可以不听）：
- pick = "highest" → 通常选分数**最高**的
- pick = "lowest"  → 通常选分数**最低**的（最没用的那张）
- purpose 是 upgrade（火堆锻造）时：score 是**升级分**（这张牌升级值不值），挑分最高的。
- purpose 是 enchant（附魔）时：score 是卡牌本身的分，挑分最高的。
- 局面特殊时（比如这张牌有你特别需要的东西）你可以不按建议来，但要自己担着。
评分用法（选牌时最重要）：
- score 1~100，tier 是 S/A/B/C/D；分数已按你当前卡组调整过。
- 选牌以 score 为主要依据：分高的直接拿；分差 <12 时才看 desc。
- adjust 是调整理由；needsEnergy 表示吃费、需要加费支撑。
- deck.counts 是卡组里每张牌的张数。

战斗：先算一笔账——杀掉一个敌人，它这回合的伤害就直接归零（看 killGain 那句写了"打掉它需 X 点，之后少挨 Y 点"）。
如果一张牌或一组牌能斩杀某只怪，通常比堆同样数值的格挡更划算；打不死才去防御。
尽量别受到5点以上的伤害，那会很亏。

可选：JSON 里加 "why"（≤12 字）说明为什么这么选，只在选路/火堆/选牌/商店时给；战斗出牌不要 why，只要 {"i":n,"t":m}。【必须遵守】`;
function systemPrompt() {
  return SYSTEM_HEAD + SYSTEM_RULES_DEFAULT;
}


export function setup(a) {
  api = a;
  cfg = () => ({ enabled: true, port: 18731, timeoutMs: 120000, ...(api.config() || {}) });
  const c = cfg();
  if (c.enabled === false) {
    api.log('决策服务已关闭');
    return;
  }
  startServer(Number(c.port) || 18731).catch((e) => api.error('决策服务启动失败: ' + (e?.message || e)));
}

export function deactivate() { stopServer(); }
export function dispose() { stopServer(); }

export const providers = {
  'sts2.status': () => ({ running: !!server, port: Number(cfg().port) || 18731, ...stats })
};

function stopServer() {
  try { if (server) { server.close(); server = null; } } catch { /* ignore */ }
}

async function startServer(port) {
  stopServer();
  server = http.createServer((req, res) => {
    if (req.method === 'GET' && req.url.startsWith('/options')) {
      return sendJson(res, 200, { ok: true, useCheats: cfg().useCheats === true });
    }
    if (req.method === 'GET' && req.url.startsWith('/decisions')) {
      const rows = readLines(DEC_FILE).slice(-200).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
      return sendJson(res, 200, { ok: true, rows });
    }
    if (req.method === 'GET' && req.url.startsWith('/status')) {
      return sendJson(res, 200, { ok: true, running: true, ...stats });
    }
    if (req.method === 'GET' && req.url.startsWith('/decidechar')) {
      decideCharacter().then((c) => sendJson(res, 200, { ok: !!c, character: c })).catch((e) => sendJson(res, 500, { error: String(e?.message || e) }));
      return;
    }
    if (req.method === 'POST' && req.url.startsWith('/runend')) {
      let raw2 = '';
      req.on('data', (c) => { raw2 += c; });
      req.on('end', () => {
        let st = {};
        try { st = JSON.parse(raw2 || '{}'); } catch { /* ignore */ }
        generateMemory(st)
          .then((r) => { sendJson(res, 200, r); })
          .then(() => announceRunEnd(st).catch(() => {}))     // 先让她把话说完
          .then(() => finishRunWithShot(st))                  // 再发图 / 关游戏 / 关浮窗
          .catch(() => { finishRunWithShot(st); })
          .catch((e) => sendJson(res, 500, { error: String(e?.message || e) }));
      });
      return;
    }
    if (req.method === 'GET' && req.url.startsWith('/quit')) {
      killGame().then((alive) => sendJson(res, 200, { ok: true, wasRunning: alive }));
      return;
    }
    if (req.method === 'POST' && req.url.startsWith('/decide')) {
      let raw = '';
      req.on('data', (chunk) => { raw += chunk; if (raw.length > 2_000_000) req.destroy(); });
      req.on('end', () => {
        handleDecide(raw)
          .then((out) => { stats.ok++; sendJson(res, 200, out); })
          .catch((e) => {
            stats.failed++;
            stats.lastError = String(e?.message || e);
            sendJson(res, 500, { error: stats.lastError });
          });
      });
      return;
    }
    sendJson(res, 404, { error: 'not found' });
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', resolve);
  });
  stats.startedAt = Date.now();
  api.log('决策服务已监听 127.0.0.1:' + port);
}

function sendJson(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'access-control-allow-origin': '*'
  });
  res.end(body);
}

// ── 视图：模组已把 desc/eff/score 都算好，这里只裁字数 ──────────
function cleanText(t) {
  return String(t || '').replace(/\[\/?gold\]/g, '').replace(/\s+/g, ' ').trim();
}

function forCombat(o, max) {
  return { desc: cleanText(o.desc).slice(0, max || 60) };
}

function forPick(o) {
  const out = { desc: cleanText(o.desc) };
  if (o.descUp) out.descUp = cleanText(o.descUp);
  if (typeof o.eff === 'number') out.eff = o.eff;
  if (typeof o.effUp === 'number') out.effUp = o.effUp;
  if (o.aoe) out.aoe = true;
  if (o.needsEnergy) out.needsEnergy = true;
  if (typeof o.score === 'number') out.score = o.score;
  if (o.tier) out.tier = o.tier;
  if (typeof o.scoreBase === 'number') out.scoreBase = o.scoreBase;
  if (o.adjust) out.adjust = o.adjust;
  return out;
}

function enrich(req) {
  try {
    if (req.kind === 'pick_cards' && Array.isArray(req.options)) {
      req.options = req.options.map((o) =>
        Object.assign({ i: o.i, title: o.title, cost: o.cost, type: o.type }, forPick(o)));
    } else if (req.kind === 'combat_turn' && Array.isArray(req.hand)) {
      req.hand = req.hand.map((h) => Object.assign({}, h, forCombat(h)));
    }
  } catch { /* ignore */ }
  return req;
}

async function handleDecide(raw) {
  const c = cfg();
  if (c.enabled === false) throw new Error('决策服务已关闭');
  const req = enrich(JSON.parse(raw || '{}'));
  const kind = String(req.kind || '');
  if (!['pick_cards', 'pick_map', 'combat_turn', 'pick_option', 'pick_shop', 'pick_character'].includes(kind)) {
    throw new Error('未知 kind: ' + kind);
  }

  const t0 = Date.now();
  stats.requests++;
  stats.lastKind = kind;
  stats.lastStateAt = Date.now();

  const user = JSON.stringify(req).slice(0, 8000);
  stats.lastPromptChars = user.length;

  const r = await chatCompletionWithRetry(
    {
      messages: [
        { role: 'system', content: systemPrompt() + (req && req.char === 'regent' ? STAR_HINT : '') },
        { role: 'user', content: user }
      ],
      tools: null,
      temperature: 0,
      maxTokens: 3000,
      skillContext: { thinkingMode: 'off' },
      tag: 'sts2-decide'
    },
    1
  );

  const text = String(r?.message?.content || '');
  const finish = r?.raw?.choices?.[0]?.finish_reason || r?.message?.finish_reason || '';
  const reasoningTokens = r?.usage?.completion_tokens_details?.reasoning_tokens ?? 0;

  stats.lastMs = Date.now() - t0;
  stats.lastFinish = finish;
  stats.lastReasoning = reasoningTokens;

  let answer;
  try {
    answer = parseAnswer(text, kind, req);
  } catch (e) {
    const completion = r?.usage?.completion_tokens ?? 0;
    stats.lastError = '解析失败（长度 ' + String(text).length + '，completion=' + completion +
      '，reasoning=' + reasoningTokens + '，finish=' + finish + '）原文: ' + String(text).slice(0, 160);
    throw new Error(stats.lastError);
  }

  const usageTok = Number(r?.usage?.total_tokens || ((r?.usage?.prompt_tokens || 0) + (r?.usage?.completion_tokens || 0))) || 0;
  let whyText = '';
  try { const mw = text.match(/"why"\s*:\s*"([^"]{1,40})"/); if (mw) whyText = mw[1]; } catch { /* ignore */ }
  try { traceDecision(kind, req, answer, whyText); } catch { /* ignore */ }
  try { recordDecision(kind, req, answer, whyText, stats.lastMs, usageTok); } catch { /* ignore */ }
  stats.lastAnswer = answer;
  stats.lastError = '';
  api.log('决策 ' + kind + ' → ' + JSON.stringify(answer) + '  ' + stats.lastMs + 'ms  finish=' +
    finish + ' reasoning=' + reasoningTokens);
  return { id: req.id || 0, ...answer, ms: stats.lastMs };
}

function parseAnswer(text, kind, req) {
  const m = text.match(/\{[\s\S]*\}/);
  if (!m) throw new Error('模型没有返回 JSON：' + text.slice(0, 120));
  const obj = JSON.parse(m[0]);

  if (kind === 'pick_cards') {
    const list = Array.isArray(obj.picks) ? obj.picks.map(Number).filter((n) => Number.isInteger(n)) : [];
    if (!list.length) throw new Error('模型没给 picks');
    const min = Math.max(1, Number(req.min ?? 1));
    return { picks: list.slice(0, Math.max(min, list.length)) };
  }
  if (kind === 'pick_character') {
    const ci = Number(obj.index);
    if (!Number.isInteger(ci) || ci < 0) throw new Error('模型没给 index');
    return { index: ci };
  }
  if (kind === 'pick_map' || kind === 'pick_option') {
    const idx = Number(obj.index);
    if (!Number.isInteger(idx) || idx < 0) throw new Error('模型没给 index');
    return { index: idx };
  }
  if (kind === 'pick_shop') {
    if (obj.skip === true) return { skip: true, index: -1 };
    const idx = Number(obj.index);
    if (!Number.isInteger(idx) || idx < 0) throw new Error('模型没给 index');
    return { index: idx };
  }

  if (obj.end === true) return { end: true };
  const i = Number(obj.i);
  if (!Number.isInteger(i) || i < 0) throw new Error('模型没给合法的 i');
  const t = Number.isFinite(Number(obj.t)) ? Number(obj.t) : -1;
  return { i, t };
}



// ── 保存并退出：直接杀进程（游戏会自动存档，不丢进度）──────────────
function killGame() {
  return new Promise((resolve) => {
    exec('taskkill /F /IM SlayTheSpire2.exe', (e) => {
      try { fs.unlinkSync(AUTOPLAY_ACTIVE); } catch { /* ignore */ }
      resolve(!e || /not found|没有运行|找不到/i.test(String(e.message || '')));
    });
  });
}


// ── 记忆层 ────────────────────────────────────────────────

const MEM_COMBAT = MEM_DIR + '\\combat.jsonl';
const MEM_CHAT = MEM_DIR + '\\chat.md';
const MEM_TRACE = MEM_DIR + '\\trace.jsonl';
const LAST_CHAT_FILE = MEM_DIR + '\\lastchat.txt';

function memEnsure() {
  try { if (!fs.existsSync(MEM_DIR)) fs.mkdirSync(MEM_DIR, { recursive: true }); } catch { /* ignore */ }
}

function readLines(p) {
  try { if (!fs.existsSync(p)) return []; return fs.readFileSync(p, 'utf8').split('\n').filter(Boolean); } catch { return []; }
}

function capLines(p, max) {
  try {
    const lines = readLines(p);
    if (lines.length <= max) return;
    fs.writeFileSync(p, lines.slice(-max).join('\n') + '\n', 'utf8');
  } catch { /* ignore */ }
}

function capChatSections(max) {
  try {
    if (!fs.existsSync(MEM_CHAT)) return;
    const txt = fs.readFileSync(MEM_CHAT, 'utf8');
    const parts = txt.split(/<!-- run:/).filter((x) => x.trim());
    if (parts.length <= max) return;
    const kept = parts.slice(-max).map((x) => '<!-- run:' + x);
    fs.writeFileSync(MEM_CHAT, kept.join(''), 'utf8');
  } catch { /* ignore */ }
}

function personaText() {
  try {
    const cfgPath = path.join(AGENT_DATA, 'host-config.json');
    if (!fs.existsSync(cfgPath)) return '';
    const raw = fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, '');
    const c = JSON.parse(raw);
    return String(c.persona || '').trim();
  } catch { return ''; }
}


// 记录一次决策（极简）
function traceDecision(kind, req, answer, why) {
  try {
    memEnsure();
    const row = { t: Date.now(), kind };
    if (kind === 'pick_map') {
      row.hp = req.hp ?? null;
      row.cands = (req.nodes || []).map((n) => n.type + '(' + (n.score ?? '') + ')');
      if (answer && answer.index >= 0) row.chose = (req.nodes[answer.index] || {}).type;
    } else if (kind === 'pick_option') {
      row.event = req.event || '';
      row.options = (req.options || []).map((o) => o.label);
      if (answer && answer.index >= 0) row.chose = (req.options[answer.index] || {}).label;
    } else if (kind === 'pick_cards') {
      row.options = (req.options || []).map((o) => o.title + '(' + (o.score ?? '') + ')');
      if (answer && Array.isArray(answer.picks) && answer.picks.length) {
        const p = req.options[answer.picks[0]] || {};
        row.chose = p.title;
      }
      row.reward = !!req.reward;
    } else if (kind === 'combat_turn') {
      if (answer && answer.end) row.chose = 'end';
      else if (answer && Number.isInteger(answer.i)) {
        const h = (req.hand || [])[answer.i] || {};
        row.chose = h.title;
      }
    } else if (kind === 'pick_shop') {
      row.options = (req.options || []).map((o) => o.info);
      row.chose = answer && answer.skip ? 'skip' : 'buy:' + ((req.options || [])[answer?.index] || {}).info;
    } else if (kind === 'pick_character') {
      row.chose = answer && Number.isInteger(answer.index) ? ((req.options || [])[answer.index] || {}).name : '';
    }
    if (why) row.why = String(why).slice(0, 24);
    fs.appendFileSync(MEM_TRACE, JSON.stringify(row) + '\n', 'utf8');
  } catch { /* ignore */ }
}


// 一局结束：用人设写复盘 + 极简战绩
async function generateMemory(stats = {}) {
  memEnsure();
  const lines = readLines(MEM_TRACE);
  if (!lines.length) return { ok: false, why: '没有 trace' };

  // 压缩 trace：只保留关键决策（选路/事件/火堆/奖励/商店），出牌只留统计
  const key = [];
  let combatTurns = 0, cardsPlayed = 0;
  for (const ln of lines) {
    let r;
    try { r = JSON.parse(ln); } catch { continue; }
    if (r.kind === 'combat_turn') { combatTurns++; if (r.chose && r.chose !== 'end') cardsPlayed++; continue; }
    key.push(r);
  }
  const compact = key.slice(-40).map((r) => {
    const bits = [r.kind];
    if (r.hp !== undefined && r.hp !== null) bits.push('hp' + r.hp);
    if (r.cands) bits.push('候选:' + r.cands.join('/'));
    if (r.options) bits.push('选项:' + r.options.slice(0, 4).join('/'));
    if (r.chose) bits.push('选了:' + r.chose);
    if (r.why) bits.push('理由:' + r.why);
    if (r.event) bits.push('事件:' + r.event);
    return bits.join(' ');
  }).join('\n');

  const persona = personaText();
  const facts = '楼层 ' + (stats.floor ?? '?') + '，结果 ' + (stats.result ?? '未知') +
    '，角色 ' + (stats.char ?? '?') + '，出牌 ' + cardsPlayed + ' 张，回合 ' + combatTurns;

  // 聊天记忆：她的口吻
  let chatText = '';
  try {
    const sys = (persona ? persona + '\n\n' : '') +
      '下面是你刚打完的一局《杀戮尖塔2》的关键决策记录。' +
      '请用你自己的口吻（第一人称、符合你的性格）写一段 200~300 字的复盘：' +
      '这局怎么打的、关键选择时你在想什么、哪里做得好、哪里失误。' +
      '只写正文，不要加标题或解释。';
    const r = await chatCompletionWithRetry({
      messages: [
        { role: 'system', content: sys },
        { role: 'user', content: facts + '\n\n' + compact }
      ],
      tools: null,
      temperature: 0.7,
      maxTokens: 700,
      skillContext: { thinkingMode: 'off' },
      tag: 'sts2-memory'
    }, 1);
    chatText = String(r?.message?.content || '').trim();
  } catch (e) {
    if (api && api.error) api.error('写复盘失败: ' + (e?.message || e));
  }

  // 战斗记忆：极简一行
  const brief = (stats.char || '?') + ' 第' + (stats.floor ?? '?') + '层 ' +
    (stats.result === 'win' ? '通关' : (stats.result === 'death' ? '阵亡' : '结束')) +
    ' | 出牌' + cardsPlayed + ' | 回合' + combatTurns + ' | ' + new Date().toISOString().slice(0, 10);
  try {
    fs.appendFileSync(MEM_COMBAT, brief + '\n', 'utf8');
    capLines(MEM_COMBAT, 5);
  } catch { /* ignore */ }

  if (chatText) {
    try {
      const stamp = new Date().toISOString().slice(0, 16);
      fs.appendFileSync(MEM_CHAT, '<!-- run:' + stamp + ' -->\n' + chatText + '\n\n', 'utf8');
      capChatSections(2);
    } catch { /* ignore */ }
  }

  try { fs.unlinkSync(MEM_TRACE); } catch { /* ignore */ }
  dumpPanelData();
  if (api && api.log) api.log('记忆已生成: ' + brief);
  return { ok: true, brief, chat: chatText.slice(0, 60) };
}


// ── 角色表与"开局前定角色"────────────────────────────────
const CHARACTERS = [
  { id: 'ironclad', name: '铁甲战士', hint: '力量与格挡，正面硬刚' },
  { id: 'silent', name: '静默猎手', hint: '毒与飞刀，灵活消耗' },
  { id: 'defect', name: '故障机器人', hint: '充能球，能量与爆发' },
  { id: 'regent', name: '君王', hint: '星星与命令，召唤体系' },
  { id: 'necrobinder', name: '亡灵缚者', hint: '亡灵与消耗，亡者大军' }
];

async function decideCharacter() {
  const list = CHARACTERS.map((c) => c.name + '(' + c.id + ')：' + c.hint).join('\n');
  try {
    const r = await chatCompletionWithRetry({
      messages: [
        { role: 'system', content: '你在玩《杀戮尖塔2》。只输出一个 JSON：{"id":"角色id","name":"中文名"}，不解释。' },
        { role: 'user', content: '这局你想用哪个角色？候选：\n' + list + '\n你最近玩过：' + (readLines(MEM_COMBAT).slice(-3).join('；') || '还没玩过') }
      ],
      tools: null,
      temperature: 0.8,
      maxTokens: 300,
      skillContext: { thinkingMode: 'off' },
      tag: 'sts2-character'
    }, 1);
    const txt = String(r?.message?.content || '');
    const m = txt.match(/\{[\s\S]*\}/);
    if (!m) return null;
    const obj = JSON.parse(m[0]);
    const hit = CHARACTERS.find((c) => c.id === String(obj.id || '').toLowerCase())
      || CHARACTERS.find((c) => c.name === String(obj.name || ''));
    return hit || null;
  } catch (e) {
    if (api && api.error) api.error('选角色失败: ' + (e?.message || e));
    return null;
  }
}


// ── 决策记录（实时解释，供面板显示）──────────────────────
const DEC_FILE = MEM_DIR + '\\decisions.jsonl';

function buildExplain(kind, req, answer, why) {
  try {
    if (kind === 'pick_map') {
      const nodes = req.nodes || [];
      const pick = answer && answer.index >= 0 ? nodes[answer.index] : null;
      if (!pick) return '没选出节点';
      const best = [...nodes].sort((a, b) => (b.score || 0) - (a.score || 0))[0] || {};
      const hpTxt = req.hp != null ? '血量 ' + req.hp + '%，' : '';
      return hpTxt + '她选了 ' + pick.type + '（' + (pick.score ?? '?') + ' 分' +
        (pick.why ? '：' + pick.why : '') + '）' +
        (best.score === pick.score ? '，与最高分一致' : '，最高分是 ' + best.type + '（' + best.score + '）');
    }
    if (kind === 'pick_cards') {
      const opts = req.options || [];
      const p = answer && Array.isArray(answer.picks) ? opts[answer.picks[0]] : null;
      if (!p) return '没选牌';
      return (req.reward ? '奖励' : '选牌') + '：从 ' + opts.length + ' 张里选了「' + p.title + '」' +
        (p.score != null ? '（' + p.score + ' 分' + (p.tier ? '/' + p.tier : '') + '）' : '') +
        (p.adjust ? '，调整：' + p.adjust : '') + (why ? '；她的理由：' + why : '');
    }
    if (kind === 'pick_option') {
      const opts = req.options || [];
      const p = answer && answer.index >= 0 ? opts[answer.index] : null;
      return (req.event ? '事件「' + req.event + '」' : '火堆') + '：' +
        (p ? '选了「' + p.label + '」' + (p.score != null ? '（' + p.score + ' 分）' : '') : '未选') +
        (why ? '；理由：' + why : '');
    }
    if (kind === 'pick_shop') {
      if (answer && answer.skip) return '商店：不买了（金币 ' + (req.gold ?? '?') + '）' + (why ? '；' + why : '');
      const p = answer && req.options ? req.options[answer.index] : null;
      return '商店：买了「' + (p ? p.info : '?') + '」（金币 ' + (req.gold ?? '?') + '）' + (why ? '；' + why : '');
    }
    if (kind === 'combat_turn') {
      if (answer && answer.end) return '结束回合（能量 ' + (req.energy ?? '?') + '）' + (why ? '；' + why : '');
      const h = answer && req.hand ? req.hand[answer.i] : null;
      const tgt = answer && req.enemies && answer.t >= 0 ? req.enemies[answer.t] : null;
      return '出牌：' + (h ? h.title : '?') + (tgt ? ' → ' + tgt.name : '') +
        (h && h.dmg ? '（伤害 ' + h.dmg + '）' : '') + (why ? '；' + why : '');
    }
    return '';
  } catch { return ''; }
}

function recordDecision(kind, req, answer, why, ms, tok) {
  // 同一次决策被重复调用（比如地图点两次）时，只记第一条
  try {
    const kk = String(kind || '');
    const cc = String(answer && (answer.chose || answer.title) || '');
    const nowT = Date.now();
    if (kk && cc && lastRecRef.kind === kk && lastRecRef.chose === cc && nowT - lastRecRef.at < 2000) {
      return;
    }
    lastRecRef = { kind: kk, chose: cc, at: nowT };
  } catch { /* ignore */ }

  try {
    memEnsure();
    const row = {
      t: Date.now(),
      kind,
      ms: ms ?? null,
      tok: tok ?? 0,
      chose: summarizeChoice(kind, req, answer),
      why: why || '',
      explain: buildExplain(kind, req, answer, why)
    };
    fs.appendFileSync(DEC_FILE, JSON.stringify(row) + '\n', 'utf8');
    capLines(DEC_FILE, 800);
    dumpPanelData();
  } catch { /* ignore */ }
}

function summarizeChoice(kind, req, answer) {
  try {
    if (kind === 'pick_map') return (req.nodes || [])[answer?.index]?.type || '';
    if (kind === 'pick_cards') return (req.options || [])[answer?.picks?.[0]]?.title || '';
    if (kind === 'pick_option') return (req.options || [])[answer?.index]?.label || '';
    if (kind === 'pick_shop') return answer?.skip ? '不买' : ((req.options || [])[answer?.index]?.info || '');
    if (kind === 'combat_turn') return answer?.end ? '结束回合' : ((req.hand || [])[answer?.i]?.title || '');
    if (kind === 'pick_character') return (req.options || [])[answer?.index]?.name || '';
    return '';
  } catch { return ''; }
}


// ── 面板数据（同源，供 /plugin-panel/<id>/@data/ 读取）──────────
const PANEL_DATA_DIR = path.join(AGENT_DATA, 'plugin-data');

function dumpPanelData() {
  try {
    if (!fs.existsSync(PANEL_DATA_DIR)) fs.mkdirSync(PANEL_DATA_DIR, { recursive: true });
    const rows = readLines(DEC_FILE).slice(-200).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
    fs.writeFileSync(PANEL_DATA_DIR + '\\decisions.json', JSON.stringify({ ok: true, rows }, null, 0), 'utf8');

    const combat = readLines(MEM_COMBAT).slice(-5);
    let chat = '';
    try {
      if (fs.existsSync(MEM_CHAT)) chat = fs.readFileSync(MEM_CHAT, 'utf8').replace(/<!-- run:[^>]*-->/g, '').trim();
    } catch { /* ignore */ }
    fs.writeFileSync(PANEL_DATA_DIR + '\\memory.json', JSON.stringify({ ok: true, combat, chat }, null, 0), 'utf8');
  } catch { /* ignore */ }
}


// ── 气泡浮窗：开局时拉起，退出时关闭 ──────────────────────
import path from 'node:path';
import { fileURLToPath } from 'node:url';




const OVERLAY_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), 'overlay');
const OVERLAY_EXE = path.join(OVERLAY_DIR, 'bubbles.exe');
let overlayProc = null;

function startOverlay() {
  try {
    if (cfg().overlayEnabled === false) { if (api && api.log) api.log('浮窗已关闭（设置里关掉了）'); return; }
    if (overlayProc && !overlayProc.killed) return;
    if (!fs.existsSync(OVERLAY_EXE)) { if (api && api.error) api.error('浮窗程序不存在: ' + OVERLAY_EXE); return; }
    overlayProc = exec('"' + OVERLAY_EXE + '"', { windowsHide: true });
    overlayProc.on('exit', () => { overlayProc = null; });
    if (api && api.log) api.log('气泡浮窗已启动');
  } catch (e) {
    if (api && api.error) api.error('启动浮窗失败: ' + (e?.message || e));
  }
}

function stopOverlay() {
  try {
    if (overlayProc && !overlayProc.killed) overlayProc.kill();
    overlayProc = null;
  } catch { /* ignore */ }
}



// 结算收尾：发截图 → 删文件 → 关游戏 → 关浮窗
async function finishRunWithShot(st) {
  const shot = String((st && st.shot) || '');
  // 等截图动画/结算页画完
  await new Promise((r) => setTimeout(r, 3000));
  let key = lastChatKey;
  if (!key) {
    try { if (fs.existsSync(LAST_CHAT_FILE)) key = fs.readFileSync(LAST_CHAT_FILE, 'utf8').trim(); } catch { /* ignore */ }
  }
  if (shot && fs.existsSync(shot)) {
    if (key) {
      try {
        const r = await api.capability('chat.send-image', { chatKey: key, file: shot });
        if (api && api.log) api.log('结算截图已发送' + (r && r.ok === false ? '（返回失败）' : ''));
      } catch (e) {
        if (api && api.error) api.error('结算截图发送失败: ' + (e?.message || e));
      }
    } else if (api && api.log) api.log('结算截图跳过：没有 chatKey');
    try { fs.unlinkSync(shot); } catch { /* ignore */ }
  }
  // 关游戏 + 关浮窗
  try { stopOverlay(); } catch { /* ignore */ }
  try {
    const alive = await killGame();
    if (api && api.log) api.log('结算后关闭游戏' + (alive ? '（原在运行）' : '（已关闭）'));
  } catch (e) {
    if (api && api.error) api.error('结算后关游戏失败: ' + (e?.message || e));
  }
}

// ── 结算播报：先发模板，再让她自己说两句 ─────────────────
function charNameOf(id) {
  const hit = CHARACTERS.find((c) => c.id === id);
  return hit ? hit.name : (id || '某个角色');
}

async function sendChat(text) {
  try {
    const cap = api && api.capability ? api.capability('chat.send', { chatKey: lastChatKey, text }) : null;
    if (cap && typeof cap.then === 'function') await cap;
    return true;
  } catch (e) {
    if (api && api.error) api.error('结算播报发送失败: ' + (e?.message || e));
    return false;
  }
}

async function announceRunEnd(stats) {
  if (!lastChatKey) {
    try { if (fs.existsSync(LAST_CHAT_FILE)) lastChatKey = fs.readFileSync(LAST_CHAT_FILE, 'utf8').trim(); } catch { /* ignore */ }
  }
  if (!lastChatKey) { if (api && api.log) api.log('结算播报跳过：没有 chatKey'); return; }
  const who = charNameOf(stats.char);
  const floor = stats.floor || 0;
  const won = stats.result === 'win';
  await sendChat('刚才那局' + (won ? '赢了' : '输了') + ' —— ' + who + '，第 ' + floor + ' 层。');

  // 等记忆写好后，让她自己说两句
  await new Promise((r) => setTimeout(r, 2500));
  let recap = '';
  try {
    if (fs.existsSync(MEM_CHAT)) {
      const txt = fs.readFileSync(MEM_CHAT, 'utf8');
      const parts = txt.split(/<!-- run:[^>]*-->/).filter((x) => x.trim());
      recap = parts.length ? parts[parts.length - 1].trim().slice(0, 600) : '';
    }
  } catch { /* ignore */ }

  const persona = personaText();
  try {
    const sys = (persona ? persona + '\n\n' : '') +
      '你刚打完一局《杀戮尖塔2》。下面是你这一局的复盘。' +
      '请用你自己的口吻，对主人说一两句话（不超过 60 字），可以是抱怨、总结或求安慰，不要罗列数据，不要复述复盘原文。';
    const r = await chatCompletionWithRetry({
      messages: [
        { role: 'system', content: sys },
        { role: 'user', content: '本局结果：' + (won ? '通关' : '失败') + '，角色 ' + who + '，第 ' + floor + ' 层。\n复盘：' + (recap || '（没有复盘）') }
      ],
      tools: null,
      temperature: 0.9,
      maxTokens: 200,
      skillContext: { thinkingMode: 'off' },
      tag: 'sts2-afterrun'
    }, 1);
    const say = String(r?.message?.content || '').trim();
    if (api && api.log) api.log('赛后发言: 长度=' + say.length + (say ? ' 内容=' + say.slice(0, 40) : '（空）'));
    if (say) {
      const ok = await sendChat(say);
      if (api && api.log) api.log('赛后发言发送: ' + (ok ? '成功' : '失败'));
    }
  } catch (e) {
    if (api && api.error) api.error('赛后发言失败: ' + (e?.message || e));
  }
}

// ── QQ 触发：收到"玩一把杀戮尖塔2"就开游戏（必须走 Steam 启动）──────
const CHAR_ALIASES = [
  { re: /战士|铁甲|ironclad/i, id: 'ironclad' },
  { re: /猎人|静默|silent/i, id: 'silent' },
  { re: /机器人|故障|defect/i, id: 'defect' },
  { re: /君王|君主|regent/i, id: 'regent' },
  { re: /亡灵|死灵|necrobinder/i, id: 'necrobinder' }
];


let lastLaunchAt = 0;
let lastChatKey = '';
let lastRecRef = { kind: '', chose: '', at: 0 };
const seenMids = new Set();

function launchGame() {
  exec('cmd /c start "" "steam://rungameid/2868840"', (e) => {
    if (e && api && api.error) api.error('启动游戏失败: ' + e.message);
  });
}


// ── 聊天注入：每轮把她的游戏记忆带上 ──────────────────────
// 把 "ironclad 第10层 阵亡 | 出牌0 | 回合0 | 2026-10-27" 变成好读的中文
function prettyGameLine(raw) {
  try {
    const parts = String(raw).split('|').map((x) => x.trim());
    const head = parts[0] || '';
    const m = head.match(/^(\S+)\s*第\s*(\S+)\s*层\s*(\S+)/);
    if (!m) return '';
    const id = m[1].trim();
    const floor = m[2].trim();
    const outcome = m[3].trim();
    if (!floor || floor === '?' || !id || id === '?') return '';   // 数据缺失的行直接丢掉
    const hit = CHARACTERS.find((c) => c.id === id);
    const name = hit ? hit.name : id;
    const cards = (parts[1] || '').replace('出牌', '').trim();
    const turns = (parts[2] || '').replace('回合', '').trim();
    let line = name + ' · 第 ' + floor + ' 层 · ' + outcome;
    if (cards && cards !== '0') line += ' · 打了 ' + cards + ' 张牌';
    if (turns && turns !== '0') line += ' · ' + turns + ' 回合';
    return line;
  } catch { return ''; }
}

function memoryBlock() {
  const combat = readLines(MEM_COMBAT).map(prettyGameLine).filter(Boolean).slice(-5);
  let chat = '';
  try {
    if (fs.existsSync(MEM_CHAT)) {
      const txt = fs.readFileSync(MEM_CHAT, 'utf8');
      chat = txt.replace(/<!-- run:[^>]*-->/g, '').trim();
    }
  } catch { /* ignore */ }
  if (!combat.length && !chat) return '';

  const lines = ['【关于你自己最近在玩的游戏】',
      '【硬规则】你没有能力自己启动任何游戏（包括《杀戮尖塔2》）。想玩只能求主人给你开，主人开了你才玩得上；不要说「我去开」「我开一把」这种你没有能力做到的事。',
    '下面都是**过去的**对局记录，不是当前这一局。',
    '你现在看不到游戏的实时画面（手牌、遗物、事件选项你都看不见），所以**不要编**当前这局的手牌、遗物或事件细节；',
    '想聊当前局，就等有人把具体情况告诉你，或者说点感受/思路这类不需要具体数据的话。'];
  if (combat.length) {
    lines.push('最近几局：');
    for (const c of combat) lines.push('· ' + c);
  }
  if (chat) {
    lines.push('你上两局的复盘（你自己写的话）：');
    lines.push(chat.slice(0, 700));
  }
  lines.push('聊天聊到游戏时可以自然提一句，但不要每次都提，也别反复讲同一局。');
  return lines.join('\n');
}

export const hooks = {
  // 每轮聊天前注入游戏记忆（没有记忆就什么都不做）
  async 'before-llm-messages'(ctx = {}) {
    try {
      const block = memoryBlock();
      if (!block) return;
      const msgs = ctx.messages;
      if (!Array.isArray(msgs)) return;
      const MARK = '【关于你自己最近在玩的游戏】';
      // 先清掉旧的同一段，避免每轮累积（曾经把 prompt 撑到 14 万 token）
      for (let i = msgs.length - 1; i >= 0; i--) {
        const m = msgs[i];
        if (m && m.role === 'system' && typeof m.content === 'string' && m.content.includes(MARK)) {
          msgs.splice(i, 1);
        }
      }
      msgs.push({ role: 'system', content: block });
    } catch (e) {
      if (api && api.error) api.error('注入记忆失败: ' + (e?.message || e));
    }
  },

  async onMessage(ctx = {}) {
    try {
      const text = String(ctx.text || '');
      if (ctx.chatKey) {
        lastChatKey = String(ctx.chatKey);
        try { memEnsure(); fs.writeFileSync(LAST_CHAT_FILE, lastChatKey, 'utf8'); } catch { /* ignore */ }
      }


      // 所有开/续/退都必须先提到「杀戮尖塔」（或 sts2），避免"继续玩""保存退出"这种太宽松的说法误触发
      const mentionsGame = /杀戮尖塔|sts2/i.test(text);

      // "接着玩"：强制继续存档，不选角色、不放弃旧局
      if (mentionsGame && /(继续|接着|续)/.test(text) && /(玩|打|来|存档|一局)/.test(text)) {
        const qmid2 = String(ctx.mid || '');
        if (qmid2 && seenMids.has('c' + qmid2)) return;
        if (qmid2) seenMids.add('c' + qmid2);

        try { fs.writeFileSync(NEXT_CHAR, 'auto', 'utf8'); } catch { /* ignore */ }
        try { fs.writeFileSync(AUTOPLAY_FLAG, String(Date.now()), 'utf8'); } catch { /* ignore */ }
        lastLaunchAt = Date.now();
        startOverlay();
        try { fs.writeFileSync(DEC_FILE, '', 'utf8'); } catch { /* ignore */ }
        launchGame();
        if (api && api.log) api.log('QQ 触发继续存档');
        if (ctx.send) await ctx.send(['好，接着上一局～']);
        return;
      }

      // 退出指令：秒杀进程，记忆后台写（钩子必须 <5 秒返回）
      if (mentionsGame && /保存.*退出|退出|关掉|关闭|停止|别玩了|不玩了/.test(text)) {
        const qmid = String(ctx.mid || '');
        if (qmid && seenMids.has('q' + qmid)) return;
        if (qmid) seenMids.add('q' + qmid);
        stopOverlay();
        const alive = await killGame();
        generateMemory({ result: 'quit' }).catch(() => {});
        if (api && api.log) api.log('QQ 触发退出: alive=' + alive);
        if (ctx.send) await ctx.send([alive ? '好，我保存退出了～' : '游戏本来就没开呀']);
        return;
      }
      if (!mentionsGame) return;
      if (!/玩|开|来一?局/.test(text)) return;

      const mid = String(ctx.mid || '');
      if (mid && seenMids.has(mid)) return;
      if (mid) { seenMids.add(mid); if (seenMids.size > 200) seenMids.clear(); }

      const now = Date.now();
      if (now - lastLaunchAt < 30000) {
        if (ctx.send) await ctx.send(['刚开过一局，等它跑起来～']);
        return;
      }

      let picked = null;
      for (const a of CHAR_ALIASES) {
        if (a.re.test(text)) { picked = a.id; break; }
      }

      let charName = '';
      if (picked) {
        const c = CHARACTERS.find((x) => x.id === picked);
        charName = c ? c.name : picked;
      } else {
        picked = 'auto';
      }

      try { fs.writeFileSync(NEXT_CHAR, picked, 'utf8'); } catch (e) { /* ignore */ }
      try { fs.writeFileSync(AUTOPLAY_FLAG, String(Date.now()), 'utf8'); }
      catch (e) { if (api && api.error) api.error('写角色文件失败: ' + (e?.message || e)); }

      lastLaunchAt = now;
      startOverlay();
      // 新的一局：清空决策记录，面板只显示本局
      try { fs.writeFileSync(DEC_FILE, '', 'utf8'); } catch { /* ignore */ }
      launchGame();

      const reply = picked === 'auto'
        ? '好，我这就开一局～'
        : '好，我这就开一局，玩' + (charName || picked) + '～';
      if (ctx.send) await ctx.send([reply]);
      if (picked === 'auto') {
        decideCharacter().then((dec) => {
          if (!dec) return;
          try {
            fs.writeFileSync(NEXT_CHAR, dec.id, 'utf8');
            if (api && api.log) api.log('后台定角色: ' + dec.name);
          } catch { /* ignore */ }
        }).catch(() => {});
      }
      if (api && api.log) api.log('QQ 触发开局: character=' + picked);
    } catch (e) {
      if (api && api.error) api.error('onMessage 处理失败: ' + (e?.message || e));
    }
  }
};
