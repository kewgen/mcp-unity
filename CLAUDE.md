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

### Play Mode Architecture Notes
- Server no longer stops on `ExitingEditMode` — stays active through Play Mode transitions
- Domain reload during Play Mode entry will temporarily disconnect; server auto-restarts via `[DidReloadScripts]` and `EnteredPlayMode` handler
- `simulate_input` validates `EditorApplication.isPlaying` and rejects calls in Edit Mode
- `capture_screenshot` uses `Camera.Render()` in Edit Mode, same approach in Play Mode (avoids `ScreenCapture` timing issues)
- TMP components accessed via reflection to avoid hard dependency on TextMeshPro package

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
