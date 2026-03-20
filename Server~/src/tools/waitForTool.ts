import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'wait_for';
const toolDescription = 'Waits for a condition to be met by polling Unity. Conditions: object_exists, object_not_exists, scene_loaded, property_equals.';

const paramsSchema = z.object({
  condition: z
    .enum(['object_exists', 'object_not_exists', 'scene_loaded', 'property_equals'])
    .describe('Condition to wait for'),
  target: z.string().describe('Target: GameObject path or scene name'),
  component: z.string().optional().describe('Component name (for property_equals)'),
  property: z.string().optional().describe('Property name (for property_equals)'),
  value: z.any().optional().describe('Expected value (for property_equals)'),
  timeout: z.number().min(100).max(60000).optional().describe('Timeout in milliseconds (default: 10000)'),
  interval: z.number().min(50).max(5000).optional().describe('Polling interval in milliseconds (default: 200)')
});

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

export function registerWaitForTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>): Promise<CallToolResult> => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);

        const timeout = params.timeout ?? 10000;
        const interval = params.interval ?? 200;
        const startTime = Date.now();

        while (true) {
          const elapsed = Date.now() - startTime;
          if (elapsed >= timeout) {
            throw new McpUnityError(
              ErrorType.TOOL_EXECUTION,
              `Timeout (${timeout}ms) waiting for condition: ${params.condition} on "${params.target}"`
            );
          }

          // Poll Unity via check_condition tool
          const response = await mcpUnity.sendRequest({
            method: 'check_condition',
            params: {
              condition: params.condition,
              target: params.target,
              component: params.component,
              property: params.property,
              value: params.value
            }
          });

          if (response.success && response.conditionMet) {
            logger.info(`Tool execution successful: ${toolName} (${elapsed}ms)`);
            return {
              content: [{
                type: 'text',
                text: `Condition met after ${elapsed}ms: ${response.details || response.message}`
              }]
            };
          }

          await sleep(interval);
        }
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
