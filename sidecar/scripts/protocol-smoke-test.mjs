import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { once } from "node:events";
import { cwd, env, execPath } from "node:process";
import { fileURLToPath } from "node:url";

const sidecarPath = fileURLToPath(new URL("../dist/index.js", import.meta.url));

function jsonRpc(id, method, params = {}) {
  return { jsonrpc: "2.0", id, method, params };
}

function contentLengthFrame(message) {
  const body = JSON.stringify(message);
  return `Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`;
}

function jsonLineFrame(message) {
  return `${JSON.stringify(message)}\n`;
}

async function runSidecar({ chunks, outputMode = "jsonl", extraEnv = {} }) {
  const child = spawn(execPath, [sidecarPath], {
    cwd: cwd(),
    env: { ...env, ...extraEnv },
    stdio: ["pipe", "pipe", "pipe"]
  });

  let stdout = Buffer.alloc(0);
  let stderr = "";
  child.stdout.on("data", chunk => {
    stdout = Buffer.concat([stdout, chunk]);
  });
  child.stderr.on("data", chunk => {
    stderr += chunk.toString("utf8");
  });

  for (const chunk of chunks) {
    child.stdin.write(chunk);
    await delay(10);
  }

  child.stdin.end();
  await Promise.race([once(child, "exit"), delay(1500)]);
  if (!child.killed) {
    child.kill();
  }

  const responses = outputMode === "content-length"
    ? parseContentLengthResponses(stdout)
    : parseJsonLineResponses(stdout);

  return { responses, stdout: stdout.toString("utf8"), stderr };
}

function parseJsonLineResponses(stdout) {
  return stdout
    .toString("utf8")
    .split(/\r?\n/)
    .filter(line => line.trim().length > 0)
    .map(line => JSON.parse(line));
}

function parseContentLengthResponses(stdout) {
  const responses = [];
  let buffer = stdout;

  while (buffer.length > 0) {
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd < 0) break;

    const header = buffer.subarray(0, headerEnd).toString("utf8");
    const match = /content-length:\s*(\d+)/i.exec(header);
    assert.ok(match, `Missing Content-Length in ${header}`);

    const length = Number(match[1]);
    const start = headerEnd + 4;
    const end = start + length;
    assert.ok(buffer.length >= end, "Incomplete Content-Length response");

    responses.push(JSON.parse(buffer.subarray(start, end).toString("utf8")));
    buffer = buffer.subarray(end);
  }

  return responses;
}

async function testJsonLinesInitializeAndToolsList() {
  const { responses, stdout } = await runSidecar({
    chunks: [
      jsonLineFrame(jsonRpc(1, "initialize", { protocolVersion: "2024-11-05" })),
      jsonLineFrame(jsonRpc(2, "tools/list"))
    ]
  });

  assert.equal(responses.length, 2);
  assert.equal(responses[0].id, 1);
  assert.equal(responses[0].result.serverInfo.name, "@unity-explorer/mcp");
  assert.equal(responses[1].id, 2);
  assert.ok(responses[1].result.tools.some(tool => tool.name === "ue_status"));
  assert.ok(!stdout.includes("Content-Length:"), "JSON-lines mode must not emit Content-Length responses");
}

async function testContentLengthInitializeAndToolsList() {
  const { responses, stdout } = await runSidecar({
    outputMode: "content-length",
    chunks: [
      contentLengthFrame(jsonRpc(1, "initialize", { protocolVersion: "2024-11-05" })),
      contentLengthFrame(jsonRpc(2, "tools/list"))
    ]
  });

  assert.equal(responses.length, 2);
  assert.equal(responses[0].id, 1);
  assert.equal(responses[1].id, 2);
  assert.ok(responses[1].result.tools.some(tool => tool.name === "ue_list_members"));
  assert.ok(stdout.includes("Content-Length:"), "Content-Length mode must preserve framed responses");
}

async function testChunkedSplitInput() {
  const first = jsonLineFrame(jsonRpc(1, "initialize", { protocolVersion: "2024-11-05" }));
  const second = jsonLineFrame(jsonRpc(2, "tools/list"));
  const combined = first + second;

  const { responses } = await runSidecar({
    chunks: [
      combined.slice(0, 5),
      combined.slice(5, 37),
      combined.slice(37)
    ]
  });

  assert.equal(responses.length, 2);
  assert.equal(responses[0].id, 1);
  assert.equal(responses[1].id, 2);
}

async function testChunkedContentLengthInput() {
  const first = contentLengthFrame(jsonRpc(1, "initialize", { protocolVersion: "2024-11-05" }));
  const second = contentLengthFrame(jsonRpc(2, "tools/list"));
  const combined = first + second;

  const { responses } = await runSidecar({
    outputMode: "content-length",
    chunks: [
      combined.slice(0, 3),
      combined.slice(3, 24),
      combined.slice(24, 80),
      combined.slice(80)
    ]
  });

  assert.equal(responses.length, 2);
  assert.equal(responses[0].id, 1);
  assert.equal(responses[1].id, 2);
}

async function testStatusBridgeCall() {
  const token = "test-token";
  const server = createServer((request, response) => {
    assert.equal(request.method, "POST");
    assert.equal(request.url, "/rpc");
    assert.equal(request.headers["x-unityexplorer-mcp-token"], token);

    let body = "";
    request.setEncoding("utf8");
    request.on("data", chunk => {
      body += chunk;
    });
    request.on("end", () => {
      const rpc = JSON.parse(body);
      assert.equal(rpc.method, "status");

      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({
        ok: true,
        result: {
          bridge: "fake",
          running: true,
          unityExplorerVersion: "test"
        }
      }));
    });
  });

  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  const url = `http://127.0.0.1:${address.port}/`;

  try {
    const { responses } = await runSidecar({
      chunks: [
        jsonLineFrame(jsonRpc(1, "tools/call", {
          name: "ue_status",
          arguments: {}
        }))
      ],
      extraEnv: {
        UE_MCP_BRIDGE_URL: url,
        UE_MCP_BRIDGE_TOKEN: token
      }
    });

    assert.equal(responses.length, 1);
    assert.equal(responses[0].id, 1);
    const text = responses[0].result.content[0].text;
    const status = JSON.parse(text);
    assert.equal(status.running, true);
    assert.equal(status.unityExplorerVersion, "test");
  } finally {
    server.close();
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

await testJsonLinesInitializeAndToolsList();
await testContentLengthInitializeAndToolsList();
await testChunkedSplitInput();
await testChunkedContentLengthInput();
await testStatusBridgeCall();

console.error("protocol smoke tests passed");
