import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'call_method';
const toolDescription = 'Calls a method or gets/sets a property on a component (instance) or type (static) via reflection. Powerful runtime access tool.';

const paramsSchema = z.object({
  objectPath: z.string().optional().describe('GameObject path for instance calls'),
  instanceId: z.number().int().optional().describe('Instance ID for instance calls'),
  typeName: z.string().optional().describe('Fully qualified type name for static calls (e.g. "UnityEngine.Application")'),
  component: z.string().optional().describe('Component type name on the GameObject'),
  methodName: z.string().describe('Method or property name to call/access'),
  parameters: z.array(z.any()).optional().describe('Method parameters or property value to set'),
  isStatic: z.boolean().optional().describe('Whether this is a static call (default: false)')
});

export function registerCallMethodTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
            response.message || 'Failed to call method'
          );
        }

        let text = response.message;
        if (response.result !== undefined && response.result !== null) {
          text += `\nResult: ${JSON.stringify(response.result, null, 2)}`;
        }

        logger.info(`Tool execution successful: ${toolName}`);
        return {
          content: [{ type: 'text', text }],
          data: { result: response.result }
        };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
