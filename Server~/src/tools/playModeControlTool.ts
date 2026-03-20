import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'play_mode_control';
const toolDescription = 'Controls Unity Play Mode: enter, exit, pause, resume, or get current state. Use this to start/stop game execution for testing.';

const paramsSchema = z.object({
  action: z
    .enum(['enter', 'exit', 'pause', 'resume', 'get_state'])
    .describe('Action to perform: enter/exit Play Mode, pause/resume, or get_state')
});

export function registerPlayModeControlTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
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
        return {
          content: [{ type: 'text', text }]
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
