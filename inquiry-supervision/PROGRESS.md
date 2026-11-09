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
| **G9** | Management view: `DashboardService` / `CaseWorkflowOverviewDto` count by work status. There is no per-agent breakdown by unanswered / idle / awaiting customer / potentially resolved. | `CaseWorkflowService.cs:147-158` | Section 7 |

---

## 4. Implementation plan and task log

Legend: `[ ]` not started · `[~]` in progress · `[x]` done and verified (note how)

### Phase 0: Setup
- [x] Create branch `feature/inquiry-supervision` from `main` (2026-11-09)
- [x] Create the `inquiry-supervision/` folder, this log, a separate `docker-compose.yml`, `.env.example` and pgAdmin registration (2026-11-09)
- [ ] First build and run of the separate stack. Confirm it starts next to the current stack with no port, container or volume clashes.

### Phase 1: Zettelkasten filter v2 (G1)
- [ ] Three-way result: **Inquiry** (confident, no AI) / **NotInquiry** (confident, no AI) / **Uncertain** (send to AI)
- [ ] Weighted signals: inquiry phrases ("request a quotation", "interested in", "how much", "price", …), exclude phrases, automated-sender patterns (no-reply, newsletters, payment notices), and internal company domains (sawo.com = not a new customer inquiry)
- [ ] Store the score, the matched terms and the decision on the classification record, so every decision can be explained
- [ ] Learn from history: senders and phrases from confirmed inquiries raise the score
- [ ] Tests: clear inquiry skips the AI, newsletter skips the AI, ambiguous email goes to the AI

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

## 5. Open questions (answers needed before the related phase)

- **Q1 (Phase 4): Default time limits.** Proposed: first response 4 business hours, reminder every 2 h, escalate after 2 reminders. Idle 1 business day. Maximum wait for the customer 3 days. Resolution 5 business days.
- **Q2 (Phase 5): What confirms a resolution?** Proposed: the agent confirms. Optionally, a Case that is potentially resolved auto-closes after 3 days of customer silence.
- **Q3 (Phase 1): Internal domains.** Is `sawo.com` the only one? Should internal senders be ignored entirely, or only never start a *new* inquiry, while still counting inside an existing thread?
- **Q4 (Phase 2): Storing outbound mail.** Is it acceptable to store the agent's reply bodies in the database for the conversation view and AI assessment, or only the headers and timestamps?

---

## 6. Decisions

| Date | Decision | Why |
|---|---|---|
| 2026-11-09 | Extend IEMAS on a branch and run it as a separate Compose project. Do not write a new codebase. | The requirements say to work with the existing project. Most modules already exist. A separate project name, ports and volumes keep the current system running and untouched. |

---

## 7. Session log

### 2026-11-09: Session 1 (setup + assessment)
- Read the existing system: domain enums, filter, classification, case matching, reply verification, reminders, escalation, scheduled jobs.
- Wrote the assessment (§3), with gaps G1–G9 and file/line evidence.
- Created branch `feature/inquiry-supervision`, folder `inquiry-supervision/`, separate Compose stack `iemas-inq`, `.env.example`, pgAdmin registration.
- Uncommitted changes from `main` (`.env.example`, `docker-compose.yml`, `EscalationPoliciesPage.tsx`, `deploy/pgadmin/`) were present in the working tree when the branch was created. They were **not** committed to this branch.
- **Next:** answer Q1–Q4, first build of the separate stack, then start Phase 1.
