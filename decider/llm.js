/**
 * 模型调用的统一入口：优先用宿主提供的实现（如果有），否则用自带的最小实现。
 *
 * 为什么这么写：
 *   - 在 DeepSeek Harness 里跑时，宿主的 ../../src/llm.js 提供完整能力（模型池、缓存、重试）
 *   - 别人单独跑时没有那个文件，自动退回 ./llm-shim.js（OpenAI 兼容接口）
 *
 * 想强制用某一边：设置环境变量 STS2_LLM_IMPL=host 或 STS2_LLM_IMPL=shim
 */

let impl = null;
let usedFrom = '';

const forced = String(process.env.STS2_LLM_IMPL || '').toLowerCase();

if (forced !== 'shim') {
  try {
    const mod = await import('../../src/llm.js');
    if (mod && typeof mod.chatCompletionWithRetry === 'function') {
      impl = mod.chatCompletionWithRetry;
      usedFrom = 'host(../../src/llm.js)';
    }
  } catch { /* 没有宿主，正常 */ }
}

if (!impl) {
  const mod = await import('./llm-shim.js');
  impl = mod.chatCompletionWithRetry;
  usedFrom = 'shim(OpenAI 兼容接口)';
}

export const chatCompletionWithRetry = impl;
export const llmSource = usedFrom;
