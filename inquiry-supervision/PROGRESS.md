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

### Phase 2: Full conversation tracking (G4, G5). SERVER DONE 2026-11-09
- [x] A message in the thread of an existing inquiry is linked to it whatever it says. The match uses thread id, In-Reply-To or References, never subject. It is never re-screened, so a NotImportant verdict can't lose it.
  - Code: `ConversationThreadMatcher`. It is used in classification, in Case creation, and in intake, so an Ignored or internal sender inside a thread still reaches its inquiry.
  - Test: `S6_CustomerFollowUp_JoinsTheSameInquiry…`
- [x] The agent's Sent messages for every open inquiry are stored as outbound conversation messages (`ConversationSyncService`):
  - direction `Outbound`, status `Outbound`
  - role `Agent`, or `Internal` for internal-only forwards
  - body masked and encrypted (Q4)
  - unrelated sent mail is never stored

  This replaces the old first-reply-only check while supervision is on. Tests: S5, S10, Q3.
- [x] New Case fields:
  - `LastCustomerMessageAt`, `LastAgentMessageAt`, `LastInternalActivityAt`, `FirstAgentResponseAt`
  - `CaseEmail.Role` (Customer / Agent / Internal)

  Who owes the next step is derived from the stage: `InquiryLifecycle.AgentOwesNextStep` / `CustomerOwesNextStep`.
- [x] No duplicate Case for replies in the same conversation. Asserted in S6, S9c and Q3.
- [x] A customer's reply to the *agent's* message joins the inquiry, because outbound Message-IDs are now known.

### Phase 3: Inquiry lifecycle states (G6). SERVER DONE 2026-11-09
- [x] `InquiryStage` holds the required states:
  - Awaiting initial response
  - Initial response sent
  - Customer follow-up received
  - Agent follow-up sent
  - Awaiting customer
  - Awaiting internal action
  - Conversation inactive
  - **Potentially resolved**
  - Resolved
  - Closed

  Overdue and Escalated are flags shown alongside any stage: `OverdueSince` and `EpisodeEscalations`.
- [x] `InquiryLifecycle` is the single place that decides transitions. Every change is recorded on the Case timeline as `StageChanged`.
  - A message older than the current stage never rewinds it.
  - Only Resolved and Closed are terminal.
- [x] `CaseWorkStatus` is projected from the stage, so the existing CMS and Windows Agent keep working. `CaseWorkStatus.PotentiallyResolved` was added.
- [ ] CMS and Windows Agent show the new stage and the confirm/reject buttons. See Phase 8 and the Windows Agent task below.

### Phase 4: Supervise after the first reply (G2, G3). DONE 2026-11-09
- [x] New `InquirySupervisionService`, run every minute, with the pure, clock-explicit `SupervisionRules`. Reminders and escalation are decided from the stage and from who owes the next step, never from "a reply was found".
- [x] Separate timers, all configurable (System Settings → "Inquiry supervision"):
  - initial response: 4 business h
  - customer follow-up and inactive: 1 business day
  - customer wait: 3 calendar days
  - internal wait: 1 business day
  - resolution target: 5 business days
  - reminder interval: 2 business h, with 2 reminders before escalation
  - next escalation level: 1 business day later
- [x] Business time skips weekends and holidays, using the calendar of the default Reminder Policy (`BusinessCalendar`). Covered by `BusinessCalendarTests`.
- [x] When a wait expires, the agent is notified and the inquiry becomes Inactive. Escalation follows only if the agent still does nothing. Silence from the customer alone is never escalated (S7, S7b).
- [x] A customer reply returns the inquiry to the agent and restarts that timer (S6). Every stage change restarts the counters for the new stage.
- [x] Reminders and escalation stop as soon as their trigger no longer applies (S5, S9, S10). Pending older-engine reminders are cancelled on a stage change.
- [x] The resolution target marks the inquiry overdue and notifies the agent. Management is escalated for overdue only while the agent owes the next step (`ResolutionTarget_…` test).
- [x] The older reminder and escalation engines stay in the code. They run only when `inquiry.supervision_enabled` is off (`SupervisionOff_…` test). An agent's "Remind me later" still works after a reply; before, it was cancelled.

### Phase 5: AI resolution assessment (Module 8). SERVER DONE 2026-11-09
- [x] `ResolutionAssessmentService` + `OpenRouterResolutionAssessor`:
  - runs when an inquiry's conversation changes
  - verdicts: Acknowledged only / In progress / Customer waiting / Appears resolved, with a confidence and a reason
  - the prompt treats the conversation as untrusted data
  - each call is recorded under AI usage purpose `ResolutionAssessment`
- [x] The AI can only move an inquiry to **Potentially resolved**, at or above 75 % confidence (configurable), and the agent is asked to confirm. It never closes an inquiry (S9).
- [x] The agent confirms or rejects through the Windows Agent actions `ConfirmResolved` / `RejectResolution` or the CMS API. Rejecting returns the inquiry to its previous stage (S9b). A customer writing after resolution reopens it (S9c).
- [x] Auto-close is **not** implemented, per Q2. After 3 days of customer silence the agent is asked to review and confirm. Gentle reminders follow, with no escalation.

### Phase 6: Escalation content and delivery (G8, G10). DONE 2026-11-09
- [x] Management escalation emails are actually sent through the active Outbound (SMTP) account (`SmtpEscalationEmailSender`). Failed sends are retried up to 3 times. Delivery result and failure reason are stored on `EscalationEvent`.
- [x] Email content (`EscalationEmailComposer`):
  - customer
  - original subject and date
  - assigned agent
  - current status
  - whether the agent has replied
  - last activity, and by whom
  - how long it has been inactive and how long open
  - resolution target
  - previous reminders, with times
  - the reason, naming the timer that was breached

  The S4/S8 test asserts every field.
- [x] Each further escalation of the same inquiry goes one level higher, for example supervisor first, then director. Ownership never changes.

### Phase 7: Reassignment (G7). SERVER DONE 2026-11-09
- [x] `InquiryOverviewService.ReassignAsync` + `POST /api/v1/inquiries/{id}/reassign`:
  - recorded in history and in the audit log
  - timers continue, because a handover does not reset a deadline
  - later reminders and escalation follow the new owner (`Reassign_…` test)
- [ ] CMS button. See Phase 8.

### Phase 8: Management visibility (G9)
- [x] API `GET /api/v1/inquiries/overview`, with counts per agent and stage, overdue and escalated (`Overview_…` test)
- [x] API `GET /api/v1/inquiries?bucket=…`
- [x] API `GET /api/v1/inquiries/{id}`: the full conversation in both directions, with agent text decrypted. Supervisor or above only, and every view is written to the audit log.
- [ ] CMS page "Inquiry Supervision": dashboard by agent, lists by bucket, and an inquiry view with the conversation and confirm / reject / reassign
- [ ] Windows Agent: "Confirm resolved" / "Not resolved yet" buttons, and the new notification types

### Phase 9: Verification: the 10 required scenarios
Automated end-to-end tests: `server/tests/Iemas.Tests/Inquiries/InquiryLifecycleEndToEndTests.cs`. Each test runs the real pipeline:

intake → Zettelkasten/AI → Case → Sent-folder sync → supervision engine

It uses fake mailboxes and an explicit clock (Mon–Fri 08–17).

| # | Scenario | Test | Live |
|---|---|---|---|
| 1 | Legitimate inquiry → Case, owner, notification | [x] `S1_…` | [ ] |
| 2 | Non-inquiry email → no Case | [x] `S2_…` | [ ] |
| 3 | Uncertain email → AI → classified | [x] `S3_…` | [ ] |
| 4 | Agent never replies → reminders after the time limit | [x] `S4_S8_…` | [ ] |
| 5 | Agent acknowledges only → stays open and supervised | [x] `S5_…` | [ ] |
| 6 | Customer follow-up → joins the same inquiry | [x] `S6_…` | [ ] |
| 7 | Conversation goes idle → notice, update request, escalation if ignored | [x] `S7_…`, `S7b_…` | [ ] |
| 8 | Escalation threshold exceeded → manager email with full context | [x] `S4_S8_…` | [ ] |
| 9 | Resolved → reminders and escalation stop | [x] `S9_…`, `S9b_…`, `S9c_…` | [ ] |
| 10 | Several agents at once → ownership never crosses | [x] `S10_…` | [ ] |

Additional tests:
- Q3 internal forward and colleague reply
- waiting for internal action
- overdue without escalation while the customer owes the reply
- calendar
- reply safeguards
- supervision off
- remind-later after a reply
- reassignment
- overview

Full suite: **537 passed, 0 failed.**

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
