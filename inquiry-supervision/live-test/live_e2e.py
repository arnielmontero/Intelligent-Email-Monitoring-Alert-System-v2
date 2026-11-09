"""
Live end-to-end test of inquiry supervision against the running iemas-inq stack.

Real IMAP/SMTP (the 'testmail' GreenMail container), the real API, the real scheduled jobs and a
real PostgreSQL database. Customer emails are sent by SMTP into agent mailboxes; agent replies are
placed in each agent's Sent folder by IMAP APPEND, exactly as a mail client would. Emails are
back-dated where a scenario needs time to have passed, so deadlines are already due and the
scheduled jobs (every 1-2 minutes) work through them in real time.

Python standard library only. Run it inside the stack's network (see README.md).
"""
import email.utils
import imaplib
import json
import os
import smtplib
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage

API = os.environ.get("API", "http://api:8080/api/v1")
MAIL_HOST = os.environ.get("MAIL_HOST", "testmail")
ADMIN_EMAIL = os.environ["BOOTSTRAP_ADMIN_EMAIL"]
ADMIN_PASSWORD = os.environ["BOOTSTRAP_ADMIN_PASSWORD"]
RUN = datetime.now(timezone.utc).strftime("%H%M%S")  # makes every run's subjects unique

results = []
token = None


def log(msg):
    print(msg, flush=True)


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    log(f"  [{'PASS' if ok else 'FAIL'}] {name}{(' - ' + detail) if detail else ''}")


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read().decode()[:400]}")


def wait_for(description, predicate, timeout=420, every=10):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = predicate()
            if last:
                return last
        except RuntimeError as e:
            last = str(e)
        time.sleep(every)
    log(f"  ... timed out waiting for: {description} (last: {str(last)[:200]})")
    return None


# ---------------------------------------------------------------- mail helpers

def smtp_send(sender, to, subject, body, date, msg_id, in_reply_to=None, references=None):
    m = EmailMessage()
    m["From"], m["To"], m["Subject"] = sender, to, subject
    m["Date"] = email.utils.format_datetime(date)
    m["Message-ID"] = msg_id
    if in_reply_to:
        m["In-Reply-To"] = in_reply_to
        m["References"] = references or in_reply_to
    m.set_content(body)
    with smtplib.SMTP(MAIL_HOST, 3025) as s:
        s.send_message(m)


def imap(user):
    c = imaplib.IMAP4(MAIL_HOST, 3143)
    c.login(user, "x")
    return c


def agent_sent(agent, to, subject, body, date, msg_id, in_reply_to, references=None):
    """What a mail client does when the agent sends: the message lands in the Sent folder."""
    m = EmailMessage()
    m["From"], m["To"], m["Subject"] = agent, to, subject
    m["Date"] = email.utils.format_datetime(date)
    m["Message-ID"] = msg_id
    m["In-Reply-To"] = in_reply_to
    m["References"] = references or in_reply_to
    m.set_content(body)
    c = imap(agent)
    c.create("Sent")
    c.append("Sent", "\\Seen", imaplib.Time2Internaldate(time.time()), m.as_bytes())
    c.logout()


def inbox_subjects(user):
    c = imap(user)
    c.select("INBOX")
    _, data = c.search(None, "ALL")
    subjects = []
    for num in data[0].split():
        _, msg = c.fetch(num, "(BODY.PEEK[HEADER.FIELDS (SUBJECT)])")
        subjects.append(msg[0][1].decode(errors="replace").strip())
    c.logout()
    return subjects


def inbox_bodies(user, subject_contains):
    c = imap(user)
    c.select("INBOX")
    _, data = c.search(None, "ALL")
    bodies = []
    for num in data[0].split():
        _, msg = c.fetch(num, "(RFC822)")
        parsed = email.message_from_bytes(msg[0][1])
        if subject_contains in str(parsed["Subject"]):
            part = parsed.get_payload(decode=True) if not parsed.is_multipart() else parsed.get_payload(0).get_payload(decode=True)
            bodies.append(part.decode(errors="replace"))
    c.logout()
    return bodies


# ---------------------------------------------------------------- inquiry helpers

def inquiries(bucket="all", search=None):
    q = f"/inquiries?bucket={bucket}&pageSize=100" + (f"&search={urllib.parse.quote(search)}" if search else "")
    return api("GET", q)["items"]


def find(subject):
    items = [i for i in inquiries("all", RUN) if subject in i["subject"]]
    return items[0] if items else None


def detail(case_id):
    return api("GET", f"/inquiries/{case_id}")


def stage_is(subject, *stages):
    def p():
        i = find(subject)
        return i if i and i["stage"] in stages else None
    return p


import urllib.parse  # noqa: E402  (used in inquiries())


def main():
    global token
    now = datetime.now(timezone.utc)
    log(f"Live inquiry supervision test, run {RUN}, now {now:%Y-%m-%d %H:%M} UTC")

    wait_for("API", lambda: urllib.request.urlopen(API.replace("/api/v1", "") + "/health/live", timeout=5).status == 200, timeout=180, every=5)
    token = api("POST", "/auth/login", {"email": ADMIN_EMAIL, "password": ADMIN_PASSWORD})["accessToken"]

    # ---- setup (idempotent across runs)
    employees = {e["email"]: e for e in api("GET", "/employees")}

    def employee(name, mail, supervisor=None):
        if mail in employees:
            return employees[mail]["id"]
        created = api("POST", "/employees", {"fullName": name, "email": mail, "departmentId": None, "supervisorEmployeeId": supervisor})
        employees[mail] = created
        return created["id"]

    manager = employee("Mia Manager", "manager@sawo.com")
    agents = {a: employee(f"Agent {a.split('@')[0]}", a, manager) for a in ("agent1@sawo.com", "agent2@sawo.com")}

    accounts = {a["emailAddress"]: a for a in api("GET", "/email-accounts")}
    if "iemas@sawo.com" not in accounts:
        api("POST", "/email-accounts", {
            "emailAddress": "iemas@sawo.com", "displayName": "IEMAS", "purpose": 1, "kind": 0, "protocol": 2,
            "host": MAIL_HOST, "port": 3025, "encryption": "None", "username": "iemas@sawo.com", "authMethod": 0, "secret": "x",
            "ownerEmployeeId": None, "classificationProfileName": None, "monitoringEnabled": False})
    for address, owner in agents.items():
        imap(address).logout()  # creates the mailbox
        if address not in accounts:
            api("POST", "/email-accounts", {
                "emailAddress": address, "displayName": None, "purpose": 0, "kind": 0, "protocol": 0,
                "host": MAIL_HOST, "port": 3143, "encryption": "None", "username": address, "authMethod": 0, "secret": "x",
                "ownerEmployeeId": owner, "classificationProfileName": None, "monitoringEnabled": True,
                "processEmailsReceivedAfter": "2026-01-01T00:00:00Z"})
    imap("manager@sawo.com").logout()

    def ids(n):
        return f"<{RUN}.{n}@customer.example>"

    # ---- send the scenario emails
    log("Sending scenario emails...")
    # S1 + S4 + S8: a clear inquiry 10 hours ago that agent1 never answers (24-hour calendar on a fresh install).
    s4 = f"Request for quotation {RUN}"
    smtp_send("anna@customer.example", "agent1@sawo.com", s4,
              "Hello, I would like to request a quotation for 20 heaters. How much per unit?", now - timedelta(hours=10), ids(1))
    # S2: newsletter and payment notice.
    smtp_send("newsletter@vendor.example", "agent1@sawo.com", f"November news {RUN}",
              "Our latest news. Unsubscribe at any time. View this email in your browser.", now - timedelta(minutes=5), ids(2))
    smtp_send("noreply@bank.example", "agent1@sawo.com", f"Payment confirmation {RUN}",
              "Payment received for invoice 1043. This is an automated message.", now - timedelta(minutes=5), ids(3))
    # S5 + S6: agent2 acknowledges only, then the customer follows up in the same thread.
    s5 = f"Spare part pricing {RUN}"
    smtp_send("ben@customer.example", "agent2@sawo.com", s5,
              "I would like to request a quotation for a control panel. What is the price?", now - timedelta(hours=2), ids(4))
    agent_sent("agent2@sawo.com", "ben@customer.example", f"Re: {s5}", "We have received your request and will look into it.",
               now - timedelta(hours=1, minutes=30), ids(5), ids(4))
    # S7: agent2 answered 4 days ago; the customer has been silent since.
    s7 = f"Sauna heater quotation {RUN}"
    smtp_send("cara@customer.example", "agent2@sawo.com", s7,
              "Can you provide a quotation for a 9kW sauna heater? How much is delivery?", now - timedelta(days=5), ids(6))
    agent_sent("agent2@sawo.com", "cara@customer.example", f"Re: {s7}", "Which voltage do you need, 230V or 400V?",
               now - timedelta(days=4), ids(7), ids(6))
    # S10: the same customer writes to both agents.
    s10 = f"Dealer inquiry {RUN}"
    for n, agent in ((8, "agent1@sawo.com"), (9, "agent2@sawo.com")):
        smtp_send("dan@dealer.example", agent, s10, "We would like to become a dealer. Can you provide a price list?", now - timedelta(minutes=20), ids(n))
    # Q3: agent1 forwards an inquiry internally; the colleague answers in the thread.
    q3 = f"Warranty extension {RUN}"
    smtp_send("eva@customer.example", "agent1@sawo.com", q3,
              "Can you provide a quotation for a warranty extension? How much?", now - timedelta(minutes=30), ids(10))
    agent_sent("agent1@sawo.com", "service@sawo.com", f"Fwd: {q3}", "Can service check the warranty options?",
               now - timedelta(minutes=25), ids(11), ids(10))
    # S9: agent1 sends a solution; the inquiry is then confirmed resolved by the agent.
    s9 = f"Heater fault {RUN}"
    smtp_send("finn@customer.example", "agent1@sawo.com", s9,
              "I would like to request a quotation for a repair, my heater shows E3. How much?", now - timedelta(hours=1), ids(12))
    agent_sent("agent1@sawo.com", "finn@customer.example", f"Re: {s9}", "E3 means the sensor; reset it. No repair is needed.",
               now - timedelta(minutes=50), ids(13), ids(12))

    log("Waiting for intake, classification, Case creation and the Sent-folder sync...")
    # The colleague's reply must follow the forward that the sync stores; send it once the inquiry exists.
    wait_for("Q3 inquiry", lambda: find(q3), timeout=300)
    smtp_send("service@sawo.com", "agent1@sawo.com", f"Re: Fwd: {q3}", "Option B applies: 3 extra years.",
              now + timedelta(minutes=1), ids(14), ids(11), f"{ids(10)} {ids(11)}")
    s5_reply = ids(5)
    smtp_send("ben@customer.example", "agent2@sawo.com", f"Re: {s5}", "Any update? Still waiting.",
              datetime.now(timezone.utc), ids(15), s5_reply, f"{ids(4)} {ids(5)}")

    log("\nScenario 1 - legitimate inquiry")
    i = wait_for("S1 inquiry", lambda: find(s4))
    check("S1 inquiry recorded", i is not None)
    if i:
        check("S1 assigned to agent1", i["agentName"] == "Agent agent1", i["agentName"])

    log("\nScenario 2 - non-inquiry email")
    check("S2 newsletter not an inquiry", find(f"November news {RUN}") is None)
    check("S2 payment notice not an inquiry", find(f"Payment confirmation {RUN}") is None)

    log("\nScenarios 5 + 6 - acknowledgement only, then customer follow-up")
    i = wait_for("S6 follow-up stage", stage_is(s5, "CustomerFollowUpReceived"))
    check("S6 follow-up joined the inquiry and handed the next step back to the agent", i is not None, i and i["stageLabel"])
    if i:
        d = detail(i["id"])
        roles = [m["role"] for m in d["conversation"]]
        check("S5 agent's acknowledgement stored, inquiry not closed", "Agent" in roles and d["stage"] not in ("Resolved", "Closed"), str(roles))
        check("S5 agent reply text readable to management (decrypted)", any(m["role"] == "Agent" and m["text"] and "look into it" in m["text"] for m in d["conversation"]))
        check("S6 one inquiry for the whole conversation", len([x for x in inquiries("all", RUN) if s5 in x["subject"]]) == 1)

    log("\nScenario 7 - customer silent for more than 3 days")
    i = wait_for("S7 inactive", stage_is(s7, "Inactive"))
    check("S7 inquiry became Inactive (agent told a follow-up may be needed)", i is not None, i and i["stageLabel"])

    log("\nScenario 10 - several agents")
    both = wait_for("S10 two inquiries", lambda: (lambda xs: xs if len(xs) == 2 else None)([x for x in inquiries("all", RUN) if s10 in x["subject"]]))
    check("S10 one inquiry per agent mailbox, correct owners",
          both is not None and sorted(x["agentName"] for x in both) == ["Agent agent1", "Agent agent2"], both and str([x["agentName"] for x in both]))

    log("\nQ3 - internal forward and colleague reply")
    i = wait_for("Q3 internal linked", lambda: (lambda x: x if x and len([m for m in detail(x["id"])["conversation"] if m["role"] == "Internal"]) >= 2 else None)(find(q3)))
    check("Q3 forward and colleague reply linked as Internal", i is not None)
    if i:
        check("Q3 still awaiting the agent's first reply", detail(i["id"])["stage"] == "AwaitingInitialResponse")
    check("Q3 colleague did not start a new inquiry", len([x for x in inquiries("all", RUN) if q3 in x["subject"]]) == 1)

    log("\nScenario 9 - agent confirms resolution; supervision stops")
    i = wait_for("S9 agent reply seen", stage_is(s9, "InitialResponseReceived", "PotentiallyResolved"))
    if i:
        api("POST", f"/inquiries/{i['id']}/confirm-resolved", {"comment": "Customer confirmed by phone (live test)."})
        d = detail(i["id"])
        check("S9 confirmed -> Resolved", d["stage"] == "Resolved", d["stageLabel"])
    else:
        check("S9 agent reply seen", False)

    log("\nScenarios 4 + 8 - no reply: deadline, 2 reminders, management escalation email (takes ~4 minutes)")
    def escalated():
        bodies = inbox_bodies("manager@sawo.com", f"Request for quotation {RUN}")
        return bodies[0] if bodies else None
    body = wait_for("escalation email in the manager's inbox", escalated, timeout=600, every=15)
    check("S8 escalation email delivered to the supervisor's mailbox", body is not None)
    if body:
        for field in ("Customer:", "Original subject:", "Original date:", "Assigned agent:", "Current status:", "Agent has replied:  No",
                      "Last activity:", "Inactive for:", "Previous reminders: 2", "Reason for escalation:"):
            check(f"S8 email contains '{field}'", field in body)
    i = find(s4)
    if i:
        d = detail(i["id"])
        check("S4 two reminders before escalation", d["episodeRemindersSent"] == 2, str(d["episodeRemindersSent"]))
        check("S4 still awaiting the initial response (supervision continues)", d["stage"] == "AwaitingInitialResponse")

    log("\nScenario 9 (continued) - nothing more happens to the resolved inquiry")
    i = find(s9)
    if i:
        before = detail(i["id"])
        time.sleep(130)
        after = detail(i["id"])
        check("S9 no reminders or escalation after resolution",
              after["stage"] == "Resolved" and after["episodeRemindersSent"] == before["episodeRemindersSent"] == 0
              and not any(f"Heater fault {RUN}" in s for s in inbox_subjects("manager@sawo.com")))

    passed = sum(1 for _, ok, _ in results if ok)
    log(f"\n{passed}/{len(results)} checks passed")
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
