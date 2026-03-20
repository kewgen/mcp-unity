import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import { sendWithRetry } from './toolHelper.js';

const toolName = 'simulate_input';
const toolDescription = 'Simulates user input in Play Mode: click/tap on objects or coordinates, swipe, key press, mouse move, scroll, pointer events. Requires Play Mode.';

const paramsSchema = z.object({
  action: z
    .enum(['click', 'tap', 'swipe', 'key_down', 'key_up', 'key_press', 'mouse_move', 'scroll', 'pointer_down', 'pointer_up', 'reset'])
    .describe('Type of input to simulate'),
  objectPath: z.string().optional().describe('GameObject path for click/pointer events'),
  x: z.number().optional().describe('Screen X coordinate'),
  y: z.number().optional().describe('Screen Y coordinate'),
  startX: z.number().optional().describe('Swipe start X'),
  startY: z.number().optional().describe('Swipe start Y'),
  endX: z.number().optional().describe('Swipe end X'),
  endY: z.number().optional().describe('Swipe end Y'),
  duration: z.number().optional().describe('Swipe duration in seconds (default: 0.5)'),
  count: z.number().int().min(1).max(10).optional().describe('Click/tap count (default: 1)'),
  keyCode: z.string().optional().describe('Key code for key events (e.g., "Space", "Return", "A")'),
  deltaX: z.number().optional().describe('Scroll delta X'),
  deltaY: z.number().optional().describe('Scroll delta Y')
});

export function registerSimulateInputTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await sendWithRetry(mcpUnity, toolName, params, logger);

        if (!response.success) {
          throw new McpUnityError(
            ErrorType.TOOL_EXECUTION,
            response.message || 'Failed to simulate input'
          );
        }

        logger.info(`Tool execution successful: ${toolName}`);
        return {
          content: [{ type: 'text', text: response.message }]
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
