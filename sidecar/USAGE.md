# UnityExplorer MCP Sidecar Usage

This sidecar lets MCP clients inspect a Unity game running UnityExplorer MCP on the same machine.

Process layout:

```text
MCP client -> Node sidecar over stdio -> 127.0.0.1 UnityExplorer bridge -> Unity game
```

## Requirements

- Node.js 18 or newer.
- A game running a UnityExplorer build that includes the MCP bridge.
- The MCP client and Unity game must run on the same machine for v1.

When UnityExplorer starts, it writes a connection file with the local bridge URL and session token:

```text
<os temp>/unityexplorer-mcp/connection.json
```

The sidecar reads that file automatically. You can override it with:

```bash
UE_MCP_CONNECTION_FILE=/path/to/connection.json
UE_MCP_BRIDGE_URL=http://127.0.0.1:8765/
UE_MCP_BRIDGE_TOKEN=token
```

## Run From This Release Folder

From the extracted `UnityExplorer.MCP.Sidecar` folder:

```bash
npm install --omit=dev
node dist/index.js
```

The sidecar speaks both newline-delimited JSON-RPC and `Content-Length` framed JSON-RPC on stdio. It responds using the same framing style as the client. stdout is reserved for MCP protocol messages; diagnostics go to stderr.

## Codex Config

Use the built local sidecar path:

```toml
[mcp_servers.unityexplorer]
enabled = true
command = "node"
args = ["F:\\git2\\UnityExplorerMCP\\sidecar\\dist\\index.js"]
```

For an extracted release zip, point `args` at that release folder instead:

```toml
[mcp_servers.unityexplorer]
enabled = true
command = "node"
args = ["C:\\path\\to\\UnityExplorer.MCP.Sidecar\\dist\\index.js"]
```

Restart Codex after editing the MCP config.

## Claude Desktop Example

```json
{
  "mcpServers": {
    "unityexplorer": {
      "command": "node",
      "args": ["C:\\path\\to\\UnityExplorer.MCP.Sidecar\\dist\\index.js"]
    }
  }
}
```

On Linux:

```json
{
  "mcpServers": {
    "unityexplorer": {
      "command": "node",
      "args": ["/home/you/UnityExplorer.MCP.Sidecar/dist/index.js"]
    }
  }
}
```

## npm Package Install

If using the `.tgz` artifact from `Release`:

```bash
npm install -g ./unity-explorer-mcp-0.1.0.tgz
```

Then configure your MCP client with:

```json
{
  "command": "unity-explorer-mcp",
  "args": []
}
```

## Useful First Tool Calls

Check connection:

```json
ue_status({})
```

List scenes:

```json
ue_list_scenes({})
```

Guided object search:

```json
ue_search_objects({
  "scene": "PreGen",
  "nameOrPathContains": "RunSettings",
  "limit": 20
})
```

Search visible UI text under a known root:

```json
ue_list_hierarchy({
  "rootHandle": "obj_...",
  "depth": 5,
  "textContains": "Loot density",
  "limit": 25
})
```

## Notes For Agents

- Do not request an unfiltered whole-scene dump. Use scene, path/name, component, or visible text filters.
- Treat handles as session-local and ephemeral.
- Writes and invokes are preview/apply operations. Do not call apply tools unless the user explicitly approves the previewed action.
- Prefer stable UI discovery by visible labels and component layout, not clone index alone.
