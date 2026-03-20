import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'player_prefs';
const toolDescription = 'Manages Unity PlayerPrefs: get, set, delete, check existence, or delete all.';

const paramsSchema = z.object({
  action: z.enum(['get', 'set', 'delete', 'has', 'delete_all']).describe('Action to perform'),
  key: z.string().optional().describe('PlayerPrefs key'),
  value: z.any().optional().describe('Value to set'),
  type: z.enum(['int', 'float', 'string']).optional().describe('Value type (default: string)')
});

export function registerPlayerPrefsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await mcpUnity.sendRequest({ method: toolName, params });
        if (!response.success) {
          throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to access PlayerPrefs');
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
