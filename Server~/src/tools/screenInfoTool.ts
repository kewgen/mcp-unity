import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'screen_info';
const toolDescription = 'Gets screen size or converts coordinates between screen space and world space.';

const paramsSchema = z.object({
  action: z.enum(['get_size', 'screen_to_world', 'world_to_screen']).describe('Action to perform'),
  x: z.number().optional().describe('X coordinate'),
  y: z.number().optional().describe('Y coordinate'),
  z: z.number().optional().describe('Z coordinate (depth for screen_to_world, world Z for world_to_screen)'),
  camera: z.string().optional().describe('Camera GameObject path (default: Main Camera)')
});

export function registerScreenInfoTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await mcpUnity.sendRequest({ method: toolName, params });
        if (!response.success) {
          throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to get screen info');
        }
        logger.info(`Tool execution successful: ${toolName}`);
        return { content: [{ type: 'text', text: response.message }], data: response };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
