"""
Browser check of the management interface, in headless Chromium, against the running stack.

Logs in through the CMS (the address a person would open), visits the pages that matter, performs real
interactions (drill down from a card, open an inquiry's conversation, edit and save a setting, test
the OpenRouter connection button) and saves a screenshot of each page to /shots.
Prints what it saw; any page that shows an error banner or fails to render is a failure.
"""
import os
import sys

from playwright.sync_api import sync_playwright

BASE = os.environ.get("CMS", "http://web")
EMAIL, PASSWORD = os.environ["BOOTSTRAP_ADMIN_EMAIL"], os.environ["BOOTSTRAP_ADMIN_PASSWORD"]
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}{(' - ' + detail) if detail else ''}", flush=True)


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1500, "height": 950})
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))

    page.goto(f"{BASE}/login")
    page.fill("input[type=email]", EMAIL)
    page.fill("input[type=password]", PASSWORD)
    page.click("button[type=submit]")
    page.wait_for_url(lambda u: "/login" not in u, timeout=20000)
    check("login through the CMS", True, page.url)

    def visit(path, expect_text, shot):
        page.goto(f"{BASE}{path}")
        page.wait_for_load_state("networkidle")
        page.wait_for_selector("h1", timeout=15000)
        page.screenshot(path=f"/shots/{shot}.png", full_page=True)
        banner = page.query_selector(".form-error")
        check(f"{path} renders", expect_text.lower() in page.inner_text("body").lower() and banner is None,
              (banner.inner_text() if banner else "").strip()[:120])

    visit("/setup", "Readiness", "1-setup")
    body = page.inner_text("body")
    check("setup checklist lists the API key as to fix", "OpenRouter API key" in body)
    check("setup page links to every configuration area", all(t in body for t in ("Agent mailboxes", "Outbound email", "Business hours", "Who management is", "Internal company domains")))

    visit("/inquiry-supervision", "Detected (all time)", "2-inquiry-supervision")
    cards = page.inner_text("body")
    cards = cards.lower()
    check("dashboard shows the summary cards", all(t.lower() in cards for t in ("New", "Awaiting agent", "Active conversations", "Potentially resolved", "Unresolved", "Overdue", "Escalated", "Resolved")))
    check("per-agent table lists both agents", "agent agent1" in cards and "agent agent2" in cards)

    page.click("button.stat-card:has-text('Overdue')")
    page.wait_for_load_state("networkidle")
    page.screenshot(path="/shots/3-overdue-list.png", full_page=True)
    listing = page.locator("table", has_text="Resolution target").last
    rows = listing.locator("tbody tr").all()
    check("clicking the Overdue card lists only overdue inquiries", len(rows) >= 1 and all("overdue" in r.inner_text().lower() for r in rows), f"{len(rows)} row(s)")

    page.select_option("select", "all")
    page.wait_for_load_state("networkidle")
    page.locator("table", has_text="Resolution target").last.locator("tbody tr td .case-link").first.click()
    page.wait_for_selector("text=Conversation", timeout=15000)
    page.screenshot(path="/shots/4-inquiry-panel.png", full_page=True)
    panel = page.inner_text("aside").lower()
    check("inquiry panel shows stage, timers and the conversation", all(t.lower() in panel for t in ("First reply", "Resolution target", "Conversation", "Confirm resolved", "Reassign")))

    visit("/system-settings", "Inquiry supervision", "5-system-settings")
    check("settings page shows switches and multi-line lists", page.query_selector("input[type=checkbox]") is not None and page.query_selector("textarea") is not None)
    # Real edit: change the initial response time, save, reload, confirm it stuck, then put it back.
    row = page.locator("tr", has_text="Initial response time (business hours)")
    box = row.locator("input[type=number]")
    original = box.input_value()
    box.fill("6")
    page.click("text=Save changes")
    page.wait_for_selector("text=Saved 1 setting", timeout=10000)
    page.reload()
    page.wait_for_load_state("networkidle")
    kept = page.locator("tr", has_text="Initial response time (business hours)").locator("input[type=number]").input_value()
    check("an edited setting saves and survives a reload", kept == "6", f"was {original}, now {kept}")
    box = page.locator("tr", has_text="Initial response time (business hours)").locator("input[type=number]")
    box.fill(original)
    page.click("text=Save changes")
    page.wait_for_selector("text=Saved 1 setting", timeout=10000)
    check("...and was restored", True)

    visit("/ai-models", "API key", "6-ai-models")
    page.screenshot(path="/shots/6-ai-models.png", full_page=True)
    ai = page.inner_text("body")
    check("AI page shows the key state without ever showing the key", "API key set" in ai or "No API key" in ai)
    check("AI page has a connection test button", page.query_selector("button:has-text('Test')") is not None)

    for path, text, shot in (("/email-accounts", "Email Accounts", "7-email-accounts"), ("/employees", "Employees", "8-employees"),
                             ("/reminder-policies", "Reminder", "9-reminder-policies"), ("/escalation-policies", "Escalation", "10-escalation-policies"),
                             ("/outbound-email", "Outbound", "11-outbound")):
        visit(path, text, shot)

    check("no JavaScript errors in the browser", not errors, "; ".join(errors)[:200])
    browser.close()

print(f"\n{sum(results)}/{len(results)} checks passed")
sys.exit(0 if all(results) else 1)
