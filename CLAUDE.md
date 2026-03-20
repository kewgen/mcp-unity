# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MCP Unity exposes Unity Editor capabilities to MCP-enabled clients (Cursor, Windsurf, Claude Code, Codex CLI, GitHub Copilot) through a two-tier architecture:

- **Unity Editor (C#)**: WebSocket server inside Unity that executes tools/resources
- **Node.js Server (TypeScript)**: MCP stdio server that bridges AI clients to Unity via WebSocket

**Data flow**: MCP Client ⇄ (stdio) ⇄ Node Server (`Server~/src/index.ts`) ⇄ (WebSocket) ⇄ Unity Editor (`Editor/UnityBridge/McpUnityServer.cs`)

## Build & Development Commands

### Node.js Server (`Server~/`)
```bash
npm install          # Install dependencies
npm run build        # Compile TypeScript to build/
npm run watch        # Watch mode compilation
npm start            # Run server (node build/index.js)
npm test             # Run Jest tests (uses --experimental-vm-modules)
npm run test:watch   # Watch mode testing
npm run inspector    # Launch MCP Inspector for debugging
```

### Unity Side
- Build/Test via Unity Editor
- **Tools > MCP Unity > Server Window** for configuration
- **Window > General > Test Runner** for EditMode tests

## Key Directories

```
Editor/                       # Unity Editor package (C#)
├── Tools/                    # MCP tools (inherit McpToolBase)
├── Resources/                # MCP resources (inherit McpResourceBase)
├── Services/                 # TestRunnerService, ConsoleLogsService
├── UnityBridge/              # WebSocket server + message routing
│   ├── McpUnityServer.cs     # Singleton managing server lifecycle
│   └── McpUnitySocketHandler.cs  # WebSocket handler
└── Utils/                    # Logging, config, workspace helpers

Server~/                      # Node.js MCP server (TypeScript/ESM)
├── src/index.ts              # Entry point - registers tools/resources
├── src/tools/                # MCP tool definitions (zod + handler)
├── src/resources/            # MCP resource definitions
└── src/unity/mcpUnity.ts     # WebSocket client connecting to Unity
```

## Key Invariants

- **WebSocket endpoint**: `ws://localhost:8090/McpUnity` (configurable)
- **Config file**: `ProjectSettings/McpUnitySettings.json`
- **Tool/resource names must match exactly** between Node and Unity (use `lower_snake_case`)
- **Execution thread**: All tool execution runs on Unity main thread via EditorCoroutineUtility

## Adding a New Tool

### 1. Unity Side (C#)
Create `Editor/Tools/YourTool.cs`:
```csharp
public class YourTool : McpToolBase {
    public override string Name => "your_tool";  // Must match Node side
    public override JObject Execute(JObject parameters) {
        // Implementation
    }
}
```
Register in `McpUnityServer.cs` → `RegisterTools()`.

### 2. Node Side (TypeScript)
Create `Server~/src/tools/yourTool.ts`:
```typescript
export function registerYourTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  server.tool("your_tool", "Description", paramsSchema.shape, async (params) => {
    return await mcpUnity.sendRequest({ method: "your_tool", params });
  });
}
```
Register in `Server~/src/index.ts`.

### 3. Build
```bash
cd Server~ && npm run build
```

## Adding a New Resource

Same pattern as tools:
- Unity: inherit `McpResourceBase`, implement `Fetch()`, register in `RegisterResources()`
- Node: register with `server.resource()`, forward via `mcpUnity.sendRequest()`

## Configuration

**McpUnitySettings.json** fields:
- `Port` (default 8090): Unity WebSocket server port
- `RequestTimeoutSeconds` (default 10): Node request timeout
- `AllowRemoteConnections` (default false): Bind to 0.0.0.0 when true

**Environment variables** (Node side):
- `UNITY_HOST`: Override Unity host (for remote connections)
- `MCP_CLIENT_NAME`: Client name shown in Unity logs (fallback if MCP SDK doesn't provide one). Set in `.mcp.json` → `env` to identify which agent/chat is connected.
- `LOGGING=true`: Enable console logging
- `LOGGING_FILE=true`: Write logs to log.txt

## Debugging

- **MCP Inspector**: `cd Server~ && npm run inspector`
- **Unity logs**: Controlled by `EnableInfoLogs` in settings
- **Node logs**: Set `LOGGING=true` environment variable

## AltTester-Replacement Tools (Runtime Testing)

12 additional tools that replace AltTester (AltUnityTester) functionality for automated UI/integration testing. The WebSocket server stays active during Play Mode to enable runtime interaction.

### Play Mode & Lifecycle
| Tool | Description |
|------|-------------|
| `play_mode_control` | Enter/exit Play Mode, pause/resume, get state. Async — waits for transition. |
| `wait_for` | Polls Unity until a condition is met (object exists/gone, scene loaded, property equals). Polling logic runs on Node.js side via `check_condition`. |
| `check_condition` | Internal helper for `wait_for` — checks a single condition and returns `conditionMet: bool`. |

### Object Discovery & Inspection
| Tool | Description |
|------|-------------|
| `find_objects` | Find GameObjects by tag, layer, component type, text content, partial name, or screen coordinates (raycast). |
| `inspect_object` | Get world/screen position, bounds, text content, parent info for a GameObject. |

### Input Simulation (Play Mode only)
| Tool | Description |
|------|-------------|
| `simulate_input` | Click/tap (by path or coordinates), swipe, key press/release, mouse move, scroll, pointer events. Uses `ExecuteEvents` for UI interaction. |
| `set_text` | Set text on `Text`, `TMP_Text`, `InputField`, `TMP_InputField`. Optional `submit` triggers `onEndEdit`. |

### Runtime Access
| Tool | Description |
|------|-------------|
| `call_method` | Call instance/static methods, get/set properties via reflection. Works on any component or type. |
| `player_prefs` | Get/set/delete/has/delete_all PlayerPrefs. |
| `time_control` | Get/set `Time.timeScale`, read frame count and elapsed time. |
| `screen_info` | Get screen size, convert screen↔world coordinates. |
| `capture_screenshot` | Render camera to PNG, return as base64. Works in both Edit and Play mode. |

### Connection Resilience & Auto-Retry

All runtime tools use `sendWithRetry()` (`Server~/src/tools/toolHelper.ts`) which automatically handles WebSocket disconnections during Play Mode transitions:

- **Auto-retry**: Up to 5 attempts, 2s interval on CONNECTION/TIMEOUT errors
- **`play_mode_control enter/exit`**: Special handling — sends command, expects connection loss during domain reload, polls `get_state` until target state is reached (up to 30s)
- **Error messages**: When all retries are exhausted, agents get clear messages like `"failed after 6 attempts. Unity may be in domain reload"` instead of raw connection errors

**When does disconnection happen?**
- `play_mode_control enter` → Unity domain reload (~5-10s) → WebSocket reconnects → server auto-restarts via `[DidReloadScripts]`
- `play_mode_control exit` → same domain reload cycle
- `recompile_scripts` → assembly reload → brief disconnection

**Agent best practices:**
- After `play_mode_control enter/exit`, the tool itself waits for reconnection — no manual sleep needed
- If calling tools immediately after a domain reload trigger, `sendWithRetry` handles the wait automatically
- For batch operations after Play Mode entry, use `batch_execute` — it runs on Unity side, no per-tool reconnection overhead

### Multi-Agent / Parallel Usage

The MCP Unity server supports multiple concurrent MCP client connections (multi-client WebSocket). Important considerations for parallel agent workflows:

**Client identification:**
Each MCP client is identified in Unity logs by name. Set `MCP_CLIENT_NAME` in `.mcp.json` env to distinguish agents:
```json
{
  "mcpServers": {
    "mcp-unity": {
      "env": { "MCP_CLIENT_NAME": "Agent-Render" }
    }
  }
}
```
Priority: MCP SDK `clientInfo.name` → `MCP_CLIENT_NAME` env var → `"Unknown MCP Client"`.
When running multiple agents, give each a unique name to trace which agent issued which command in Unity console.

**What works in parallel:**
- Multiple agents can read scene state simultaneously (`get_scene_info`, `find_objects`, `inspect_object`, `get_gameobject`, etc.)
- `capture_screenshot` from different agents — each gets independent render
- `call_method` read-only calls (getting properties, calling pure methods)
- `check_condition` / `wait_for` polling — each agent polls independently

**What requires coordination:**
- **Play Mode transitions** — only one agent should call `play_mode_control enter/exit`. Domain reload affects ALL connected clients. If agent A enters Play Mode, agent B's connection also drops and reconnects.
- **Scene modifications** — `simulate_input`, `set_text`, `update_component`, `move_gameobject` etc. modify shared Unity state. Concurrent writes can conflict.
- **`time_control set`** — global state, last write wins
- **`player_prefs set/delete`** — shared storage, last write wins

**Recommended parallel patterns:**
1. **One writer, many readers**: One agent drives Play Mode and input simulation, others observe via `find_objects`, `inspect_object`, `capture_screenshot`
2. **Sequential phases**: Agent A sets up scene → Agent B runs tests → Agent C captures results
3. **Independent scenes**: Use `load_scene` additively for isolated testing (if scene design allows)

**Domain reload impact on all clients:**
When ANY client triggers domain reload (Play Mode enter/exit, `recompile_scripts`), ALL connected MCP clients experience a WebSocket disconnect. Each client's `sendWithRetry` handles reconnection independently. The Unity server re-registers all tools after reload via `[DidReloadScripts]`.

### Play Mode Architecture Notes
- Server no longer stops on `ExitingEditMode` — stays active through Play Mode transitions
- Domain reload during Play Mode entry will temporarily disconnect; server auto-restarts via `[DidReloadScripts]` and `EnteredPlayMode` handler
- `simulate_input` validates `EditorApplication.isPlaying` and rejects calls in Edit Mode
- `capture_screenshot` uses `Camera.Render()` in Edit Mode, same approach in Play Mode (avoids `ScreenCapture` timing issues)
- TMP components accessed via reflection to avoid hard dependency on TextMeshPro package
- All runtime tools import `sendWithRetry` from `toolHelper.ts` for connection resilience

## Common Pitfalls

- **Name mismatch**: Node tool/resource name must equal Unity `Name` exactly
- **Long main-thread work**: Synchronous `Execute()` blocks Unity; use `IsAsync = true` with `ExecuteAsync()` for long operations
- **Unity domain reload**: Server stops during script reloads; avoid persistent in-memory state
- **Port conflicts**: Default is 8090; check if another process is using it
- **Multiplayer Play Mode**: Clone instances auto-skip server startup; only main editor hosts MCP

## Code Conventions

- **C# classes**: PascalCase (e.g., `CreateSceneTool`)
- **TypeScript functions**: camelCase (e.g., `registerCreateSceneTool`)
- **Tool/resource names**: lower_snake_case (e.g., `create_scene`)
- **Commits**: Conventional format - `feat(scope):`, `fix(scope):`, `chore:`
- **Undo support**: Use `Undo.RecordObject()` for scene modifications

## Requirements

- Unity 2022.3+ (Unity 6 recommended)
- Node.js 18+
- npm 9+
