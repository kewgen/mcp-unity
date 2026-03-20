import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import { sendWithRetry } from './toolHelper.js';

const toolName = 'inspect_object';
const toolDescription = 'Inspects a GameObject for screen position, world position, bounds, text content, and parent info. Use query parameter to select specific data.';

const paramsSchema = z.object({
  objectPath: z.string().optional().describe('Hierarchical path to the GameObject (e.g. "/Canvas/Panel/Button")'),
  instanceId: z.number().int().optional().describe('Instance ID of the GameObject'),
  query: z
    .enum(['screen_position', 'world_position', 'bounds', 'text', 'parent', 'all'])
    .optional()
    .describe('What data to retrieve (default: all)')
});

export function registerInspectObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
            response.message || 'Failed to inspect object'
          );
        }

        logger.info(`Tool execution successful: ${toolName}`);
        return {
          content: [{ type: 'text', text: JSON.stringify(response, null, 2) }]
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
