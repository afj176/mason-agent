import json
import sqlite3
import uuid
from pathlib import Path

STORE_NAME = "Trail Market"

def checkout(key):
    with sqlite3.connect("orders.sqlite") as db:
        db.execute("BEGIN IMMEDIATE")
        db.execute("CREATE TABLE IF NOT EXISTS orders (id TEXT PRIMARY KEY, request_key TEXT NOT NULL)")
        prior = db.execute("SELECT id FROM orders WHERE request_key = ?", (key,)).fetchone()
        if prior:
            return prior[0]
        receipt = "ORD-" + uuid.uuid4().hex
        db.execute("INSERT INTO orders (id, request_key) VALUES (?, ?)", (receipt, key))
        return receipt

def application(env, respond):
    if env["PATH_INFO"] == "/index.html" and env["REQUEST_METHOD"] == "GET":
        respond("200 OK", [("Content-Type", "text/html; charset=utf-8")])
        return [Path("index.html").read_bytes().replace(b"STORE_NAME", STORE_NAME.encode())]
    if env["PATH_INFO"] == "/checkout" and env["REQUEST_METHOD"] == "POST":
        key = json.load(env["wsgi.input"])["key"]
        respond("200 OK", [("Content-Type", "application/json")])
        return [json.dumps({"id": checkout(key)}).encode()]
    respond("404 Not Found", [("Content-Type", "text/plain")])
    return [b"Unknown route"]
