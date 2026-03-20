import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'set_text';
const toolDescription = 'Sets text on UI elements (Text, TMP_Text, InputField, TMP_InputField). Can optionally submit the input.';

const paramsSchema = z.object({
  objectPath: z.string().optional().describe('GameObject path'),
  instanceId: z.number().int().optional().describe('Instance ID'),
  text: z.string().describe('Text to set'),
  submit: z.boolean().optional().describe('Trigger onEndEdit/onSubmit events (default: false)')
});

export function registerSetTextTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const response = await mcpUnity.sendRequest({ method: toolName, params });
        if (!response.success) {
          throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to set text');
        }
        logger.info(`Tool execution successful: ${toolName}`);
        return { content: [{ type: 'text', text: response.message }] };
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
