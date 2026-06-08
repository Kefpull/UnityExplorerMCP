# UnityExplorer MCP Design

## Summary

UnityExplorer MCP is split into two processes:

```text
MCP client -> TypeScript/Node sidecar over stdio -> localhost JSON bridge -> UnityExplorer/game
```

The Unity side is intentionally thin. It owns Unity main-thread access, object handles, reflection, and compact summaries. The Node sidecar owns MCP stdio, tool schemas, pagination defaults, bridge discovery, and client-facing errors. It accepts both newline-delimited JSON-RPC and `Content-Length` framed JSON-RPC on stdin, and responds using the same framing style as the request. JSON-lines is the default for parser errors or clients that do not send `Content-Length`.

v1 is same-machine only. Cloud-hosted agents need a later relay/tunnel design.

## Support Matrix

Tested first:

- BepInEx 5 Mono

Designed to remain compatible:

- BepInEx 6 Mono
- MelonLoader Mono
- Standalone Mono

Later validation:

- BepInEx IL2CPP
- BepInEx IL2CPP CoreCLR
- MelonLoader IL2CPP
- Standalone IL2CPP

The bridge starts from `ExplorerCore.LateInit` and avoids loader-specific APIs so the loader surface stays behind `IExplorerLoader`.

## Bridge Discovery

The bridge binds to localhost only:

```text
http://127.0.0.1:8765/
```

If the default port is busy it tries the next ports. A random token is generated per session and written to:

```text
<os temp>/unityexplorer-mcp/connection.json
```

The sidecar auto-loads that file. It can also be configured explicitly:

```bash
unity-explorer-mcp --url http://127.0.0.1:8765/ --token TOKEN
```

or with environment variables:

```bash
UE_MCP_BRIDGE_URL=http://127.0.0.1:8765/
UE_MCP_BRIDGE_TOKEN=TOKEN
UE_MCP_CONNECTION_FILE=/path/to/connection.json
```

## MCP Tools

Implemented v1 tools:

- `ue_status`
- `ue_list_scenes`
- `ue_search_objects`
- `ue_get_object`
- `ue_list_hierarchy`
- `ue_list_components`
- `ue_list_members`
- `ue_read_member`
- `ue_write_member_preview`
- `ue_apply_confirmed_write`
- `ue_invoke_member_preview`
- `ue_apply_confirmed_invoke`
- `ue_generate_mod_recipe`

List/search tools accept:

```json
{
  "limit": 25,
  "cursor": null,
  "detail": "summary"
}
```

The bridge caps `limit` at 100. `detail` is accepted by the sidecar schema for forward compatibility; v1 bridge responses are summary-oriented.

## Handles And Summaries

The bridge returns opaque handles for runtime objects:

```json
{
  "handle": "obj_123",
  "kind": "GameObject",
  "name": "RunSettingFloat(Clone)",
  "path": "Canvas/RunSettings/CustomSettings/Scroll/Viewport/Content/RunSettingFloat(Clone)",
  "scene": "PreGen",
  "activeSelf": true,
  "components": ["UnityEngine.RectTransform", "RunSettingFloat"],
  "childCount": 3,
  "text": null,
  "evidence": []
}
```

Handles are per game session. Destroyed Unity objects return clear stale-handle errors.

## Safety

- The bridge only accepts requests with the session token.
- Search refuses unfiltered whole-scene object dumps.
- Reads are explicit.
- Writes and invokes require preview plus a one-time confirmation token.
- Search and member listing do not automatically invoke methods.
- Property reads are marked as property-getter risk because some games use side-effectful getters.

## Loot Density Workflow

1. Find likely settings roots:

```json
ue_search_objects({
  "scene": "PreGen",
  "nameOrPathContains": "RunSettings",
  "limit": 20
})
```

2. Search under the root for visible label text:

```json
ue_list_hierarchy({
  "rootHandle": "obj_RunSettings",
  "depth": 5,
  "textContains": "Loot density",
  "limit": 25
})
```

3. Inspect the matched row:

```json
ue_list_components({
  "objectHandle": "obj_RowLootDensity"
})
```

4. Inspect controller and UI members:

```json
ue_list_members({
  "targetHandle": "component_RunSettingFloat",
  "filter": "slider,value,on",
  "kinds": ["field", "property", "method"]
})
```

5. Prove the binding:

```json
ue_read_member({
  "targetHandle": "component_Slider",
  "memberId": "property:value"
})
```

6. Prototype only with confirmation:

- Preview a write or invocation.
- Apply it only with the returned action token.
- Prefer adding a `TMP_InputField` or companion input behavior. `TextMeshProUGUI` remains display text.

7. Generate a BepInEx/Harmony recipe:

```json
ue_generate_mod_recipe({
  "framework": "BepInExHarmony",
  "goal": "Make Loot density value editable and sync it to Slider.value",
  "evidenceHandles": ["obj_RowLootDensity", "obj_Slider", "obj_Value", "component_RunSettingFloat"]
})
```

The generated recipe should prefer label text and component layout as stable locators, not clone index alone.

## Packaging

Build the sidecar:

```bash
cd sidecar
npm install
npm run build
```

Run protocol smoke tests:

```bash
cd sidecar
npm run test:protocol
```

Run from a local MCP client using:

```bash
npx @unity-explorer/mcp
```

For local development:

```bash
node sidecar/dist/index.js
```

The repository `build.ps1` also builds and packages the sidecar. It writes:

```text
Release/UnityExplorer.MCP.Sidecar.zip
Release/unity-explorer-mcp-<version>.tgz
```
