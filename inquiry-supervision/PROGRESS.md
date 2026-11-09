# Inquiry Supervision: Progress Log

**Branch:** `feature/inquiry-supervision`
**Started:** 2026-11-09
**Goal:** Turn IEMAS from "detect email, notify, check for a first reply" into a full customer-inquiry supervisor. It follows every inquiry until it is confirmed resolved, reminds the agent when the inquiry is unanswered or idle, and escalates neglected inquiries to management.

This file is the single record of progress for this work. Update it at the end of every session. Tick a task only after it has been verified, and say how it was verified.

---

## 1. How this is kept separate from the current system

| | Current system (`main`) | Inquiry Supervision (this branch) |
|---|---|---|
| Code | `main` branch | `feature/inquiry-supervision` branch (same codebase, extended) |
| Compose file | `docker-compose.yml` (repo root) | `inquiry-supervision/docker-compose.yml` |
| Compose project | default (`iemas…` containers) | `iemas-inq` |
| Database | volume `iemas_pgdata`, DB `iemas` | volume `inq_pgdata`, DB `iemas_inq` |
| Ports | Postgres 5432, API 8090, CMS 8091, pgAdmin 5050 | Postgres 5433, API 8190, CMS 8191, pgAdmin 5150 |
| Env file | `.env` | `inquiry-supervision/.env` (copy from `.env.example`) |

**Run it (from the repository root, with this branch checked out):**

```powershell
copy inquiry-supervision\.env.example inquiry-supervision\.env   # then fill in secrets
docker compose -f inquiry-supervision/docker-compose.yml --env-file inquiry-supervision/.env up -d --build
```

**Stop it** (does not affect the current system):

```powershell
docker compose -f inquiry-supervision/docker-compose.yml --env-file inquiry-supervision/.env down
```

> **Caution:** Docker builds from whichever branch is checked out. Switch back to `main` before rebuilding the current system.
> Both stacks can read the same mailboxes, since reads are read-only. However, each stack sends its own Agent pop-ups and escalation emails. Point this stack at test mailboxes and test manager addresses until it replaces the current system.

**Why the same codebase and not a new project:** The requirements say to work with the existing project. They also say not to introduce a different architecture. IEMAS already does mailbox reading, filtering, AI classification, Cases, reply checking, reminders, escalation, the Windows Agent and the CMS. This branch extends those pieces and leaves `main` untouched.

---

## 2. Diagram → existing components

| Diagram box | Existing component | State |
|---|---|---|
| Server (deployed email reader) | ASP.NET API + Hangfire jobs (`server/src/Iemas.Api/Program.cs`) | Exists |
| Listens to registered inboxes | `EmailIntakeService` + IMAP / Microsoft Graph adapters, every minute | Exists |
| Registered emails Agent1/2/3 | `EmailAccount.OwnerEmployeeId` → `Employee`; Windows Agent per employee | Exists |
| Zettelkasten filter (no AI) | `DeterministicEmailFilter` + "Ignored senders" setting | **Partial**: it can only exclude. See gap G1. |
| If not sure → AI | `EmailClassificationService` + OpenRouter provider | Exists. Today it also receives the emails that are not uncertain. |
| If inquiry → flag/notify assigned agent | `CaseWorkflowService` → `NotificationService` → SignalR → Windows Agent pop-up | Exists |
| Make sure agent replies; monitor exchange till resolved; AI may judge resolution | `ReplyVerificationService` (Sent folder) | **Partial**: supervision stops at the first reply. See G2–G5. |
| Stuck/idle → flag again → later escalate to higher-ups | `ReminderSchedulingService` / `ReminderExecutionService` / `EscalationService` | **Partial**: only the "no first reply" case is handled. See G2 and G3. |

---

## 3. Assessment of the current system (verified by reading the code, 2026-11-09)

### 3.1 What already matches the requirements

- **Module 1/2: monitoring and ownership.** Each `EmailAccount` has an owner `Employee`. A Case takes its owner from the account when the Case is created (`CaseWorkflowService.cs:416`). Inboxes are polled every minute, whether or not the agent's mail client is open.
- **Module 4: AI classification.** The AI returns relevance, category, priority, confidence and a reason. `ClassificationDecisionPolicy` turns that into Important / NotImportant / ReviewRequired. Low-confidence results go to review instead of being discarded. A circuit breaker and usage tracking are in place.
- **Module 5: records and notification.** Cases have a number, customer, owner, subject, dates, statuses and an append-only `CaseEvent` history. Agent pop-ups include missed-pop-up delivery.
- **Module 7: one inquiry per conversation.** `CaseMatchingService` matches by thread id, In-Reply-To, References, then participants, with subject as a weak signal only. A customer follow-up that joins a Case resets it to ActionRequired / AwaitingReply. A follow-up on a completed Case reopens it (`CaseWorkflowService.cs:314-343`).
- **Module 6: response monitoring.** The agent's Sent folder is checked against the Case's message identifiers, not the subject. Each check is recorded.
- **Module 10/11: reminders and escalation.** Configurable policies, a check of the Case's current state before every send, a maximum-reminders limit, cooldowns, escalation levels, and recipients resolved from supervisor / department manager / group. Every attempt is recorded.
- **Business rule 15.** The AI never completes a Case. Completion is manual and needs a reason.

### 3.2 Gaps and weaknesses (what blocks the requirements)

| # | Gap | Evidence | Breaks |
|---|---|---|---|
| **G1** | The deterministic filter never decides "this is an inquiry". It only excludes. Every email that is not excluded goes to the AI, so AI cost scales with all mail. It has no scoring, no phrase patterns, and no use of previously confirmed inquiries. | `DeterministicEmailFilter.cs:24-55` | Module 3, rule 4 |
| **G2** | **A first reply ends supervision.** Once `ReplyStatus == Replied`, reminders are cancelled and escalation is skipped. The Case moves to InProgress and nothing watches it afterwards. An acknowledgment ("we'll look into it") silently counts as the end. | `ReminderExecutionService.cs:269`, `EscalationService.cs:112, 242`, `ReplyVerificationService.cs:357-359` | Module 8, 9B, 9C; rules 6, 7, 10 |
| **G3** | No idle/inactivity or overall-resolution deadline. Waiting states (WaitingForCustomer/Internal/Approval) cancel reminders with no expiry, so a Case can wait forever. | `ReminderExecutionService.cs:273` | Module 9, 10 |
| **G4** | Follow-ups join a Case only if the AI classifies them as Important. A short "any update?" or "thanks, but it still fails" on an existing thread can be marked NotImportant and never reach the Case. | `CaseWorkflowService.cs:220-226` | Module 7, rules 8, 9 |
| **G5** | The agent's outgoing messages are not stored as part of the conversation. The Sent folder is checked only to answer "is there a reply". The system therefore cannot tell who spoke last, cannot show the full exchange, and cannot give an AI the conversation to judge resolution. | `ReplyVerificationAttempt.MatchedSentMessageId` is the only trace | Module 7, 8 |
| **G6** | No "potentially resolved" state and no assessment of whether the inquiry is resolved. Completion is manual only. Nothing proposes "this looks resolved, please confirm". | `CaseEnums.cs` | Module 8 |
| **G7** | No reassignment of a Case to another agent. Ownership is fixed at creation. | no reassign path in `Application/` | Instruction 14 |
| **G8** | Escalation email content has not yet been checked against the required fields: customer, subject, date, agent, status, replied?, last activity, idle duration, prior reminders, reason. | to verify | Module 11 |
| **G10** | **Escalations never email management.** `EscalationService` records an `EscalationEvent` and sends the *agent* a pop-up. Nothing sends an email to the supervisor or manager. `SmtpEmailProviderAdapter` can only test a connection. | `EscalationService.cs` class doc, `SmtpEmailProviderAdapter.cs` | Module 11 |
| **G9** | Management view: `DashboardService` / `CaseWorkflowOverviewDto` count by work status. There is no per-agent breakdown by unanswered / idle / awaiting customer / potentially resolved. | `CaseWorkflowService.cs:147-158` | Section 7 |

---

## 4. Implementation plan and task log

Legend: `[ ]` not started · `[~]` in progress · `[x]` done and verified (note how)

### Phase 0: Setup
- [x] Create branch `feature/inquiry-supervision` from `main` (2026-11-09)
- [x] Create the `inquiry-supervision/` folder, this log, a separate `docker-compose.yml`, `.env.example` and pgAdmin registration (2026-11-09)
- [ ] First build and run of the separate stack. Confirm it starts next to the current stack with no port, container or volume clashes.

### Phase 1: Zettelkasten filter v2 (G1). DONE 2026-11-09
- [x] Three-way result: **Inquiry** (confident, no AI) / **NotInquiry** (confident, no AI) / **Uncertain** (send to AI). See `ZettelkastenFilter.cs`. Verified by `ZettelkastenFilterTests`.
- [x] Weighted signals:
  - inquiry phrases +2 each, capped at +6
  - a question mark +1
  - a known customer +2
  - profile include terms +1 each, capped at +2
  - not-inquiry phrases −3 each
  - automated sender −4
  - a profile exclude rule is still decisive, as before
  - an internal domain or one of our own mailboxes is always NotInquiry (Q3)
- [x] A confident Inquiry needs score ≥ 5 **and** no negative signal. Mixed signals always go to the AI, because context matters.
- [x] Every phrase list, the internal domains (default `sawo.com`) and both thresholds are CMS settings (System Settings → "Inquiry detection")
- [x] The score, matched signals and verdict are written to `EmailClassification.DeterministicFilterReason` and to the AI classification log
- [x] Learn from history: "sender has made inquiries before" adds +2. Cases closed as *Incorrect classification* are excluded.
- [x] Tests:
  - a clear inquiry skips the AI
  - a newsletter or payment notice skips the AI
  - an ambiguous email goes to the AI
  - internal and own-mailbox senders never start an inquiry
  - the internal domain list is configurable

  Full suite: 510 passed. The baseline was 496.

### Phase 2: Full conversation tracking (G4, G5)
- [ ] An inbound message that matches an **open** Case by thread headers joins that Case whatever its classification. The classification is still recorded.
- [ ] Store the agent's matching Sent messages as outbound conversation messages on the Case, with direction, time and sender
- [ ] Case fields: `LastCustomerMessageAt`, `LastAgentMessageAt`, `AwaitingSide` (Agent / Customer / Internal / None)
- [ ] Never create a duplicate Case for replies in the same conversation (regression test)

### Phase 3: Inquiry lifecycle states (G6)
- [ ] Map the required states onto the Case: Awaiting agent response · Agent responded / Awaiting customer · Ongoing · Awaiting internal action · **Potentially resolved** (new) · Confirmed resolved (Completed + reason) · Overdue · Escalated
- [ ] A state-transition table in code with a single place that decides transitions, and tests for each transition
- [ ] CMS and Windows Agent show the new state

### Phase 4: Supervise after the first reply (G2, G3)
- [ ] Reminder and escalation rules are driven by the lifecycle state and the side expected to act next, not by `ReplyStatus == Replied`
- [ ] Three separate time limits per policy: **first-response**, **idle** (no progress while the agent is expected to act), **resolution** (total open age)
- [ ] Waiting-for-customer and waiting-for-internal periods get a maximum. After it expires, the agent is asked for an update. Escalation follows only if the agent still does nothing.
- [ ] A customer reply returns the Case to "awaiting agent" and restarts the response timer
- [ ] Reminders and escalation stop when their trigger no longer applies, as checked on every run

### Phase 5: AI resolution assessment (Module 8)
- [ ] When the conversation changes, an optional AI call assesses the thread: Acknowledged only / In progress / Appears resolved, with confidence and a reason
- [ ] The AI can only move a Case to **Potentially resolved**. It can never complete it.
- [ ] The agent confirms or rejects in the Windows Agent or CMS. Rejection returns the Case to active.
- [ ] Optional business rule (needs a decision, see Q2): auto-confirm after N days of customer silence while the Case is potentially resolved

### Phase 6: Escalation content (G8)
- [ ] Check the escalation email and add any missing required fields
- [ ] Each escalation states which time limit was breached and by how much

### Phase 7: Reassignment (G7)
- [ ] Reassign a Case to another employee from the CMS. Recorded in history. Pending reminders move to the new owner. Escalation goes to the new owner's chain.

### Phase 8: Management visibility (G9)
- [ ] Dashboard by agent: unanswered · awaiting customer · idle · potentially resolved · overdue · escalated · resolved
- [ ] Case timeline shows inbound and outbound messages, reminders, escalations and state changes in one view

### Phase 9: Verification: the 10 required scenarios
Each scenario gets an automated test and a live run on the separate stack.

| # | Scenario | Test | Live |
|---|---|---|---|
| 1 | Legitimate inquiry → Case, owner, notification | [ ] | [ ] |
| 2 | Non-inquiry email → no Case | [ ] | [ ] |
| 3 | Uncertain email → AI → classified | [ ] | [ ] |
| 4 | Agent never replies → reminder after the time limit | [ ] | [ ] |
| 5 | Agent acknowledges only → Case stays open and supervised | [ ] | [ ] |
| 6 | Customer follow-up → joins the same Case | [ ] | [ ] |
| 7 | Conversation goes idle → idle reminder | [ ] | [ ] |
| 8 | Escalation threshold exceeded → manager email with full context | [ ] | [ ] |
| 9 | Resolved → reminders and escalation stop | [ ] | [ ] |
| 10 | Several agents at once → ownership never crosses | [ ] | [ ] |

---

## 5. Answered questions (decided by the product owner, 2026-11-09)

**Overriding rule:** the first agent reply must **never** end supervision. An inquiry stops being supervised only in a legitimate terminal state: confirmed resolved, or closed for a stated reason.

### Q1: Time limits (all configurable, none hardcoded)
| Timer | Default | Clock |
|---|---|---|
| Initial agent response | 4 h | business hours |
| 1st reminder | 2 h after the response deadline passes | business hours |
| 2nd reminder | 2 h after the 1st reminder | business hours |
| Management escalation | after the 2nd reminder, if still unanswered | business hours |
| Conversation idle | 1 business day without meaningful progress | business hours |
| Waiting for customer | 3 days, then notify the agent that a follow-up may be needed | calendar days |
| Resolution target | 5 business days. It marks the inquiry **overdue**. It never closes it. | business days |

- Business time skips weekends and configured holidays.
- The four timers are separate: initial response, inactivity, customer wait, and overall resolution.
- The first reply stops only the initial-response timer.

### Q2: Resolution confirmation (hybrid, agent is the final authority)
- The AI reads the whole conversation. It can only set **Potentially resolved**. It never closes an inquiry.
- The agent explicitly confirms, which sets **Resolved** and stops all reminders and escalation. The agent can also reject, which returns the inquiry to active supervision.
- **No auto-close** by default. If the customer is silent for 3 calendar days while the inquiry is potentially resolved, the agent is notified that a follow-up may be appropriate.

### Q3: Internal domains
- A configurable list, defaulting to `sawo.com`.
- Internal email never *starts* a new inquiry.
- Internal email that belongs to an existing inquiry stays linked to it, for example an agent forwarding the concern to another department. It never interrupts monitoring.
- Outgoing agent replies are never excluded because the agent's domain is internal.

### Q4: Full agent reply text is stored, with safeguards
- Agent replies are stored with their full text, encrypted at rest (AES-GCM, same key as mailbox credentials).
- Access is restricted to roles that may view Cases.
- A retention period applies. After it, the stored text is erased once the inquiry has been closed for that long.
- Card-number-like and password-like strings are masked before storage.
- Attachments are recorded only as a count. Their content is never stored.

---

## 6. Decisions

| Date | Decision | Why |
|---|---|---|
| 2026-11-09 | Extend IEMAS on a branch and run it as a separate Compose project. Do not write a new codebase. | The requirements say to work with the existing project. Most modules already exist. A separate project name, ports and volumes keep the current system running and untouched. |
| 2026-11-09 | Q1–Q4 answered as recorded in §5. | Product owner decision. |
| 2026-11-09 | One new **Inquiry Supervision engine** owns automatic reminders and escalation for inquiries. It decides from the inquiry's lifecycle stage. It no longer uses "has a reply been found". | The old reminder and escalation engines stop at the first reply (G2). Patching their guards would leave two engines making overlapping decisions. The old engines stay in the code behind the `inquiry.supervision_enabled` setting. Employee "Remind me later" reminders still run through the existing reminder engine. |
| 2026-11-09 | Stage changes are timestamped with the time of the email that caused them, not the processing time. | Deadlines must reflect when the customer wrote or the agent replied, even if a job runs late. This also makes the whole lifecycle testable with an explicit clock. |
| 2026-11-09 | Escalation after the 2nd reminder fires one reminder interval (2 business hours) after that reminder. It does not fire at the same moment. | Escalating together with the 2nd reminder would give the agent no chance to act on it. The interval is configurable. |
| 2026-11-09 | Missing an overall resolution target marks the inquiry **overdue** and notifies the agent. Management is escalated for overdue only while the **agent** owes the next action. | Business rule: "A case should not be unnecessarily escalated simply because the customer has not replied." |
| 2026-11-09 | Business calendar (time zone, business hours, weekends, holidays) comes from the **default Reminder Policy**. | Holidays and business hours are already managed there in the CMS. A second calendar would drift from it. |
| 2026-11-09 | Management escalation emails are actually sent through the configured Outbound email account (new). | Found during this session: `EscalationService` only recorded the escalation and pinged the agent. No email ever reached management (G10). |

---

## 7. Session log

### 2026-11-09: Session 1 (setup + assessment)
- Read the existing system: domain enums, filter, classification, case matching, reply verification, reminders, escalation, scheduled jobs.
- Wrote the assessment (§3), with gaps G1–G9 and file/line evidence.
- Created branch `feature/inquiry-supervision`, folder `inquiry-supervision/`, separate Compose stack `iemas-inq`, `.env.example`, pgAdmin registration.
- Uncommitted changes from `main` (`.env.example`, `docker-compose.yml`, `EscalationPoliciesPage.tsx`, `deploy/pgadmin/`) were present in the working tree when the branch was created. They were **not** committed to this branch.
- **Next:** answer Q1–Q4, first build of the separate stack, then start Phase 1.
