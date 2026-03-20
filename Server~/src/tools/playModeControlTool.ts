import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'play_mode_control';
const toolDescription = 'Controls Unity Play Mode: enter, exit, pause, resume, or get current state. Enter/exit triggers Unity domain reload (~5-10s), this tool automatically waits for reconnection.';

const paramsSchema = z.object({
  action: z
    .enum(['enter', 'exit', 'pause', 'resume', 'get_state'])
    .describe('Action to perform: enter/exit Play Mode, pause/resume, or get_state')
});

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Waits for Unity WebSocket to reconnect after domain reload,
 * then polls play_mode_control get_state until the expected state is reached.
 */
async function waitForStateAfterReload(
  mcpUnity: McpUnity,
  expectedPlaying: boolean,
  logger: Logger,
  timeoutMs: number = 30000
): Promise<any> {
  const startTime = Date.now();
  const pollInterval = 1000;

  // Wait a bit for domain reload to start (Unity needs time to begin the transition)
  await sleep(2000);

  while (Date.now() - startTime < timeoutMs) {
    try {
      // Try to get current state — will fail if WebSocket is still disconnected
      const response = await mcpUnity.sendRequest(
        { method: toolName, params: { action: 'get_state' } },
        { timeout: 3000, queueIfDisconnected: false }
      );

      if (response.success && response.isPlaying === expectedPlaying) {
        return response;
      }

      // Connected but state hasn't changed yet — keep polling
      logger.debug(`Play Mode state: isPlaying=${response.isPlaying}, waiting for ${expectedPlaying}...`);
    } catch (error) {
      // Connection not yet restored — expected during domain reload
      logger.debug(`Waiting for Unity reconnection... (${Math.round((Date.now() - startTime) / 1000)}s)`);
    }

    await sleep(pollInterval);
  }

  throw new McpUnityError(
    ErrorType.TIMEOUT,
    `Timeout (${timeoutMs / 1000}s) waiting for Play Mode ${expectedPlaying ? 'enter' : 'exit'} after domain reload`
  );
}

export function registerPlayModeControlTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      const { action } = params;

      try {
        logger.info(`Executing tool: ${toolName} action=${action}`);

        // For enter/exit: send command, expect possible connection loss, wait for reconnection
        if (action === 'enter' || action === 'exit') {
          const expectedPlaying = action === 'enter';

          // First check current state — maybe already in desired state
          try {
            const currentState = await mcpUnity.sendRequest(
              { method: toolName, params: { action: 'get_state' } },
              { timeout: 3000 }
            );
            if (currentState.success && currentState.isPlaying === expectedPlaying) {
              const text = `Already in ${expectedPlaying ? 'Play' : 'Edit'} Mode`;
              return { content: [{ type: 'text', text }] };
            }
          } catch {
            // Can't check state — proceed anyway
          }

          // Send the enter/exit command — may fail due to domain reload, that's OK
          try {
            await mcpUnity.sendRequest(
              { method: toolName, params: { action } },
              { timeout: 5000, queueIfDisconnected: false }
            );
          } catch (error) {
            // Expected: domain reload kills the WebSocket connection
            logger.info(`Play Mode ${action}: connection interrupted (domain reload), waiting for reconnection...`);
          }

          // Wait for Unity to reconnect and reach expected state
          const response = await waitForStateAfterReload(mcpUnity, expectedPlaying, logger);

          const text = `${expectedPlaying ? 'Entered Play Mode' : 'Exited to Edit Mode'} successfully\nPlay Mode: ${response.isPlaying ? 'Playing' : 'Stopped'}${response.isPaused ? ' (Paused)' : ''}`;
          logger.info(`Tool execution successful: ${toolName} action=${action}`);
          return { content: [{ type: 'text', text }] };
        }

        // For pause/resume/get_state: simple direct call
        const response = await mcpUnity.sendRequest({
          method: toolName,
          params
        });

        if (!response.success) {
          throw new McpUnityError(
            ErrorType.TOOL_EXECUTION,
            response.message || 'Failed to control Play Mode'
          );
        }

        let text = `Play Mode: ${response.isPlaying ? 'Playing' : 'Stopped'}`;
        if (response.isPaused) text += ' (Paused)';
        if (response.isCompiling) text += ' [Compiling]';
        if (response.message) text = `${response.message}\n${text}`;

        logger.info(`Tool execution successful: ${toolName}`);
        return { content: [{ type: 'text', text }] };

      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
