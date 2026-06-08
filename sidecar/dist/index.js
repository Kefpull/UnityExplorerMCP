#!/usr/bin/env node
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { stdin, stdout, stderr, argv, env } from "node:process";
const toolToBridgeMethod = {
    ue_status: "status",
    ue_list_scenes: "list_scenes",
    ue_search_objects: "search_objects",
    ue_get_object: "get_object",
    ue_list_hierarchy: "list_hierarchy",
    ue_list_components: "list_components",
    ue_list_members: "list_members",
    ue_read_member: "read_member",
    ue_write_member_preview: "write_member_preview",
    ue_apply_confirmed_write: "apply_confirmed_write",
    ue_invoke_member_preview: "invoke_member_preview",
    ue_apply_confirmed_invoke: "apply_confirmed_invoke",
    ue_generate_mod_recipe: "generate_mod_recipe"
};
const paginationProperties = {
    limit: {
        type: "number",
        description: "Maximum items to return. Defaults to 25; bridge caps large values."
    },
    cursor: {
        type: ["string", "number", "null"],
        description: "Opaque cursor returned by the previous call."
    },
    detail: {
        type: "string",
        enum: ["summary", "normal", "diagnostic"],
        description: "Response detail level. summary is the default."
    }
};
const tools = [
    {
        name: "ue_status",
        description: "Check UnityExplorer MCP bridge status and connected game metadata.",
        inputSchema: schema({})
    },
    {
        name: "ue_list_scenes",
        description: "List loaded Unity scenes and special UnityExplorer scene buckets.",
        inputSchema: schema({})
    },
    {
        name: "ue_search_objects",
        description: "Search GameObjects with guided filters. Refuses unfiltered whole-scene dumps.",
        inputSchema: schema({
            scene: { type: "string" },
            nameOrPathContains: { type: "string" },
            textContains: { type: "string" },
            componentTypesAny: { type: "array", items: { type: "string" } },
            ...paginationProperties
        })
    },
    {
        name: "ue_get_object",
        description: "Get a compact GameObject summary by handle.",
        inputSchema: schema({
            handle: { type: "string" }
        }, ["handle"])
    },
    {
        name: "ue_list_hierarchy",
        description: "List descendants under a root handle or scene roots with depth, filters, and cursor paging.",
        inputSchema: schema({
            rootHandle: { type: "string" },
            depth: { type: "number" },
            textContains: { type: "string" },
            nameOrPathContains: { type: "string" },
            ...paginationProperties
        })
    },
    {
        name: "ue_list_components",
        description: "List compact component summaries for a GameObject handle.",
        inputSchema: schema({
            objectHandle: { type: "string" }
        }, ["objectHandle"])
    },
    {
        name: "ue_list_members",
        description: "List reflected fields, properties, and methods for an object, component, or type handle.",
        inputSchema: schema({
            targetHandle: { type: "string" },
            filter: { type: "string" },
            kinds: { type: "array", items: { type: "string", enum: ["field", "property", "method"] } },
            includeValues: { type: "boolean" },
            ...paginationProperties
        }, ["targetHandle"])
    },
    {
        name: "ue_read_member",
        description: "Explicitly read a field or non-indexed property. Use memberHandle or targetHandle plus memberId.",
        inputSchema: schema({
            memberHandle: { type: "string" },
            targetHandle: { type: "string" },
            memberId: { type: "string", description: "Example: property:value or field:slider" }
        })
    },
    {
        name: "ue_write_member_preview",
        description: "Preview a runtime field/property write and receive a confirmation token. Does not apply changes.",
        inputSchema: schema({
            memberHandle: { type: "string" },
            targetHandle: { type: "string" },
            memberId: { type: "string" },
            value: {}
        })
    },
    {
        name: "ue_apply_confirmed_write",
        description: "Apply a previously previewed write using its one-time actionToken.",
        inputSchema: schema({
            actionToken: { type: "string" }
        }, ["actionToken"])
    },
    {
        name: "ue_invoke_member_preview",
        description: "Preview a method invocation and receive a confirmation token. Does not invoke immediately.",
        inputSchema: schema({
            memberHandle: { type: "string" },
            targetHandle: { type: "string" },
            memberId: { type: "string" },
            arguments: { type: "array", items: {} }
        })
    },
    {
        name: "ue_apply_confirmed_invoke",
        description: "Apply a previously previewed invocation using its one-time actionToken.",
        inputSchema: schema({
            actionToken: { type: "string" }
        }, ["actionToken"])
    },
    {
        name: "ue_generate_mod_recipe",
        description: "Generate a BepInEx/Harmony-oriented mod recipe from discovered evidence handles.",
        inputSchema: schema({
            framework: { type: "string", description: "Defaults to BepInExHarmony." },
            goal: { type: "string" },
            evidenceHandles: { type: "array", items: { type: "string" } }
        })
    }
];
function schema(properties, required = []) {
    return {
        type: "object",
        additionalProperties: true,
        properties,
        required
    };
}
function parseArgs() {
    const values = {};
    for (let i = 2; i < argv.length; i++) {
        const arg = argv[i];
        if (!arg.startsWith("--"))
            continue;
        const key = arg.slice(2);
        const next = argv[i + 1];
        if (next && !next.startsWith("--")) {
            values[key] = next;
            i++;
        }
        else {
            values[key] = "true";
        }
    }
    return values;
}
function loadConnection() {
    const args = parseArgs();
    const explicitUrl = args.url ?? env.UE_MCP_BRIDGE_URL;
    const explicitToken = args.token ?? env.UE_MCP_BRIDGE_TOKEN;
    if (explicitUrl && explicitToken) {
        return { url: explicitUrl, token: explicitToken };
    }
    const connectionFile = args["connection-file"] ??
        env.UE_MCP_CONNECTION_FILE ??
        join(tmpdir(), "unityexplorer-mcp", "connection.json");
    if (!existsSync(connectionFile)) {
        throw new Error(`UnityExplorer MCP connection file was not found at ${connectionFile}. ` +
            `Start a game with the bridge enabled, or pass --url and --token.`);
    }
    const parsed = JSON.parse(readFileSync(connectionFile, "utf8"));
    if (!parsed.url || !parsed.token) {
        throw new Error(`Connection file ${connectionFile} did not contain url and token.`);
    }
    return parsed;
}
async function callBridge(method, params) {
    const connection = loadConnection();
    const response = await fetch(new URL("rpc", connection.url), {
        method: "POST",
        headers: {
            "content-type": "application/json",
            "x-unityexplorer-mcp-token": connection.token
        },
        body: JSON.stringify({ method, params: params ?? {} })
    });
    const text = await response.text();
    let payload;
    try {
        payload = JSON.parse(text);
    }
    catch {
        throw new Error(`Bridge returned non-JSON response (${response.status}): ${text}`);
    }
    if (!response.ok || !payload.ok) {
        const code = payload?.error?.code ?? response.status;
        const message = payload?.error?.message ?? text;
        throw new Error(`Bridge ${code}: ${message}`);
    }
    return payload.result;
}
async function handleRequest(request, framing) {
    try {
        if (request.method === "initialize") {
            send({
                jsonrpc: "2.0",
                id: request.id,
                result: {
                    protocolVersion: request.params?.protocolVersion ?? "2024-11-05",
                    capabilities: { tools: {} },
                    serverInfo: { name: "@unity-explorer/mcp", version: "0.1.0" }
                }
            }, framing);
            return;
        }
        if (request.method === "tools/list") {
            send({
                jsonrpc: "2.0",
                id: request.id,
                result: { tools }
            }, framing);
            return;
        }
        if (request.method === "tools/call") {
            const name = request.params?.name;
            const bridgeMethod = toolToBridgeMethod[name];
            if (!bridgeMethod) {
                throw new Error(`Unknown tool '${name}'.`);
            }
            const result = await callBridge(bridgeMethod, request.params?.arguments ?? {});
            send({
                jsonrpc: "2.0",
                id: request.id,
                result: {
                    content: [
                        {
                            type: "text",
                            text: JSON.stringify(result, null, 2)
                        }
                    ]
                }
            }, framing);
            return;
        }
        if (request.method.startsWith("notifications/")) {
            return;
        }
        sendError(request.id, -32601, `Unsupported method '${request.method}'.`, framing);
    }
    catch (error) {
        sendError(request.id, -32000, error instanceof Error ? error.message : String(error), framing);
    }
}
function send(message, framing = "jsonl") {
    const body = JSON.stringify(message);
    if (framing === "content-length") {
        stdout.write(`Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`);
        return;
    }
    stdout.write(`${body}\n`);
}
function sendError(id, code, message, framing = "jsonl") {
    send({
        jsonrpc: "2.0",
        id: id ?? null,
        error: { code, message }
    }, framing);
}
let buffer = Buffer.alloc(0);
stdin.on("data", chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    processBuffer().catch(error => {
        stderr.write(`${error instanceof Error ? error.stack : String(error)}\n`);
    });
});
stdin.on("error", error => {
    stderr.write(`${error.stack}\n`);
});
async function processBuffer() {
    while (true) {
        discardLeadingBlankBytes();
        if (buffer.length === 0)
            return;
        const framing = detectFraming(buffer);
        if (framing === "content-length") {
            const frame = readContentLengthFrame();
            if (!frame)
                return;
            await dispatchRawMessage(frame, "content-length");
            continue;
        }
        const line = readJsonLineFrame();
        if (line == null)
            return;
        if (line.trim().length === 0)
            continue;
        await dispatchRawMessage(line, "jsonl");
    }
}
function detectFraming(bytes) {
    const prefix = bytes.subarray(0, Math.min(bytes.length, 64)).toString("utf8");
    if (/^content-length\s*:/i.test(prefix)) {
        return "content-length";
    }
    return "jsonl";
}
function readContentLengthFrame() {
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd < 0) {
        return null;
    }
    const header = buffer.subarray(0, headerEnd).toString("utf8");
    const match = /content-length:\s*(\d+)/i.exec(header);
    if (!match) {
        buffer = buffer.subarray(headerEnd + 4);
        sendError(null, -32700, "Malformed Content-Length frame.", "jsonl");
        return "";
    }
    const length = Number(match[1]);
    const messageStart = headerEnd + 4;
    const messageEnd = messageStart + length;
    if (buffer.length < messageEnd) {
        return null;
    }
    const body = buffer.subarray(messageStart, messageEnd).toString("utf8");
    buffer = buffer.subarray(messageEnd);
    return body;
}
function readJsonLineFrame() {
    const newline = buffer.indexOf(0x0a);
    if (newline < 0) {
        return null;
    }
    let line = buffer.subarray(0, newline).toString("utf8");
    buffer = buffer.subarray(newline + 1);
    if (line.endsWith("\r")) {
        line = line.slice(0, -1);
    }
    return line;
}
async function dispatchRawMessage(body, framing) {
    try {
        await handleRequest(JSON.parse(body), framing);
    }
    catch (error) {
        sendError(null, -32700, error instanceof Error ? `Invalid JSON-RPC message: ${error.message}` : "Invalid JSON-RPC message.", framing);
    }
}
function discardLeadingBlankBytes() {
    let offset = 0;
    while (offset < buffer.length) {
        const byte = buffer[offset];
        if (byte !== 0x20 && byte !== 0x09 && byte !== 0x0d && byte !== 0x0a) {
            break;
        }
        offset++;
    }
    if (offset > 0) {
        buffer = buffer.subarray(offset);
    }
}
