#!/usr/bin/env python3
"""A small MCP server for tests. It offers two tools (echo, fail) and one resource (note://hello).

    mcp-test-server.py stdio          newline-delimited JSON-RPC on stdin and stdout
    mcp-test-server.py http PORT      streamable HTTP on 127.0.0.1:PORT/mcp

Over HTTP it hands out a session id on initialize and refuses later requests without it, answers
tools/call as a server-sent event stream and everything else as plain JSON, so both reply forms are exercised.
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

VERSION = "2025-06-18"
TOOLS = [
    {"name": "echo", "description": "Returns the text it is given.", "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}},
    {"name": "fail", "description": "Always reports an error.", "inputSchema": {"type": "object", "properties": {}}},
]
RESOURCES = [{"uri": "note://hello", "name": "hello", "mimeType": "text/plain"}]


def answer(message):
    """The JSON-RPC reply to one message, or None for a notification."""
    if "id" not in message:
        return None
    method, params = message.get("method"), message.get("params") or {}
    result = None
    if method == "initialize":
        result = {"protocolVersion": VERSION, "capabilities": {"tools": {}, "resources": {}}, "serverInfo": {"name": "mason-test", "version": "1"}}
    elif method == "tools/list":
        result = {"tools": TOOLS}
    elif method == "resources/list":
        result = {"resources": RESOURCES}
    elif method == "resources/read":
        if params.get("uri") == "note://hello":
            result = {"contents": [{"uri": "note://hello", "mimeType": "text/plain", "text": "hello from the test server"}]}
    elif method == "tools/call":
        if params.get("name") == "echo":
            result = {"content": [{"type": "text", "text": "echo: " + str((params.get("arguments") or {}).get("text", ""))}]}
        elif params.get("name") == "fail":
            result = {"content": [{"type": "text", "text": "it broke"}], "isError": True}
    if result is None:
        return {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": "unknown method or name"}}
    return {"jsonrpc": "2.0", "id": message["id"], "result": result}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_DELETE(self):
        self.send_response(200 if self.headers.get("Mcp-Session-Id") == "test-session" else 400)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        message = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        if message.get("method") != "initialize" and self.headers.get("Mcp-Session-Id") != "test-session":
            self.send_response(400)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        reply = answer(message)
        if reply is None:
            self.send_response(202)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if message.get("method") == "tools/call":
            body = ("event: message\r\ndata: " + json.dumps(reply) + "\r\n\r\n").encode()
            kind = "text/event-stream"
        else:
            body, kind = json.dumps(reply).encode(), "application/json"
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        if message.get("method") == "initialize":
            self.send_header("Mcp-Session-Id", "test-session")
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "http":
        HTTPServer(("127.0.0.1", int(sys.argv[2])), Handler).serve_forever()
    for line in sys.stdin:
        if not line.strip():
            continue
        reply = answer(json.loads(line))
        if reply is not None:
            sys.stdout.write(json.dumps(reply) + "\n")
            sys.stdout.flush()
