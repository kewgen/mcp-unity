import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'time_control';
const toolDescription = 'Gets or sets Unity Time.timeScale. Use to speed up, slow down, or pause game time.';

const paramsSchema = z.object({
  action: z.enum(['get', 'set']).describe('Action: get current time info or set timeScale'),
  timeScale: z.number().min(0).max(100).optional().describe('Time scale value (0 = paused, 1 = normal, >1 = fast)')
});

export function registerTimeControlTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await mcpUnity.sendRequest({ method: toolName, params });
        if (!response.success) {
          throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to control time');
        }
        let text = response.message;
        if (response.time !== undefined) {
          text += `\nTime: ${response.time}s | DeltaTime: ${response.deltaTime}s | Frame: ${response.frameCount}`;
        }
        logger.info(`Tool execution successful: ${toolName}`);
        return { content: [{ type: 'text', text }], data: response };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
