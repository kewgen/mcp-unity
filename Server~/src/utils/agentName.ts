import { readdirSync, readFileSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

/**
 * Имя агента для заголовка X-Client-Name: Unity пишет его в лог вместо «Unknown MCP Client»,
 * и видно, какая из параллельных сессий шлёт запрос.
 *
 * Порядок: явное MCP_CLIENT_NAME → имя вкладки Claude Code из реестра ~/.claude/sessions
 * (по CLAUDE_CODE_SESSION_ID; читается при каждом подключении, поэтому переименование
 * вкладки подхватывается после переподключения) → AGENT_ID → пустая строка.
 * Та же логика — в park scripts/mcp-unity-call.mjs.
 */
export function agentName(env: NodeJS.ProcessEnv = process.env): string {
  if (env.MCP_CLIENT_NAME) return env.MCP_CLIENT_NAME;
  const sid = env.CLAUDE_CODE_SESSION_ID;
  if (sid) {
    const dir = join(env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude'), 'sessions');
    let best: { name: string; mtime: number } | null = null;
    try {
      for (const file of readdirSync(dir)) {
        if (!file.endsWith('.json')) continue;
        try {
          const record = JSON.parse(readFileSync(join(dir, file), 'utf8'));
          if (record.sessionId !== sid || !record.name) continue;
          const mtime = statSync(join(dir, file)).mtimeMs;
          if (!best || mtime > best.mtime) best = { name: String(record.name), mtime };
        } catch {
          // чужой или недописанный файл реестра — пропустить
        }
      }
    } catch {
      // реестра нет — клиент не Claude Code
    }
    if (best) return best.name;
  }
  return env.AGENT_ID || '';
}
