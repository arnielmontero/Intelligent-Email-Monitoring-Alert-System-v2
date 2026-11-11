"""
Real-AI verification of the resolution assessment (Phase 5), against the running iemas-inq stack.

Needs a real OpenRouter API key. Either enter it in the CMS first (AI Models -> OpenRouter connection),
or pass it once in the environment as OPENROUTER_API_KEY_TEST; the script then stores it through the
same API the CMS uses (encrypted at rest, never returned again). Without a key the script stops.

Five situations, each sent as real email through the GreenMail test server:
  A  the agent only acknowledges            -> expect AcknowledgedOnly, NOT Potentially resolved
  B  the agent gives a partial response     -> expect InProgress (or Acknowledged), NOT Potentially resolved
  C  the agent gives a full solution and the customer confirms it works -> expect AppearsResolved => Potentially resolved
  D  the customer asks a new question after the agent's answer          -> expect CustomerWaiting, agent owes a reply
  E  ambiguous                                                          -> any verdict is acceptable, but never closed

Hard requirement checked in every case: the AI never moves an inquiry to Resolved or Closed.
Verdicts are model output, so a mismatch with the expectation is reported as 'REVIEW', not hidden;
only the safety rules (never closed, never exposes the key) are hard failures.
Prints stage, verdict and confidence only - never the email text.
"""
import json
import os
import sys
import time
import urllib.parse
from datetime import datetime, timedelta, timezone

import live_e2e as base  # the same mail/API helpers

CASES = {
    "A": dict(
        who="ai.a@customer.example",
        inquiry="My heater shows error E3 and will not start. Can you help me?",
        agent=["We have received your message and will look into it."],
        customer_after=None, expect={"AcknowledgedOnly", "InProgress"}, expect_stage={"InitialResponseReceived"},
    ),
    "B": dict(
        who="ai.b@customer.example",
        inquiry="My heater shows error E3 and will not start. Can you help me?",
        agent=["E3 is usually the temperature sensor. I am checking which sensor your model uses and will come back to you with the exact steps."],
        customer_after=None, expect={"InProgress", "AcknowledgedOnly"}, expect_stage={"InitialResponseReceived"},
    ),
    "C": dict(
        who="ai.c@customer.example",
        inquiry="My heater shows error E3 and will not start. Can you help me?",
        agent=["E3 means the sensor lost contact. Switch the heater off for 10 minutes, then reseat the grey sensor plug behind the panel and switch it on again. That clears the error."],
        customer_after="I did exactly that and it works again. Thank you very much, problem solved!",
        expect={"AppearsResolved"}, expect_stage={"PotentiallyResolved"},
    ),
    "D": dict(
        who="ai.d@customer.example",
        inquiry="My heater shows error E3 and will not start. Can you help me?",
        agent=["Please reseat the grey sensor plug behind the panel and restart it."],
        customer_after="I reseated it, but now it shows error E7 instead. What does E7 mean and what should I do next?",
        expect={"CustomerWaiting"}, expect_stage={"CustomerFollowUpReceived"},
    ),
    "E": dict(
        who="ai.e@customer.example",
        inquiry="My heater shows error E3 and will not start. Can you help me?",
        agent=["Please reseat the grey sensor plug behind the panel and restart it."],
        customer_after="Ok. I will see.",
        expect=None, expect_stage=None,
    ),
}


def main():
    base.log("Real-AI resolution assessment test")
    key = os.environ.get("OPENROUTER_API_KEY_TEST", "")
    base.wait_for("API", lambda: base.urllib.request.urlopen(base.API.replace("/api/v1", "") + "/health/live", timeout=5).status == 200, timeout=120, every=5)
    base.token = base.api("POST", "/auth/login", {"email": base.ADMIN_EMAIL, "password": base.ADMIN_PASSWORD})["accessToken"]

    if key:
        base.api("PUT", "/ai-models/provider", {"baseUrl": None, "apiKey": key, "clearApiKey": False})
    provider = base.api("GET", "/ai-models/provider")
    base.check("API key is configured (hint only, never the key)", provider["hasApiKey"], f"source={provider['keySource']} hint=...{provider.get('apiKeyHint')}")
    if not provider["hasApiKey"]:
        base.log("\nNo OpenRouter API key. Enter one in the CMS (AI Models) or set OPENROUTER_API_KEY_TEST, then run again.")
        sys.exit(2)
    test = base.api("POST", "/ai-models/provider/test")
    base.check("OpenRouter connection test", test.get("succeeded", test.get("Succeeded")), str(test.get("message", ""))[:120])

    run = base.RUN
    now = datetime.now(timezone.utc)
    agent = "agent1@sawo.com"
    base.imap(agent).logout()
    sent = {}
    n = 100
    for tag, c in CASES.items():
        n += 10
        subj = f"AI test {tag} {run}"
        first = f"<ai{run}.{tag}.1@customer.example>"
        base.smtp_send(c["who"], agent, subj, c["inquiry"], now - timedelta(minutes=40), first)
        prev, refs = first, first
        for i, text in enumerate(c["agent"], start=2):
            mid = f"<ai{run}.{tag}.{i}@sawo.com>"
            base.agent_sent(agent, c["who"], f"Re: {subj}", text, now - timedelta(minutes=30), mid, prev, refs)
            prev, refs = mid, f"{refs} {mid}"
        sent[tag] = (subj, prev, refs)

    # Wait for the inquiries and the agent replies, then send the customers' later messages in-thread.
    for tag, c in CASES.items():
        subj, prev, refs = sent[tag]
        base.wait_for(f"{tag} agent reply", base.stage_is(subj, "InitialResponseReceived"), timeout=720)
        if c["customer_after"]:
            base.smtp_send(c["who"], agent, f"Re: {subj}", c["customer_after"], datetime.now(timezone.utc), f"<ai{run}.{tag}.9@customer.example>", prev, refs)

    base.log("\nWaiting for the AI assessment of each conversation (real model calls)...")
    review = 0
    for tag, c in CASES.items():
        subj = sent[tag][0]
        got = base.wait_for(f"{tag} assessed", lambda: (lambda i: i if i and i["aiVerdict"] else None)(base.find(subj)), timeout=600, every=10)
        base.check(f"{tag}: the AI assessed the conversation", got is not None)
        if not got:
            continue
        detail = base.detail(got["id"])
        stage, verdict, conf = detail["stage"], detail["aiVerdict"], detail["aiConfidence"]
        base.log(f"      {tag}: stage={stage} verdict={verdict} confidence={conf:.2f}")
        base.check(f"{tag}: AI never closed the inquiry (stage is not Resolved/Closed)", stage not in ("Resolved", "Closed"), stage)
        if c["expect"] is not None:
            ok = verdict in c["expect"] and stage in c["expect_stage"]
            if not ok:
                review += 1
            base.log(f"      {tag}: {'as expected' if ok else 'REVIEW: expected ' + '/'.join(sorted(c['expect'])) + ' in ' + '/'.join(sorted(c['expect_stage']))}")

    base.log("\nA 'Potentially resolved' inquiry still needs the agent: confirming it ends supervision.")
    c_inq = base.find(sent["C"][0])
    if c_inq and base.detail(c_inq["id"])["stage"] == "PotentiallyResolved":
        base.api("POST", f"/inquiries/{c_inq['id']}/confirm-resolved", {"comment": "live AI test"})
        base.check("C: agent confirmation moves it to Resolved", base.detail(c_inq["id"])["stage"] == "Resolved")

    passed = sum(1 for _, ok, _ in base.results if ok)
    base.log(f"\n{passed}/{len(base.results)} hard checks passed; {review} verdict(s) differed from the expectation and need a human look.")
    sys.exit(0 if passed == len(base.results) else 1)


if __name__ == "__main__":
    main()
