"""
Live check of the full inquiry lifecycle, and of the Windows Agent's server-side protocol, against
the running iemas-inq stack.

This plays the Windows Agent: it registers two devices, gets them approved, authenticates, syncs and
submits the same case actions the 'Confirm Resolved' / 'Not Resolved Yet' buttons submit
(POST /api/v1/agent/case-actions). It does NOT run the WPF window - see WINDOWS-AGENT-TEST.md.

The AI is mock_openrouter.py (the real IEMAS OpenRouter client over real HTTP, but a rule-based
stand-in for the model). Start it first:
  docker run -d --name iemas-inq-mockai --network iemas-inq_default --network-alias mockai \
      -v <repo>/inquiry-supervision/live-test:/t:ro python:3.12-alpine python /t/mock_openrouter.py
"""
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

import live_e2e as base

AGENT1, AGENT2 = "agent1@sawo.com", "agent2@sawo.com"
RUN = base.RUN


def agent_call(token, method, path, body=None):
    """Raw call to the Agent API. Returns (http status, parsed json or None)."""
    req = urllib.request.Request(base.API + path, data=json.dumps(body).encode() if body is not None else None, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw)
        except ValueError:
            return e.code, {"raw": raw[:200]}


def enroll(email, employee_id, device):
    status, reg = agent_call(None, "POST", "/agent-enrollment/register",
                             {"emailAddress": email, "clientName": f"{device}-{RUN}", "serverAddress": base.API, "agentVersion": "live-test", "deviceMetadata": device})
    if status != 200:
        raise RuntimeError(f"register failed: {status} {reg}")
    base.api("POST", f"/agents/{reg['agentId']}/approve", {"employeeId": employee_id})
    _, st = agent_call(None, "GET", f"/agent-enrollment/status/{reg['registrationRequestToken']}")
    status, auth = agent_call(None, "POST", "/agent-auth/authenticate", {"agentId": reg["agentId"], "registrationKey": st["registrationKey"]})
    if status != 200:
        raise RuntimeError(f"authenticate failed: {status} {auth}")
    return auth["accessToken"]


def action(token, case_id, kind, comment=None, request_id=None):
    return agent_call(token, "POST", "/agent/case-actions", {
        "requestId": request_id or f"live-{RUN}-{kind}-{time.time_ns()}", "caseId": case_id, "actionType": kind,
        "comment": comment, "clientTimestamp": datetime.now(timezone.utc).isoformat()})


def notified(case_id, title_part):
    items = base.api("GET", f"/notifications?caseId={case_id}&take=50")
    return any(title_part.lower() in n["title"].lower() for n in items)


def main():
    base.token = base.api("POST", "/auth/login", {"email": base.ADMIN_EMAIL, "password": base.ADMIN_PASSWORD})["accessToken"]
    employees = {e["email"]: e["id"] for e in base.api("GET", "/employees")}

    base.log("Pointing the AI connection at the stand-in (stored encrypted; the key is never returned)")
    saved = base.api("PUT", "/ai-models/provider", {"baseUrl": "http://mockai:9000/api/v1", "apiKey": "mock-key-1234567890", "clearApiKey": False})
    base.check("API key stored, only a hint is returned", saved["hasApiKey"] and saved["apiKeyHint"] == "7890" and "mock-key" not in json.dumps(saved), json.dumps({k: saved[k] for k in ("hasApiKey", "apiKeyHint", "keySource")}))
    check = base.api("POST", "/ai-models/provider/test")
    base.log(f"      provider test -> {str(check)[:140]}")

    base.log("\nWindows Agent protocol: register, approve, authenticate")
    t1 = enroll(AGENT1, employees[AGENT1], "pc-agent1")
    t2 = enroll(AGENT2, employees[AGENT2], "pc-agent2")
    base.check("both Agents authenticated", bool(t1 and t2))
    base.check("Agent API refuses a request without a token", agent_call(None, "GET", "/agent/sync")[0] == 401)
    base.check("Agent API refuses a CMS login token", agent_call(base.token, "GET", "/agent/sync")[0] in (401, 403))

    now = datetime.now(timezone.utc)
    base.imap(AGENT1).logout()

    # ---------------- A: acknowledgement only must stay open and supervised
    subjA = f"Lifecycle ack {RUN}"
    mA = f"<lcA{RUN}@customer.example>"
    base.smtp_send("amy@customer.example", AGENT1, subjA, "My heater shows error E3 and will not start. Can you help me?", now - timedelta(minutes=100), mA)
    base.agent_sent(AGENT1, "amy@customer.example", f"Re: {subjA}", "We have received your message and will look into it.", now - timedelta(minutes=90), f"<lcA2{RUN}@sawo.com>", mA)

    # ---------------- B: the full lifecycle
    subjB = f"Lifecycle main {RUN}"
    mB = f"<lcB{RUN}@customer.example>"
    base.smtp_send("ben@customer.example", AGENT1, subjB, "My heater shows error E3 and will not start. Can you help me?", now - timedelta(minutes=100), mB)
    base.log("\nB. customer inquiry arrives")
    inq = base.wait_for("B detected", lambda: base.find(subjB), timeout=240)
    base.check("B: inquiry identified and recorded", inq is not None)
    if not inq:
        sys.exit(1)
    base.check("B: assigned to the right agent", inq["agentName"] == "Agent agent1")
    base.check("B: agent notified (New Email pop-up recorded)", bool(base.wait_for("new email notification", lambda: notified(inq["id"], "New Email"), timeout=90, every=5)))
    sync = agent_call(t1, "GET", "/agent/sync")[1]
    base.check("B: the Agent's own sync lists it as needing action", any(c["id"] == inq["id"] for c in sync["actionRequired"]))
    sync2 = agent_call(t2, "GET", "/agent/sync")[1]
    base.check("B: another agent's sync does NOT list it", not any(c["id"] == inq["id"] for c in sync2["actionRequired"] + sync2["waiting"]))

    base.log("B. agent replies (a first, partial answer)")
    rB1 = f"<lcB2{RUN}@sawo.com>"
    base.agent_sent(AGENT1, "ben@customer.example", f"Re: {subjB}", "Please reseat the grey sensor plug behind the panel and restart it.", now - timedelta(minutes=90), rB1, mB)
    i = base.wait_for("B reply recorded", base.stage_is(subjB, "InitialResponseReceived"), timeout=240)
    base.check("B: agent's reply recorded; inquiry remains under supervision", i is not None, i and i["stageLabel"])

    base.log("B. customer writes again in the same conversation")
    base.smtp_send("ben@customer.example", AGENT1, f"Re: {subjB}", "I did that, now it shows E7. What should I do?", datetime.now(timezone.utc) - timedelta(minutes=20), f"<lcB3{RUN}@customer.example>", rB1, f"{mB} {rB1}")
    i = base.wait_for("B follow-up", base.stage_is(subjB, "CustomerFollowUpReceived"), timeout=240)
    base.check("B: customer follow-up joined the existing inquiry; agent owes a reply", i is not None)
    base.check("B: still one inquiry for this conversation", len([x for x in base.inquiries("all", RUN) if subjB in x["subject"]]) == 1)

    base.log("B. agent answers again; AI evaluates")
    base.agent_sent(AGENT1, "ben@customer.example", f"Re: {subjB}", "E7 means the fuse tripped. Press reset behind the panel; that clears the error.", datetime.now(timezone.utc) - timedelta(minutes=5), f"<lcB4{RUN}@sawo.com>", f"<lcB3{RUN}@customer.example>")
    i = base.wait_for("B potentially resolved", base.stage_is(subjB, "PotentiallyResolved"), timeout=300)
    base.check("B: AI marked it Potentially resolved (not closed)", i is not None, i and i["stageLabel"])
    if not i:
        sys.exit(1)
    d = base.detail(i["id"])
    base.check("B: the AI only recommended: not completed", d["stage"] == "PotentiallyResolved" and d["aiVerdict"] == "AppearsResolved", f"{d['aiVerdict']} {d['aiConfidence']}")
    base.check("B: agent asked to confirm (pop-up recorded)", notified(i["id"], "resolved"))
    sync = agent_call(t1, "GET", "/agent/sync")[1]
    row = next((c for c in sync["actionRequired"] if c["id"] == i["id"]), None)
    base.check("B: the Agent's list shows it with the 'looks resolved' status (12)", row is not None and row["workStatus"] == 12, str(row and row["workStatus"]))

    base.log("\nThe Confirm Resolved / Not Resolved Yet requests, as the Windows Agent sends them")
    s, r = action(t2, i["id"], 10)
    base.check("another agent cannot confirm this inquiry (error shown to the user)", s == 400 and "own" in str(r.get("message", "")).lower(), f"{s} {r}")
    s, r = action(t1, "00000000-0000-0000-0000-000000000001", 10)
    base.check("confirming an unknown inquiry returns a clear error", s == 400 and "not found" in str(r.get("message", "")).lower(), f"{s} {r}")

    # C: reject path on its own inquiry
    subjC = f"Lifecycle reject {RUN}"
    mC = f"<lcC{RUN}@customer.example>"
    base.smtp_send("cat@customer.example", AGENT1, subjC, "My heater shows error E3 and will not start. Can you help me?", now - timedelta(minutes=100), mC)
    base.agent_sent(AGENT1, "cat@customer.example", f"Re: {subjC}", "E3 means the sensor lost contact. Reseat the plug; that clears the error.", now - timedelta(minutes=90), f"<lcC2{RUN}@sawo.com>", mC)
    iC = base.wait_for("C potentially resolved", base.stage_is(subjC, "PotentiallyResolved"), timeout=300)
    base.check("C: AI suggested resolution", iC is not None)
    if iC:
        s, r = action(t1, iC["id"], 11, "Still waiting for the spare part")
        base.check("'Not Resolved Yet' accepted", s == 200, f"{s} {r}")
        dC = base.detail(iC["id"])
        base.check("C: rejected -> back under active supervision, not closed", dC["stage"] not in ("PotentiallyResolved", "Resolved", "Closed"), dC["stageLabel"])
        s, r = action(t1, iC["id"], 11)
        base.check("rejecting again (no longer 'looks resolved') returns a clear error", s == 400 and "not marked" in str(r.get("message", "")).lower(), f"{s} {r}")
        time.sleep(100)
        base.check("C: AI does not re-propose the same conversation after the agent rejected it", base.detail(iC["id"])["stage"] not in ("PotentiallyResolved", "Resolved"), base.detail(iC["id"])["stageLabel"])

    s, r = action(t1, i["id"], 10, "Customer confirmed")
    base.check("'Confirm Resolved' accepted", s == 200, f"{s} {r}")
    d = base.detail(i["id"])
    base.check("B: agent confirmation -> Resolved", d["stage"] == "Resolved", d["stageLabel"])
    base.check("B: status shown to the Agent is Completed", (r or {}).get("workStatus") == "Completed", str(r))
    s2, r2 = action(t1, i["id"], 10, request_id=f"dup-{RUN}")
    s3, r3 = action(t1, i["id"], 10, request_id=f"dup-{RUN}")
    base.check("confirming again returns a clear error; a repeated identical request is idempotent", s2 == 400 and s3 == 200 and r3.get("wasIdempotentReplay") is True, f"{s2} {s3}")

    # A: still open
    base.log("\nA. acknowledgement only")
    iA = base.wait_for("A reply recorded", base.stage_is(subjA, "InitialResponseReceived"), timeout=240)
    base.check("A: acknowledgement recorded, inquiry open", iA is not None)
    if iA:
        time.sleep(100)
        dA = base.detail(iA["id"])
        base.check("A: the AI judged it acknowledgement-only and the inquiry stays open", dA["aiVerdict"] == "AcknowledgedOnly" and dA["stage"] == "InitialResponseReceived", f"{dA['aiVerdict']} / {dA['stageLabel']}")

    base.log("\nNothing more happens to the resolved inquiry")
    before = base.api("GET", f"/notifications?caseId={i['id']}&take=100")
    time.sleep(130)
    after = base.api("GET", f"/notifications?caseId={i['id']}&take=100")
    base.check("B: no new pop-ups, reminders or escalations after confirmation", len(after) == len(before) and base.detail(i["id"])["stage"] == "Resolved")

    stats = json.loads(urllib.request.urlopen("http://mockai:9000/", timeout=10).read())
    base.check("AI stand-in received real OpenRouter-shaped requests with a Bearer key", stats["requests"] > 0 and stats["with_bearer"] == stats["requests"], str(stats))

    passed = sum(1 for _, ok, _ in base.results if ok)
    base.log(f"\n{passed}/{len(base.results)} checks passed")
    sys.exit(0 if passed == len(base.results) else 1)


if __name__ == "__main__":
    main()
