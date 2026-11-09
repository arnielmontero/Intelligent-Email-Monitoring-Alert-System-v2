"""
A stand-in for the OpenRouter chat-completions endpoint, for live tests only.

It lets the REAL IEMAS OpenRouter client code (request building, auth header, JSON parsing, usage
recording, error handling) run over real HTTP without a paid key. It is NOT a language model: it
answers by simple rules, so a result here proves the plumbing, never the quality of a real model's
judgement. It records only whether a Bearer token was received (never the token or any email text).

Point the stack at it with: PUT /ai-models/provider {"baseUrl": "http://mockai:9000/api/v1", "apiKey": "x"}.
"""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

STATS = {"requests": 0, "with_bearer": 0, "classification": 0, "resolution": 0}


def last_turn(user):
    parts = user.split("--- ")
    return parts[-1] if len(parts) > 1 else user


def resolution(user):
    last = last_turn(user).lower()
    role = "customer" if last.startswith("customer") else "agent" if last.startswith("agent") else "internal"
    if role == "customer" and "thank" in last and ("works" in last or "solved" in last):
        return {"verdict": "APPEARS_RESOLVED", "confidence": 0.93, "reason": "The customer confirmed the fix worked and thanked the agent."}
    if role == "customer":
        return {"verdict": "CUSTOMER_WAITING", "confidence": 0.9, "reason": "The latest customer message is unanswered."}
    if "look into it" in last or "received your" in last:
        return {"verdict": "ACKNOWLEDGED_ONLY", "confidence": 0.94, "reason": "The agent only acknowledged the request."}
    if "that clears the error" in last or "here is your quotation" in last:
        return {"verdict": "APPEARS_RESOLVED", "confidence": 0.8, "reason": "The agent supplied the requested solution."}
    return {"verdict": "IN_PROGRESS", "confidence": 0.7, "reason": "Work is under way."}


def classification(user):
    body = user.lower().split("body:")[-1]
    relevant = "?" in body or "project" in body
    return {"relevant": relevant, "legitimate": relevant, "category": "PROJECT_INQUIRY" if relevant else "OTHER",
            "action_required": relevant, "response_expected": relevant, "priority": "MEDIUM", "confidence": 0.92,
            "summary": "Mock classification."}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):  # no request logging: never log bodies
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(json.dumps(STATS).encode())

    def do_POST(self):
        STATS["requests"] += 1
        if (self.headers.get("Authorization") or "").startswith("Bearer ") and len(self.headers["Authorization"]) > 10:
            STATS["with_bearer"] += 1
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        system = payload["messages"][0]["content"]
        user = payload["messages"][1]["content"]
        if "review the email conversation" in system.lower():
            STATS["resolution"] += 1
            content = resolution(user)
        else:
            STATS["classification"] += 1
            content = classification(user)
        out = {"id": "gen-mock", "choices": [{"message": {"content": json.dumps(content)}}],
               "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120, "cost": 0.0}}
        raw = json.dumps(out).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


if __name__ == "__main__":
    HTTPServer(("0.0.0.0", 9000), Handler).serve_forever()
