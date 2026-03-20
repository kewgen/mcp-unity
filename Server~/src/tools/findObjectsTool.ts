import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'find_objects';
const toolDescription = 'Finds GameObjects by tag, layer, component type, text content, partial name match, or screen coordinates. Returns matching objects with their paths, components, and properties.';

const paramsSchema = z.object({
  by: z
    .enum(['tag', 'layer', 'component', 'text', 'name_contains', 'coordinates'])
    .describe('Search criteria type'),
  value: z
    .string()
    .optional()
    .describe('Search value (tag name, layer name/number, component type, text content, or partial name)'),
  x: z.number().optional().describe('Screen X coordinate (for coordinates search)'),
  y: z.number().optional().describe('Screen Y coordinate (for coordinates search)'),
  maxResults: z
    .number()
    .int()
    .min(1)
    .max(200)
    .optional()
    .describe('Maximum number of results to return (default: 50)'),
  activeOnly: z
    .boolean()
    .optional()
    .describe('Only search active GameObjects (default: true)')
});

export function registerFindObjectsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
            response.message || 'Failed to find objects'
          );
        }

        let text = `${response.message}\n\n`;
        if (response.objects && response.objects.length > 0) {
          for (const obj of response.objects) {
            text += `- ${obj.path} [${obj.instanceId}] tag:${obj.tag} layer:${obj.layer} active:${obj.active}\n`;
            text += `  Components: ${obj.components.join(', ')}\n`;
          }
        }

        logger.info(`Tool execution successful: ${toolName}`);
        return {
          content: [{ type: 'text', text }],
          data: {
            totalCount: response.totalCount,
            objects: response.objects
          }
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
