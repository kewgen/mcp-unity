import { describe, it, expect } from '@jest/globals';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

type McpConfig = {
  mcpServers?: Record<string, { env?: Record<string, string> }>;
};

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const mcpConfigPath = path.resolve(__dirname, '../../../../.mcp.json');

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
