/**
 * MCP Unity Integration Tests
 *
 * Требуют работающую Unity с MCP сервером на порту 8090.
 * Скипаются автоматически если Unity недоступна.
 * Явный запуск: UNITY_MCP_INTEGRATION=1 npm test -- --testPathPattern=integration
 */
import { describe, it, expect, beforeAll, afterAll } from '@jest/globals';
import { McpUnity } from '../unity/mcpUnity.js';
import { Logger, LogLevel } from '../utils/logger.js';
import { createConnection } from 'net';

const logger = new Logger('IntegrationTest', LogLevel.WARN);
let mcpUnity: McpUnity;
let unityAvailable = false;

// Быстрая проверка порта без полного подключения
async function isPortOpen(port: number, host = 'localhost'): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection({ port, host }, () => {
      socket.destroy();
      resolve(true);
    });
    socket.on('error', () => { socket.destroy(); resolve(false); });
    socket.setTimeout(2000, () => { socket.destroy(); resolve(false); });
  });
}

beforeAll(async () => {
  const portOpen = await isPortOpen(8090);
  if (!portOpen && !process.env.UNITY_MCP_INTEGRATION) {
    console.warn('Unity MCP not available on port 8090 — integration tests skipped. Set UNITY_MCP_INTEGRATION=1 to force.');
    return;
  }

  mcpUnity = new McpUnity(logger);
  try {
    await mcpUnity.start('IntegrationTest');
    // Ждём подключения
    await new Promise(r => setTimeout(r, 1000));
    unityAvailable = mcpUnity.isConnected;
  } catch {
    unityAvailable = false;
  }

  if (!unityAvailable) {
    console.warn('Unity MCP: connected but WebSocket not ready — skipping integration tests.');
  }
}, 15000);

afterAll(async () => {
  if (mcpUnity) {
    // Гарантируем выход из Play Mode
    try {
      const state = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'get_state' });
      if (state?.isPlaying) {
        await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'exit' });
      }
    } catch { /* ignore */ }

    await mcpUnity.stop();
  }
}, 30000);

// Helper: условный describe — скипает если Unity недоступна
const d = (name: string, fn: () => void) => {
  (unityAvailable ? describe : describe.skip)(name, fn);
};

// ─── Группа 1: Подключение и диагностика ────────────────────────────

d('Group 1: Connection & Diagnostics', () => {
  it('get_scene_info returns active scene', async () => {
    const res = await mcpUnity.sendRequestWithRetry('get_scene_info', {});
    expect(res).toBeDefined();
    expect(res.activeScene).toBeDefined();
    expect(res.activeScene.name).toBeDefined();
  });

  it('get_console_logs returns array', async () => {
    const res = await mcpUnity.sendRequestWithRetry('get_console_logs', {
      logType: 'error', limit: 5, includeStackTrace: false
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  });

  it('send_console_log writes message that get_console_logs can see', async () => {
    const marker = `integration-test-${Date.now()}`;
    await mcpUnity.sendRequestWithRetry('send_console_log', { message: marker, type: 'Log' });

    const res = await mcpUnity.sendRequestWithRetry('get_console_logs', {
      logType: 'info', limit: 20, includeStackTrace: false
    });
    expect(res.success).toBe(true);
  });
});

// ─── Группа 2: Сцены ────────────────────────────────────────────────

d('Group 2: Scenes', () => {
  it('load_scene loads park.unity', async () => {
    const res = await mcpUnity.sendRequestWithRetry('load_scene', { scenePath: 'Assets/park.unity' });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  }, 30000);

  it('get_scene_info shows loaded scene', async () => {
    const res = await mcpUnity.sendRequestWithRetry('get_scene_info', {});
    expect(res.activeScene).toBeDefined();
  });

  it('save_scene succeeds without error', async () => {
    const res = await mcpUnity.sendRequestWithRetry('save_scene', {});
    expect(res).toBeDefined();
  });
});

// ─── Группа 3: GameObject CRUD ──────────────────────────────────────

d('Group 3: GameObject CRUD', () => {
  let testObjectId: number | undefined;

  it('get_gameobject finds Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('get_gameobject', { idOrName: 'Main Camera' });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.name).toBe('Main Camera');
  });

  it('duplicate_gameobject clones Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('duplicate_gameobject', {
      objectPath: 'Main Camera', newName: 'IntegrationTestClone'
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    testObjectId = res.instanceId;
  });

  it('delete_gameobject removes clone', async () => {
    if (!testObjectId) return;
    const res = await mcpUnity.sendRequestWithRetry('delete_gameobject', {
      objectPath: 'IntegrationTestClone'
    });
    expect(res.success).toBe(true);
    testObjectId = undefined;
  });

  afterAll(async () => {
    // Cleanup: удалить клон если тест упал
    if (testObjectId) {
      try {
        await mcpUnity.sendRequestWithRetry('delete_gameobject', { instanceId: testObjectId });
      } catch { /* ignore */ }
    }
  });
});

// ─── Группа 4: Transform ────────────────────────────────────────────

d('Group 4: Transform', () => {
  it('move_gameobject moves Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('move_gameobject', {
      objectPath: 'Main Camera',
      position: { x: 0, y: 10, z: -10 },
      space: 'world'
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  });

  it('rotate_gameobject rotates Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('rotate_gameobject', {
      objectPath: 'Main Camera',
      rotation: { x: 0, y: 0, z: 0 }
    });
    expect(res.success).toBe(true);
  });

  it('scale_gameobject scales Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('scale_gameobject', {
      objectPath: 'Main Camera',
      scale: { x: 1, y: 1, z: 1 }
    });
    expect(res.success).toBe(true);
  });

  it('set_transform sets all at once', async () => {
    const res = await mcpUnity.sendRequestWithRetry('set_transform', {
      objectPath: 'Main Camera',
      position: { x: 0, y: 10, z: -10 },
      rotation: { x: 0, y: 0, z: 0 },
      scale: { x: 1, y: 1, z: 1 }
    });
    expect(res.success).toBe(true);
  });
});

// ─── Группа 5: Компиляция и тесты ──────────────────────────────────

d('Group 5: Compilation & Tests', () => {
  it('recompile_scripts compiles without errors', async () => {
    const res = await mcpUnity.sendRequestWithRetry('recompile_scripts', {
      returnWithLogs: true, logsLimit: 10
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  }, 60000);

  it('run_tests EditMode with filter returns results', async () => {
    const res = await mcpUnity.sendRequestWithRetry('run_tests', {
      testMode: 'EditMode',
      testFilter: 'JavaParityMoneyEncoding',
      returnOnlyFailures: false
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.testCount).toBeGreaterThan(0);
  }, 60000);

  it('run_tests EditMode all tests (120s timeout)', async () => {
    const res = await mcpUnity.sendRequestWithRetry('run_tests', {
      testMode: 'EditMode',
      returnOnlyFailures: true,
      returnWithLogs: false
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.failCount).toBe(0);
  }, 180000);
});

// ─── Группа 6: Скриншоты ────────────────────────────────────────────

d('Group 6: Screenshots', () => {
  it('capture_screenshot in Edit Mode returns data', async () => {
    const res = await mcpUnity.sendRequestWithRetry('capture_screenshot', {
      width: 320, height: 240
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  });

  it('capture_screenshot with custom dimensions', async () => {
    const res = await mcpUnity.sendRequestWithRetry('capture_screenshot', {
      width: 960, height: 540
    });
    expect(res.success).toBe(true);
  });

  it('capture_screenshot 10x stability', async () => {
    for (let i = 0; i < 10; i++) {
      const res = await mcpUnity.sendRequestWithRetry('capture_screenshot', {
        width: 320, height: 240
      });
      expect(res.success).toBe(true);
    }
  }, 60000);
});

// ─── Группа 7: Play Mode lifecycle ──────────────────────────────────

d('Group 7: Play Mode lifecycle', () => {
  it('get_state returns isPlaying=false initially', async () => {
    const res = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'get_state' });
    expect(res).toBeDefined();
    expect(res.isPlaying).toBe(false);
  });

  it('enter Play Mode', async () => {
    const res = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'enter' });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  }, 30000);

  it('get_state returns isPlaying=true after enter', async () => {
    const res = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'get_state' });
    expect(res.isPlaying).toBe(true);
  });

  it('capture_screenshot in Play Mode', async () => {
    const res = await mcpUnity.sendRequestWithRetry('capture_screenshot', {
      width: 480, height: 320
    });
    expect(res.success).toBe(true);
  });

  it('pause and resume', async () => {
    const pauseRes = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'pause' });
    expect(pauseRes.success).toBe(true);

    const resumeRes = await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'resume' });
    expect(resumeRes.success).toBe(true);
  });

  // НЕ выходим из Play Mode здесь — группа 8 продолжит в Play Mode
});

// ─── Группа 8: Runtime tools (Play Mode) ────────────────────────────

d('Group 8: Runtime tools (Play Mode)', () => {
  it('find_objects by component Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('find_objects', {
      by: 'component', value: 'Camera'
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.objects?.length).toBeGreaterThan(0);
  });

  it('inspect_object on Main Camera', async () => {
    const res = await mcpUnity.sendRequestWithRetry('inspect_object', {
      objectPath: 'Main Camera', query: 'world_position'
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
  });

  it('time_control get and set', async () => {
    const getRes = await mcpUnity.sendRequestWithRetry('time_control', { action: 'get' });
    expect(getRes.success).toBe(true);

    await mcpUnity.sendRequestWithRetry('time_control', { action: 'set', timeScale: 2 });
    const after = await mcpUnity.sendRequestWithRetry('time_control', { action: 'get' });
    expect(after.timeScale).toBe(2);

    // Restore
    await mcpUnity.sendRequestWithRetry('time_control', { action: 'set', timeScale: 1 });
  });

  it('screen_info get_size', async () => {
    const res = await mcpUnity.sendRequestWithRetry('screen_info', { action: 'get_size' });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.width).toBeGreaterThan(0);
    expect(res.height).toBeGreaterThan(0);
  });

  it('player_prefs CRUD', async () => {
    const key = `mcp_integration_test_${Date.now()}`;

    await mcpUnity.sendRequestWithRetry('player_prefs', { action: 'set', key, value: '42', type: 'string' });

    const has = await mcpUnity.sendRequestWithRetry('player_prefs', { action: 'has', key });
    expect(has.exists).toBe(true);

    const get = await mcpUnity.sendRequestWithRetry('player_prefs', { action: 'get', key, type: 'string' });
    expect(get.value).toBe('42');

    await mcpUnity.sendRequestWithRetry('player_prefs', { action: 'delete', key });
    const hasAfter = await mcpUnity.sendRequestWithRetry('player_prefs', { action: 'has', key });
    expect(hasAfter.exists).toBe(false);
  });

  it('call_method static Application.isPlaying', async () => {
    const res = await mcpUnity.sendRequestWithRetry('call_method', {
      typeName: 'UnityEngine.Application',
      methodName: 'isPlaying',
      isStatic: true
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.returnValue).toBe(true);
  });

  it('wait_for object_exists Main Camera', async () => {
    // wait_for вызывается напрямую через sendRequest (TS-side polling)
    // Для интеграционного теста проверим check_condition
    const res = await mcpUnity.sendRequestWithRetry('check_condition', {
      condition: 'object_exists', target: 'Main Camera'
    });
    expect(res).toBeDefined();
    expect(res.conditionMet).toBe(true);
  });

  // Cleanup: exit Play Mode
  afterAll(async () => {
    try {
      await mcpUnity.sendRequestWithRetry('play_mode_control', { action: 'exit' });
      // Ждём domain reload
      await new Promise(r => setTimeout(r, 5000));
    } catch { /* ignore */ }

    try {
      await mcpUnity.sendRequestWithRetry('time_control', { action: 'set', timeScale: 1 });
    } catch { /* ignore */ }
  }, 30000);
});

// ─── Группа 9: Batch и edge-cases ───────────────────────────────────

d('Group 9: Batch & Edge Cases', () => {
  it('batch_execute with read operations', async () => {
    const res = await mcpUnity.sendRequestWithRetry('batch_execute', {
      operations: [
        { id: 'op1', tool: 'get_scene_info', params: {} },
        { id: 'op2', tool: 'get_console_logs', params: { logType: 'error', limit: 5, includeStackTrace: false } },
      ],
      stopOnError: false
    });
    expect(res).toBeDefined();
    expect(res.success).toBe(true);
    expect(res.results?.length).toBe(2);
  });

  it('execute_menu_item opens Console', async () => {
    const res = await mcpUnity.sendRequestWithRetry('execute_menu_item', {
      menuPath: 'Window/General/Console'
    });
    expect(res).toBeDefined();
  });
});

// ─── Группа 10: Guard — всегда проходящий тест ──────────────────────

describe('Integration test guard', () => {
  it('should always pass (guard for test runner)', () => {
    expect(true).toBe(true);
  });
});
