/**
 * 最小可用的模型调用（OpenAI 兼容接口）
 *
 * 目的：让这个决策服务在没有宿主的情况下也能跑。
 * 只实现决策需要的两件事：发 chat/completions、把结果整理成统一结构。
 *
 * 配置优先级：环境变量 > %APPDATA%\Sts2Agent\config.json 的 llm 段
 *
 *   STS2_LLM_BASE_URL  例如 https://api.deepseek.com/v1
 *   STS2_LLM_API_KEY   sk-...
 *   STS2_LLM_MODEL     例如 deepseek-chat
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

function readConfig() {
  try {
    const p = path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'Sts2Agent', 'config.json');
    if (!fs.existsSync(p)) return {};
    const j = JSON.parse(fs.readFileSync(p, 'utf8'));
    return j.llm || {};
  } catch { return {}; }
}

const cfg = readConfig();
const BASE_URL = process.env.STS2_LLM_BASE_URL || cfg.baseUrl || 'https://api.deepseek.com/v1';
const API_KEY = process.env.STS2_LLM_API_KEY || cfg.apiKey || '';
const MODEL = process.env.STS2_LLM_MODEL || cfg.model || 'deepseek-chat';

function buildBody(args) {
  const body = {
    model: args.model || MODEL,
    messages: args.messages,
    temperature: typeof args.temperature === 'number' ? args.temperature : 0,
    stream: false,
  };
  if (args.maxTokens) body.max_tokens = Math.floor(args.maxTokens);
  if (Array.isArray(args.tools) && args.tools.length) body.tools = args.tools;
  return body;
}

/** 发一次请求，返回 { message, usage, raw, model }（和宿主的 chatCompletion 对齐） */
export async function chatCompletion(args) {
  if (!API_KEY) {
    throw new Error('没有配置模型 API Key：设置环境变量 STS2_LLM_API_KEY，或在 config.json 的 llm.apiKey 里填');
  }
  const url = BASE_URL.replace(/\/+$/, '') + '/chat/completions';
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + API_KEY },
    body: JSON.stringify(buildBody(args)),
    signal: args.signal,
  });
  const text = await res.text();
  if (!res.ok) throw new Error('HTTP ' + res.status + ': ' + text.slice(0, 300));

  let data;
  try { data = JSON.parse(text); } catch { throw new Error('返回不是 JSON: ' + text.slice(0, 200)); }

  const choice = (data.choices && data.choices[0]) || {};
  return {
    message: { content: choice.message?.content ?? '', finish_reason: choice.finish_reason ?? '' },
    usage: data.usage || {},
    raw: data,
    model: data.model || args.model || MODEL,
  };
}

/** 带重试的版本（签名和宿主一致：args, retries, opts） */
export async function chatCompletionWithRetry(args, retries = 2, { onAttempt = null } = {}) {
  let lastError = null;
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      if (onAttempt) { try { onAttempt(); } catch { /* ignore */ } }
      return await chatCompletion(args);
    } catch (error) {
      lastError = error;
      const msg = String(error?.message || error);
      const retryable = /HTTP (5\d\d|429)|ECONN|ETIMEDOUT|fetch failed/i.test(msg);
      if (attempt >= retries || !retryable) break;
      await new Promise((r) => setTimeout(r, 1000 * Math.pow(2, attempt)));
    }
  }
  throw lastError || new Error('模型调用失败');
}
