import { describe, it, expect } from '@jest/globals';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

type McpConfig = {
  mcpServers?: Record<string, { env?: Record<string, string> }>;
};

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Репозиторий моста лежит рядом с park только в главном чекауте; из полосы-worktree
// (pfp2/lanes/<имя>, ADR-0018) относительный путь «на четыре вверх» указывает мимо.
// Ищем .mcp.json подъёмом по дереву, а не жёстким числом «..».
function findMcpConfigPath(): string {
  const candidates: string[] = [];
  let dir = __dirname;

  for (let i = 0; i < 8; i++) {
    candidates.push(path.join(dir, '.mcp.json'), path.join(dir, 'park', '.mcp.json'));
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }

  const found = candidates.find((candidate) => {
    if (!fs.existsSync(candidate)) return false;
    try {
      const raw = JSON.parse(fs.readFileSync(candidate, 'utf-8')) as McpConfig;
      return Boolean(raw.mcpServers?.['user-mcp-unity']);
    } catch {
      return false;
    }
  });

  if (!found) {
    throw new Error(`.mcp.json с сервером user-mcp-unity не найден, искал от ${__dirname} вверх`);
  }

  return found;
}

const mcpConfigPath = findMcpConfigPath();

function readMcpConfig(): McpConfig {
  const raw = fs.readFileSync(mcpConfigPath, 'utf-8');
  return JSON.parse(raw) as McpConfig;
}

describe('Proxy contract for user-mcp-unity', () => {
  it('keeps local loopback in no_proxy for Unity bridge', () => {
    const config = readMcpConfig();
    const env = config.mcpServers?.['user-mcp-unity']?.env;

    expect(env).toBeDefined();
    expect(env?.NO_PROXY).toBe('127.0.0.1,localhost');
    expect(env?.no_proxy).toBe('127.0.0.1,localhost');
  });

  it('explicitly disables inherited HTTP(S) proxy variables', () => {
    const config = readMcpConfig();
    const env = config.mcpServers?.['user-mcp-unity']?.env;

    // Empty values keep MCP traffic deterministic and avoid accidental parent-shell proxy inheritance.
    expect(env?.HTTP_PROXY).toBe('');
    expect(env?.HTTPS_PROXY).toBe('');
    expect(env?.http_proxy).toBe('');
    expect(env?.https_proxy).toBe('');
  });
});
