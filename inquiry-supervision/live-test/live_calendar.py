"""
Live check of business-time deadlines, against the running iemas-inq stack.

1. Through the API (as the CMS does) set the default Reminder Policy's calendar: Mon-Fri 08:00-17:00 UTC,
   and today's date as a holiday when 'HOLIDAY_TODAY=1'.
2. Send an inquiry dated several hours ago, so that around the clock its 4-hour deadline would
   already have passed.
3. Wait through several real scheduler cycles: no deadline note, no reminder and no escalation may appear,
   because outside business hours (or on a holiday) the response clock has not started or is not running.
4. Then the control: restore the 24/7 calendar; the same inquiry's deadline must now be missed.

Run this on a weekday before 08:00 UTC, on a weekend, or with HOLIDAY_TODAY=1 on any weekday.
"""
import json
import os
import sys
import time
from datetime import datetime, timedelta, timezone

import live_e2e as base

HOLIDAY_TODAY = os.environ.get("HOLIDAY_TODAY") == "1"


def default_policy():
    return next(p for p in base.api("GET", "/reminder-policies") if p["isDefault"])


def save(policy, **changes):
    body = {
        "name": policy["name"], "description": policy["description"], "enabled": True, "isDefault": True,
        "classificationProfileId": policy["classificationProfileId"], "initialDelay": policy["initialDelay"],
        "reminderInterval": policy["reminderInterval"], "maxReminders": policy["maxReminders"], "minimumInterval": policy["minimumInterval"],
        "restrictToBusinessHours": policy["restrictToBusinessHours"], "businessHoursStart": policy["businessHoursStart"],
        "businessHoursEnd": policy["businessHoursEnd"], "excludeWeekends": policy["excludeWeekends"], "timeZoneId": policy["timeZoneId"],
        "expirationWindow": policy["expirationWindow"], "escalationThresholdReminderCount": policy["escalationThresholdReminderCount"],
        "holidays": [{"id": h["id"], "date": h["date"], "label": h["label"]} for h in policy["holidays"]],
    }
    body.update(changes)
    base.api("PUT", f"/reminder-policies/{policy['id']}", body)


def events(case_id):
    # supervision notices are on the case timeline
    return [e for e in base.api("GET", f"/cases/{case_id}")["history"]]


def main():
    base.token = base.api("POST", "/auth/login", {"email": base.ADMIN_EMAIL, "password": base.ADMIN_PASSWORD})["accessToken"]
    now = datetime.now(timezone.utc)
    policy = default_policy()
    original = {k: policy[k] for k in ("restrictToBusinessHours", "businessHoursStart", "businessHoursEnd", "excludeWeekends", "timeZoneId", "holidays")}
    holidays = [{"id": "00000000-0000-0000-0000-000000000000", "date": now.date().isoformat(), "label": "live test"}] if HOLIDAY_TODAY else policy["holidays"]
    save(policy, restrictToBusinessHours=True, businessHoursStart="08:00:00", businessHoursEnd="17:00:00", excludeWeekends=True, timeZoneId="UTC", holidays=holidays)
    base.log(f"Calendar set: Mon-Fri 08:00-17:00 UTC{' + today as a holiday' if HOLIDAY_TODAY else ''}; now {now:%a %H:%M} UTC")
    try:
        agent = "agent2@sawo.com"
        subj = f"Calendar check {base.RUN}"
        base.smtp_send(f"hal.{base.RUN}@customer.example", agent, subj, "I would like to request a quotation for two heaters. How much?", now - timedelta(hours=5), f"<cal{base.RUN}@customer.example>")
        i = base.wait_for("inquiry created", lambda: base.find(subj), timeout=240)
        base.check("inquiry created", i is not None)
        if not i:
            sys.exit(1)

        base.log("Waiting through 4 scheduler cycles with the business calendar on (4 minutes)...")
        time.sleep(240)
        d = base.detail(i["id"])
        case = base.api("GET", f"/cases/{i['id']}")
        notes = [e for e in case["history"] if "Deadline missed" in e["detail"] or "Supervision notice" in e["detail"]]
        base.check("no deadline missed / reminder while the business clock is not running", d["episodeRemindersSent"] == 0 and not notes, f"reminders={d['episodeRemindersSent']} notes={len(notes)}")
        base.check("still awaiting the initial response", d["stage"] == "AwaitingInitialResponse")
    finally:
        save(default_policy(), **original)
        base.log("Calendar restored (24/7).")

    base.log("Control: with the 24/7 calendar the same inquiry is already late...")
    ok = base.wait_for("deadline missed with 24/7 calendar", lambda: (lambda c: any("Deadline missed" in e["detail"] for e in c["history"]))(base.api("GET", f"/cases/{i['id']}")), timeout=240, every=10)
    base.check("control: deadline missed once the calendar is round-the-clock", bool(ok))

    passed = sum(1 for _, ok_, _ in base.results if ok_)
    base.log(f"\n{passed}/{len(base.results)} checks passed")
    sys.exit(0 if passed == len(base.results) else 1)


if __name__ == "__main__":
    main()
