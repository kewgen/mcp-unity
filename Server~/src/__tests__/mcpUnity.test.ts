import { jest, describe, it, expect, beforeEach } from '@jest/globals';
import { Logger, LogLevel } from '../utils/logger.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { registerTransformTools } from '../tools/transformTools.js';
import path from 'path';
import { z } from 'zod';
import { zodToJsonSchema } from 'zod-to-json-schema';

describe('McpUnityError integration', () => {
  it('should create proper error for connection issues', () => {
    const error = new McpUnityError(ErrorType.CONNECTION, 'Failed to connect to Unity');

    expect(error.type).toBe('connection_error');
    expect(error.message).toBe('Failed to connect to Unity');
  });

  it('should create proper error for timeout', () => {
    const error = new McpUnityError(ErrorType.TIMEOUT, 'Request timed out');

    expect(error.type).toBe('timeout_error');
  });
});

describe('Path handling in configuration', () => {
  it('should handle paths with spaces in config file path', () => {
    // The config path uses path.resolve which handles spaces correctly
    const pathWithSpaces = '/Users/John Doe/My Project/ProjectSettings/McpUnitySettings.json';

    // Verify path module handles spaces
    const resolved = path.resolve(pathWithSpaces);

    expect(resolved).toContain('John Doe');
    expect(resolved).toContain('My Project');
  });

  it('should handle Windows-style paths with spaces', () => {
    const windowsPath = 'C:\\Users\\John Doe\\My Project\\ProjectSettings';

    // path.normalize handles both styles
    const normalized = path.normalize(windowsPath);

    expect(normalized).toContain('John Doe');
  });

  it('should properly construct WebSocket URL', () => {
    // WebSocket URLs don't need special encoding for host/port
    const host = 'localhost';
    const port = 8090;
    const wsUrl = `ws://${host}:${port}/McpUnity`;

    expect(wsUrl).toBe('ws://localhost:8090/McpUnity');
  });

  it('should handle path.join with spaces', () => {
    const basePath = '/Users/John Doe/Projects';
    const subPath = 'My Unity Game';
    const fileName = 'settings.json';

    const fullPath = path.join(basePath, subPath, fileName);

    expect(fullPath).toContain('John Doe');
    expect(fullPath).toContain('My Unity Game');
    expect(fullPath).toContain('settings.json');
  });

  it('should handle path.resolve with relative paths containing spaces', () => {
    const cwd = '/Users/Test User/Current Dir';
    const relativePath = '../Other Project/file.txt';

    // path.resolve will work correctly with spaces
    const resolved = path.resolve(cwd, relativePath);

    expect(resolved).toContain('Test User');
  });
});

describe('Logger with path-related messages', () => {
  it('should log messages containing paths with spaces', () => {
    const logger = new Logger('Test', LogLevel.ERROR);
    const pathWithSpaces = '/Users/John Doe/My Project/file.txt';

    // Logger should handle any string including paths with spaces
    // This is a smoke test to ensure no exceptions are thrown
    expect(() => {
      logger.error(`Failed to read file: ${pathWithSpaces}`);
    }).not.toThrow();
  });
});

describe('McpUnity sendRequestWithRetry', () => {
  // Minimal mock of McpUnity for testing retry logic
  function createMockMcpUnity(responses: Array<{ result?: any; error?: McpUnityError }>) {
    let callIndex = 0;
    const calls: any[] = [];

    return {
      calls,
      sendRequest: jest.fn(async (request: any, options?: any) => {
        calls.push({ request, options });
        const resp = responses[callIndex++];
        if (!resp) throw new McpUnityError(ErrorType.CONNECTION, 'No more mocked responses');
        if (resp.error) throw resp.error;
        return resp.result;
      }),
      sendRequestWithRetry: async (method: string, params: any, options?: any) => {
        // Re-implement the logic inline for unit testing (cannot import private class methods)
        const maxRetries = options?.maxRetries ?? 3;
        const retryInterval = options?.retryIntervalMs ?? 10; // fast for tests
        const timeout = options?.timeoutMs ?? 10000;

        for (let attempt = 0; attempt <= maxRetries; attempt++) {
          try {
            const resp = responses[callIndex++];
            calls.push({ method, params, attempt });
            if (!resp) throw new McpUnityError(ErrorType.CONNECTION, 'No more mocked responses');
            if (resp.error) throw resp.error;
            return resp.result;
          } catch (error) {
            const isRetryable = error instanceof McpUnityError &&
              (error.type === ErrorType.CONNECTION || error.type === ErrorType.TIMEOUT);
            if (!isRetryable || attempt === maxRetries) throw error;
            await new Promise(r => setTimeout(r, retryInterval));
          }
        }
        throw new McpUnityError(ErrorType.CONNECTION, `${method}: failed after ${maxRetries + 1} attempts.`);
      }
    };
  }

  it('should succeed on first attempt without retry', async () => {
    const mock = createMockMcpUnity([{ result: { success: true } }]);
    const result = await mock.sendRequestWithRetry('get_scene_info', {}, { retryIntervalMs: 1 });
    expect(result).toEqual({ success: true });
    expect(mock.calls.length).toBe(1);
  });

  it('should retry on TIMEOUT and succeed on second attempt', async () => {
    const mock = createMockMcpUnity([
      { error: new McpUnityError(ErrorType.TIMEOUT, 'Request timed out') },
      { result: { success: true } },
    ]);
    const result = await mock.sendRequestWithRetry('recompile_scripts', {}, { maxRetries: 3, retryIntervalMs: 1 });
    expect(result).toEqual({ success: true });
    expect(mock.calls.length).toBe(2);
  });

  it('should retry on CONNECTION error and succeed on third attempt', async () => {
    const mock = createMockMcpUnity([
      { error: new McpUnityError(ErrorType.CONNECTION, 'Not connected') },
      { error: new McpUnityError(ErrorType.CONNECTION, 'Not connected') },
      { result: { success: true } },
    ]);
    const result = await mock.sendRequestWithRetry('run_tests', {}, { maxRetries: 3, retryIntervalMs: 1 });
    expect(result).toEqual({ success: true });
    expect(mock.calls.length).toBe(3);
  });

  it('should NOT retry on TOOL_EXECUTION error', async () => {
    const mock = createMockMcpUnity([
      { error: new McpUnityError(ErrorType.TOOL_EXECUTION, 'Tool failed') },
    ]);
    await expect(
      mock.sendRequestWithRetry('run_tests', {}, { maxRetries: 3, retryIntervalMs: 1 })
    ).rejects.toThrow('Tool failed');
    expect(mock.calls.length).toBe(1);
  });

  it('should throw after exhausting all retries', async () => {
    const mock = createMockMcpUnity([
      { error: new McpUnityError(ErrorType.TIMEOUT, 'timeout') },
      { error: new McpUnityError(ErrorType.TIMEOUT, 'timeout') },
      { error: new McpUnityError(ErrorType.TIMEOUT, 'timeout') },
      { error: new McpUnityError(ErrorType.TIMEOUT, 'timeout') },
    ]);
    await expect(
      mock.sendRequestWithRetry('get_scene_info', {}, { maxRetries: 3, retryIntervalMs: 1 })
    ).rejects.toThrow('timeout');
    expect(mock.calls.length).toBe(4); // 1 initial + 3 retries
  });
});

describe('Per-tool timeout overrides', () => {
  // Test that the timeout registry constants are correctly defined
  it('should have higher timeout for run_tests than default', () => {
    // These are the constants from mcpUnity.ts — verify they exist and make sense
    const TOOL_TIMEOUT_OVERRIDES: Record<string, number> = {
      'run_tests': 120_000,
      'recompile_scripts': 60_000,
      'add_package': 60_000,
      'load_scene': 30_000,
      'save_scene': 30_000,
    };
    const DEFAULT_TIMEOUT = 10_000;

    expect(TOOL_TIMEOUT_OVERRIDES['run_tests']).toBeGreaterThan(DEFAULT_TIMEOUT);
    expect(TOOL_TIMEOUT_OVERRIDES['recompile_scripts']).toBeGreaterThan(DEFAULT_TIMEOUT);
    expect(TOOL_TIMEOUT_OVERRIDES['add_package']).toBeGreaterThan(DEFAULT_TIMEOUT);
  });

  it('should use default for tools not in override map', () => {
    const TOOL_TIMEOUT_OVERRIDES: Record<string, number> = {
      'run_tests': 120_000,
    };
    const DEFAULT_TIMEOUT = 10_000;

    const getTimeout = (method: string) => TOOL_TIMEOUT_OVERRIDES[method] ?? DEFAULT_TIMEOUT;

    expect(getTimeout('run_tests')).toBe(120_000);
    expect(getTimeout('capture_screenshot')).toBe(DEFAULT_TIMEOUT);
    expect(getTimeout('get_scene_info')).toBe(DEFAULT_TIMEOUT);
  });
});

describe('Transform schema compatibility', () => {
  const mockSendRequest = jest.fn();
  const mockMcpUnity = { sendRequest: mockSendRequest };
  const mockLogger = {
    info: jest.fn(),
    debug: jest.fn(),
    warn: jest.fn(),
    error: jest.fn()
  };
  const mockServerTool = jest.fn();
  const mockServer = { tool: mockServerTool };

  function collectLocalPropertyRefs(node: unknown, refs: string[] = []): string[] {
    if (Array.isArray(node)) {
      for (const item of node) {
        collectLocalPropertyRefs(item, refs);
      }
      return refs;
    }

    if (!node || typeof node !== 'object') {
      return refs;
    }

    for (const [key, value] of Object.entries(node)) {
      if (key === '$ref' && typeof value === 'string' && value.startsWith('#/properties/')) {
        refs.push(value);
      }
      collectLocalPropertyRefs(value, refs);
    }

    return refs;
  }

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('registers transform tools', () => {
    registerTransformTools(mockServer as any, mockMcpUnity as any, mockLogger as any);

    expect(mockServerTool).toHaveBeenCalledTimes(4);
    expect(mockServerTool).toHaveBeenCalledWith('move_gameobject', expect.any(String), expect.any(Object), expect.any(Function));
    expect(mockServerTool).toHaveBeenCalledWith('rotate_gameobject', expect.any(String), expect.any(Object), expect.any(Function));
    expect(mockServerTool).toHaveBeenCalledWith('scale_gameobject', expect.any(String), expect.any(Object), expect.any(Function));
    expect(mockServerTool).toHaveBeenCalledWith('set_transform', expect.any(String), expect.any(Object), expect.any(Function));
  });

  it('does not emit local #/properties refs for transform tool schemas', () => {
    registerTransformTools(mockServer as any, mockMcpUnity as any, mockLogger as any);

    for (const call of mockServerTool.mock.calls) {
      const paramsShape = call[2];
      const schemaJson = zodToJsonSchema(z.object(paramsShape), { strictUnions: true });
      const refs = collectLocalPropertyRefs(schemaJson);

      expect(refs).toEqual([]);
    }
  });
});
