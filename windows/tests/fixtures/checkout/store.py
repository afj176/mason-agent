"""A deterministic, local WSGI checkout with a disposable SQLite order database."""
import json
import sqlite3
import uuid
from pathlib import Path

PRODUCTS = ["Honey", "Berries", "Salmon"]
THRESHOLD = 3
SAVE_ORDER = True

def application(env, respond):
    route = env["PATH_INFO"]
    with sqlite3.connect("orders.sqlite") as db:
        db.execute("CREATE TABLE IF NOT EXISTS orders (id TEXT PRIMARY KEY)")
        status, mime = "200 OK", "application/json"
        if route == "/index.html":
            mime = "text/html; charset=utf-8"
            body = Path("index.html").read_text(encoding="utf-8").replace("PRODUCT_LIST", "".join("<li>"+p+"</li>" for p in PRODUCTS))
        elif route == "/price":
            quantity = int(env["QUERY_STRING"].split("=")[1])
            body = json.dumps({"total": quantity * 10 * (.9 if quantity >= THRESHOLD else 1)})
        elif route == "/checkout" and env["REQUEST_METHOD"] == "POST":
            order = "ORD-" + uuid.uuid4().hex[:12]
            if SAVE_ORDER:
                db.execute("INSERT INTO orders VALUES (?)", (order,))
            body = json.dumps({"id": order})
        elif route.startswith("/orders/"):
            row = db.execute("SELECT id FROM orders WHERE id=?", (route.split("/")[-1],)).fetchone()
            mime = "text/html; charset=utf-8"
            body = "<output id='order'>" + (row[0] if row else "") + "</output>"
        else:
            status, body = "404 Not Found", ""
    respond(status, [("Content-Type", mime)])
    return [body.encode()]
