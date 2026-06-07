# server.py
from flask import Flask, request, jsonify
import subprocess

app = Flask(__name__)

@app.route("/connect", methods=["POST"])
def connect():
    data = request.json
    public_key = data["public_key"]
    # Lägg till peer dynamiskt
    subprocess.run(["wg", "set", "wg0", "peer", public_key, "allowed-ips", "10.8.0.3/32"])
    subprocess.run(["wg-quick", "save", "wg0"])
    return jsonify({"status": "ok", "ip": "10.8.0.3"})

@app.route("/disconnect", methods=["POST"])
def disconnect():
    data = request.json
    public_key = data["public_key"]
    subprocess.run(["wg", "set", "wg0", "peer", public_key, "remove"])
    subprocess.run(["wg-quick", "save", "wg0"])
    return jsonify({"status": "ok"})

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5001)
