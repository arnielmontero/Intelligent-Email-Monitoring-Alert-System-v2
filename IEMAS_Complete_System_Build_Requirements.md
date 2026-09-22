# IEMAS — Complete System Build & Requirements Specification

**Project:** Intelligent Email Monitoring & Alert System (IEMAS)  
**Developed by:** Arniel D. Montero — Sr. Software Engineer  
**Document Version:** 1.0  
**Document Date:** 2026-09-21  
**Status:** Build Reference / Pre-Implementation Baseline

---

## 1. Document Purpose

This document is the master technical and functional reference for building IEMAS.

It consolidates the agreed requirements, architecture, workflows, security rules, server responsibilities, Windows Client Agent responsibilities, email monitoring, AI classification, Case/Work Topic management, reply verification, reminders, escalation, internal escalation email, synchronization, auditing, reliability, testing, and deployment requirements.

The goal is to use this document as the primary reference before and during implementation so that the system is built consistently and important requirements are not lost between development phases.

This document describes **what the system must do, how the major components interact, and the rules they must follow**. Detailed database column definitions, API schemas, SignalR message contracts, and state-machine diagrams should be generated from this baseline as implementation documents.

---

# 2. Product Definition

IEMAS is a centralized system for:

- Monitoring enrolled business email accounts.
- Reading incoming email without replying to customers.
- Understanding and classifying email using an LLM.
- Determining whether an email requires business attention.
- Associating relevant email with the responsible employee.
- Creating and maintaining Cases / Work Topics.
- Notifying employees through a Windows Client Agent.
- Verifying actual outgoing replies using mailbox/Sent Items data.
- Sending reminders when required work remains unresolved.
- Escalating unresolved Cases to supervisors/managers.
- Sending internal escalation emails through a dedicated outbound email account.
- Maintaining complete Case History and audit records.
- Providing administrators with CMS configuration and investigation tools.

## Absolute system boundary

> **IEMAS shall never send, reply to, forward, delete, or modify customer email.**

Employees continue using Outlook, Gmail, webmail, or another normal mail client to actually read and reply to customers.

IEMAS monitors and verifies those actions.

IEMAS may send **internal notification/escalation emails** to authorized supervisors, managers, or other internal recipients.

---

# 3. Core Principles

The following principles are mandatory architectural rules.

1. **Database is the source of truth.**
2. IEMAS is not an email client.
3. Customer email is read/monitored, not modified.
4. Employee ownership and Windows Agent identity are separate.
5. Agent IP address is metadata, not identity.
6. Employee claims are not proof of email activity.
7. Verified mailbox activity is authoritative for reply verification.
8. Work Status, Reply Status, and Notification Status are separate.
9. AI recommends; deterministic business rules control workflow.
10. Case History is append-only/auditable.
11. All background processing must be restart-safe.
12. Important processing must be idempotent.
13. SignalR is a real-time transport, not a source of truth.
14. Server-side credentials are never exposed to Windows Agents.
15. No arbitrary remote command execution is permitted.
16. Internal escalation email is separate from monitored email.
17. A read email is not automatically a completed Case.
18. A verified reply is not automatically proof that all work is complete.
19. Subject alone must never determine Case matching.
20. Scheduled jobs must re-check current state before performing actions.

---

# 4. Business Objective

The business problem addressed by IEMAS is that important customer/business emails can remain unanswered or unnoticed.

IEMAS provides a controlled process:

```text
Customer Email
      ↓
Monitor
      ↓
Understand / Classify
      ↓
Determine Relevance
      ↓
Create / Update Case
      ↓
Notify Responsible Employee
      ↓
Verify Actual Reply
      ↓
Reminder if Needed
      ↓
Overdue
      ↓
Escalation
      ↓
Supervisor / Manager Notification
```

The system is intended to reduce missed customer communication while preserving employee control over actual email responses.

---

# 5. High-Level Architecture

```text
                         ┌─────────────────────────────┐
                         │        IEMAS SERVER         │
                         │                             │
                         │ ASP.NET Core                │
                         │                             │
                         │ Web CMS                     │
                         │ REST API                    │
                         │ Authentication / RBAC       │
                         │ Email Monitoring            │
                         │ Email Provider Adapters     │
                         │ AI Classification           │
                         │ Case / Workflow Engine      │
                         │ Reply Verification          │
                         │ Notification Engine         │
                         │ Reminder Engine             │
                         │ Escalation Engine           │
                         │ Agent Management            │
                         │ Audit / History             │
                         │ Background Jobs             │
                         │ System Health               │
                         │                             │
                         │ PostgreSQL                  │
                         └──────────────┬──────────────┘
                                        │
                       ┌────────────────┴────────────────┐
                       │                                 │
                    SignalR                         Email Providers
                       │                                 │
              ┌────────┴────────┐                ┌───────┴────────┐
              │                 │                │                │
       Windows Agent      Windows Agent       Inbound          Outbound
              │                 │                │                │
              ▼                 ▼                ▼                ▼
          Employee A        Employee B       Customer       Supervisor /
          Windows PC        Windows PC       Mailboxes       Manager
```

## Recommended V1 architecture

Use a **modular monolith** rather than microservices.

The server may contain multiple logical modules, but they run as one application/deployment unit initially.

This simplifies:

- Transactions
- Debugging
- Deployment
- Database consistency
- Workflow coordination
- Local development
- Production support

The architecture should still maintain clean module boundaries so components can be separated later if required.

---

# 6. Recommended Technology Baseline

## Server

- ASP.NET Core / .NET
- REST API
- SignalR
- Background processing
- PostgreSQL

## Web CMS

One of:

- React / Next.js
- Blazor

The final UI framework should be explicitly selected before UI implementation.

## Windows Client Agent

- C# / .NET
- Windows desktop application
- SignalR client
- Windows toast notifications
- System tray support
- Secure local credential storage

## Deployment

- Docker
- Docker Compose for initial deployment
- HTTPS
- PostgreSQL persistent storage

## Optional later infrastructure

- Redis
- External queue
- Dedicated worker services
- Distributed deployment

These are not required for the initial modular-monolith implementation.

---

# 7. Server Modules

The server should be divided logically into:

```text
IEMAS Server
│
├── Authentication
├── Authorization / RBAC
├── Employee Management
├── Email Account Management
├── Outbound Email Management
├── Email Provider Adapters
├── Email Monitoring & Intake
├── Email Normalization
├── AI Provider Management
├── AI Classification
├── Classification Profiles
├── Case / Work Topic Management
├── Case Matching
├── Case Workflow
├── Reply Verification
├── Notification Management
├── Reminder Engine
├── Escalation Engine
├── Windows Agent Management
├── SignalR Hub
├── Case History
├── Employee Activity
├── Audit Logging
├── Background Jobs
├── Reconciliation
├── System Health
└── Maintenance / Emergency Controls
```

---

# 8. Windows Client Agent

The Client Agent is a controlled endpoint for employee notifications and Case actions.

It is **not** intended to replace Outlook/Gmail.

## 8.1 Agent responsibilities

The Agent must:

- Connect to the IEMAS server.
- Authenticate using a server-issued Agent identity/credential.
- Maintain SignalR connection.
- Send heartbeat information.
- Receive notifications.
- Display Windows toast notifications.
- Display Action Required Cases.
- Display Waiting Cases.
- Display History.
- Allow predefined Case actions.
- Allow employee comments/updates.
- Send actions to the server.
- Receive Case state updates.
- Synchronize after reconnect.
- Report technical state.
- Report version.
- Report errors.
- Open the normal email client/webmail when requested.

## 8.2 Agent does not

The Agent must not:

- Store mailbox passwords.
- Store OpenRouter/API keys.
- Send customer email.
- Read customer mailbox directly.
- Execute arbitrary server commands.
- Execute arbitrary PowerShell/command-line instructions.
- Become a full email client.

---

# 9. Windows Agent User Experience

The Agent should provide:

```text
IEMAS
├── Action Required (3)
├── Waiting (2)
├── History
├── Connection: Connected
├── Settings
└── Exit
```

Main window:

```text
IEMAS                                      John Smith
● Connected

ACTION REQUIRED                              3

ABC Trading
Request for Product Price
Received: 08:15 • No reply

[Open Case] [Add Update]

XYZ Corporation
Product Availability Inquiry
Received: 10:42 • No reply

[Open Case] [Add Update]

Waiting (2)          History          Settings
```

## Design principle

IEMAS should answer:

> What do I need to take care of?

The normal email application answers:

> Where do I read and reply to the customer?

---

# 10. Windows Integration

The Agent should support:

- Desktop shortcut
- Start Menu
- Taskbar
- Application icon
- System tray
- Windows toast notifications
- Main window
- Optional automatic startup
- Secure local credential storage
- Version information

System tray should display connection state.

---

# 11. Employee and Ownership Model

Employee and Agent must remain separate entities.

```text
Employee
├── Email Account(s)
└── Windows Agent(s)
```

Case ownership is employee-level.

Example:

```text
John Smith
├── sales@sawo.com
├── Office PC Agent
└── Laptop Agent
```

A Case must never become permanently owned by a computer.

---

# 12. Employee Organizational Structure

Employee data should support:

```text
Employee
├── Name
├── Email
├── Department
├── Supervisor
└── Manager
```

This allows escalation policies to resolve recipients dynamically.

Example:

```text
Case Owner
    ↓
Employee's Supervisor
    ↓
Supervisor Email
    ↓
Internal Escalation Email
```

This is preferable to hardcoding a supervisor email into every escalation policy.

---

# 13. Multiple Agents Per Employee

The architecture must support more than one Agent per employee.

Possible policy choices:

### Option A — All Active Agents

Every active Agent receives the notification.

### Option B — Primary Agent

Only the designated primary Agent receives notifications.

### Option C — Primary + Fallback

Primary receives notification.

If unavailable according to policy, fallback Agent receives it.

The exact V1 policy remains a business decision and must be configured before production freeze.

---

# 14. Monitored Email Accounts

IEMAS requires an enrolled mailbox before monitoring begins.

Example:

```text
Email Address:
arniel.montero@sawo.com

Protocol:
IMAP

Host:
mail.sawo.com

Port:
993

Encryption:
SSL/TLS

Username:
arniel.montero@sawo.com

Authentication:
Password / OAuth2 / App Password

Owner:
Arniel Montero

Classification Profile:
Sales

Monitoring:
Enabled
```

## 14.1 Email provider abstraction

Email provider-specific logic must be isolated behind adapters.

Potential provider capabilities:

- Inbox reading
- Sent Items reading
- Thread/conversation IDs
- Message IDs
- Message references
- OAuth
- IMAP
- Provider-specific APIs

The Workflow Engine must not contain provider-specific mailbox logic.

---

# 15. Email Authentication

Data model must support:

```text
PASSWORD
OAUTH2
APP_PASSWORD
```

Preferred authentication approach:

1. OAuth2/delegated access where supported.
2. App-specific password.
3. Dedicated mailbox credential.
4. Ordinary password where necessary.

The exact V1 provider/authentication combination remains a business decision.

---

# 16. Credential Security

## Monitored mailbox credentials

Password must be:

- Masked during input.
- Write-only after saving.
- Never displayed again.
- Never returned in API GET/list operations.
- Never included in logs.
- Never placed in URLs.
- Never placed in browser local storage.
- Never included in error messages.
- Never sent to Client Agent.
- Stored encrypted or in secure secrets storage.

API may return:

```json
{
  "hasCredential": true
}
```

but never:

```json
{
  "password": "..."
}
```

## Outbound credentials

Follow exactly the same protection rules.

## Secrets

Never expose:

- Mailbox passwords
- SMTP passwords
- OAuth access tokens
- OAuth refresh tokens
- AI provider keys
- Agent credentials
- Database passwords

---

# 17. Email Management CMS

Recommended navigation:

```text
EMAIL MANAGEMENT
├── Email Accounts
├── Outbound Email
├── Email Monitoring & Intake
└── Email Classification
```

---

# 18. Outbound Email Configuration

This configuration is specifically for internal IEMAS notification emails.

Example:

```text
Outbound Email Configuration

Enabled:              Yes

Provider:             SMTP

From Name:
IEMAS Notification System

From Email:
iemas-notification@sawo.com

SMTP Host:
smtp.sawo.com

Port:
587

Encryption:
STARTTLS

Username:
iemas-notification@sawo.com

Password:
••••••••••••••••

[Test Connection]
[Save]
```

## Rules

- Prefer a dedicated IEMAS notification mailbox.
- Do not use an employee's personal mailbox as the system notification account.
- Do not monitor the outbound mailbox as a customer mailbox.
- Do not send outbound credentials to Agents.
- Support provider abstraction.
- Support test connection.
- Never expose stored password.

---

# 19. Inbound and Outbound Email Separation

There are two distinct email systems.

## Inbound monitored email

Purpose:

- Read customer/business email.
- AI classification.
- Case creation.
- Reply verification.

## Outbound internal email

Purpose:

- Internal escalation.
- Internal notification to authorized supervisors/managers.

The two credentials and configurations must remain separate.

---

# 20. Email Monitoring Pipeline

The email processing pipeline is:

```text
Mailbox / Provider
       ↓
Provider Adapter
       ↓
Fetch Message
       ↓
Normalize Message
       ↓
Duplicate Check
       ↓
Persist Email
       ↓
AI Classification
       ↓
Case Matching
       ↓
Case Creation / Update
       ↓
Workflow Evaluation
       ↓
Notification / Reminder / Escalation
```

The processing pipeline must be restart-safe.

---

# 21. Email Data Model Requirements

Store enough normalized information for classification, Case matching, investigation, and reply verification.

Minimum information:

- Provider
- Email Account ID
- Provider Message ID
- Message ID
- Thread/Conversation ID
- In-Reply-To
- References
- Sender
- Recipients
- CC
- Subject
- Body/content
- Received timestamp
- Attachment metadata
- Processing status
- Classification
- AI confidence
- AI model/provider
- Case ID
- Processing errors

---

# 22. Duplicate Email Protection

Provider Message ID should be used for idempotency.

Example:

```text
Incoming Message ID
        ↓
Already processed?
   ┌────┴────┐
  YES        NO
   ↓          ↓
Ignore      Process
```

Duplicate processing must not create:

- Duplicate Case
- Duplicate notification
- Duplicate reminder
- Duplicate escalation
- Duplicate AI processing

---

# 23. AI Classification

The primary AI role is:

> Understand and classify incoming email.

The AI must not:

- Reply to customers.
- Send customer emails.
- Draft customer replies.
- Decide final workflow state independently.

The AI provides structured information to the workflow engine.

---

# 24. AI Classification Stages

## Stage 1 — Relevance

Possible values:

```text
RELEVANT
NOT_RELEVANT
UNCERTAIN
```

## Stage 2 — Category

Examples:

```text
PRODUCT_INQUIRY
PRICE_REQUEST
PRICE_LIST_REQUEST
QUOTATION
PRODUCT_AVAILABILITY
PURCHASE_DISCUSSION
PURCHASE_VERIFICATION
CUSTOMER_REQUIREMENT
CUSTOMER_SUPPORT
CUSTOMER_COMPLAINT
FOLLOW_UP
```

Additional categories can be configured.

---

# 25. AI Result

Example:

```json
{
  "relevant": true,
  "category": "PRODUCT_INQUIRY",
  "action_required": true,
  "response_expected": true,
  "priority": "HIGH",
  "confidence": 0.96,
  "summary": "Customer is requesting product pricing and availability."
}
```

Store:

- Provider
- Model
- Prompt/profile version
- Classification
- Confidence
- Summary
- Processing timestamp
- Processing duration
- Error if any

This allows later investigation.

---

# 26. AI Confidence Policy

Confidence thresholds must be configurable.

Conceptually:

```text
HIGH confidence
    ↓
Automatic processing

MEDIUM confidence
    ↓
Configurable behavior

LOW confidence
    ↓
REVIEW_REQUIRED
```

Exact thresholds must not be hardcoded into business logic.

---

# 27. Semantic Classification

Classification must consider the meaning of the email, not only keywords.

Example:

```text
Subject:
Lazada Product Inquiry

Body:
We sell your products through Lazada.
Please send your latest price list.
```

This can be relevant.

Whereas:

```text
Subject:
Your Lazada Order Has Shipped
```

is generally an automated e-commerce notification.

The AI should consider:

- Subject
- Body
- Sender
- Recipient
- Thread context
- Classification profile

---

# 28. Classification Profiles

CMS must provide CRUD for predefined classification profiles.

A profile contains:

- Profile name
- Description
- Enabled
- Categories
- Include definitions
- Exclude definitions
- Example subject
- Example content
- Expected classification
- Confidence behavior

Example Sales profile:

### Include

- Product Inquiry
- Price Request
- Quotation
- Product Availability
- Purchase Discussion
- Customer Requirement
- Follow-up

### Exclude

- Advertisement
- Newsletter
- E-commerce Notification
- Spam
- Software Advertisement

---

# 29. Classification Testing

CMS should allow:

```text
Test Classification

Subject:
Request for ABC Product Pricing

Content:
Customer wants 50 units and requests
latest price and availability.

[Test]
```

Result:

```text
Relevant: YES
Category: PRODUCT_INQUIRY
Action Required: YES
Priority: HIGH
Confidence: 0.96
Summary: ...
```

Testing must not create a real notification or Case unless explicitly requested.

---

# 30. False Classification Handling

Admins should be able to mark stored email classification:

- Mark Relevant
- Mark Not Relevant
- Correct Category
- Correct Priority

Corrections must be audited.

Filtered email must not be deleted merely because it was classified as irrelevant.

---

# 31. Attachments

V1 attachment AI is intentionally not frozen.

Possible options:

### Option A
Subject/body only.

### Option B
Supported text attachments.

### Option C
PDF/images/documents.

Do not implement advanced attachment AI until explicitly approved.

Reasons:

- Security
- Privacy
- Malware
- Parsing complexity
- Storage
- AI cost

Attachment metadata can still be stored.

---

# 32. Case / Work Topic Definition

A Case is the central business work record.

Example:

```text
CASE-000123

Owner:
John Smith

Email Account:
sales@sawo.com

Customer:
customer@example.com

Subject:
Request for ABC Product Price

Received:
2026-09-21 08:15

Work Status:
ACTION_REQUIRED

Reply Status:
AWAITING_REPLY
```

A Case may contain multiple related email messages.

Email = communication evidence.

Case = business work record.

---

# 33. Case Creation

Basic process:

```text
Email Received
      ↓
Classification
      ↓
Relevant?
 ┌────┴────┐
 NO        YES
 ↓          ↓
Store      Find Related Case
             ↓
        Existing Case?
        ┌────┴────┐
       YES        NO
        ↓          ↓
   Update Case   Create Case
```

Non-relevant emails are still stored/classified according to retention policy.

---

# 34. Case Matching

Matching priority:

1. Provider conversation/thread ID.
2. In-Reply-To.
3. References.
4. Message-ID relationship.
5. Participant and email-account relationship.
6. Recent conversation context.
7. Normalized subject as a weak signal.

### Hard requirement

> **IEMAS shall never match a Case based solely on subject.**

Same sender does not automatically mean same Case.

Same subject does not automatically mean same Case.

---

# 35. Subject Normalization

The system may normalize:

```text
Re: Product Inquiry
RE: Product Inquiry
Fwd: Product Inquiry
FW: Product Inquiry
```

to a normalized topic.

However, normalized subject is only a supporting signal.

It is never sufficient by itself.

---

# 36. Completed Case + New Customer Email

If a completed Case receives a clearly related new email:

```text
COMPLETED
    ↓
Related customer email
    ↓
REOPEN
    ↓
ACTION_REQUIRED
```

The previous Case History remains intact.

If the new message is clearly a different business topic:

```text
CASE-000123
COMPLETED

CASE-000124
NEW TOPIC
ACTION_REQUIRED
```

---

# 37. Same Customer ≠ Same Case

Example:

```text
customer@example.com
```

sends:

```text
Price Request
```

and later:

```text
Website Access Problem
```

These should not automatically become one Case.

Topic and conversation relationship must be evaluated.

---

# 38. Multiple Emails in One Case

Example:

```text
CASE-000123
│
├── Customer Email #1
├── Employee Reply #1
├── Customer Email #2
├── Employee Reply #2
└── Customer Follow-up
```

The Case represents the work topic.

---

# 39. New Customer Message Before Employee Reply

If multiple customer messages arrive while a Case is still awaiting reply:

```text
CASE-000123
├── Customer Email #1
├── Customer Email #2
└── Reply Still Required
```

The system should not automatically create duplicate reminder cycles.

The newest email should trigger re-evaluation of:

- Relevance
- Category
- Priority
- Response requirement
- Case state

---

# 40. Case Status

## Work Status

```text
NEW
ACTION_REQUIRED
IN_PROGRESS
WAITING_FOR_CUSTOMER
WAITING_FOR_INTERNAL
WAITING_FOR_APPROVAL
OVERDUE
ESCALATED
COMPLETED
CANCELLED
EXPIRED
REVIEW_REQUIRED
```

## Reply Status

```text
NOT_APPLICABLE
AWAITING_REPLY
REPLY_VERIFICATION_PENDING
REPLY_VERIFICATION_FAILED
REPLY_NOT_FOUND
REPLIED
```

## Notification Status

```text
NONE
SCHEDULED
QUEUED
SENT
DELIVERED
DISPLAYED
ACKNOWLEDGED
CANCELLED
FAILED
EXPIRED
```

---

# 41. Critical Status Rule

The following must never be treated as equivalent:

```text
Email Read
≠
Employee Acknowledged
≠
Employee Replied
≠
Reply Verified
≠
Case Completed
```

Each is a different event/state.

---

# 42. Reply Verification Engine

Reply verification is performed by checking actual outgoing mailbox data.

Identifiers, strongest first where available:

- Conversation/thread ID
- In-Reply-To
- References
- Message ID
- Subject
- Recipient
- Sent timestamp

Verification states:

```text
VERIFIED_REPLY
NO_REPLY_FOUND
VERIFICATION_PENDING
VERIFICATION_FAILED
```

---

# 43. Employee “Already Replied” Action

If employee selects:

```text
ALREADY_REPLIED
```

IEMAS must verify mailbox activity.

If matching Sent Item exists:

```text
Reply:
VERIFIED_REPLY
```

If not:

```text
Employee Claim:
ALREADY_REPLIED

Verification:
NO_REPLY_FOUND

Case:
Still Active
```

The employee's statement is recorded as an event.

It is not proof.

---

# 44. Mailbox Verification Failure

If the mailbox is temporarily unavailable:

```text
Verification:
PENDING / FAILED
```

IEMAS must not assume:

```text
No Reply
```

Instead:

- Keep Case active.
- Retry.
- Preserve error details.
- Avoid premature escalation caused by temporary provider failure.

---

# 45. Verified Reply Does Not Automatically Complete Case

Example:

Customer:

> Please send quotation for 100 units.

Employee:

> Thank you. We received your request and will prepare the quotation.

Result:

```text
Reply Status:
REPLIED

Work Status:
IN_PROGRESS
```

The Case may remain open until the actual business work is complete.

---

# 46. Employee Actions

Supported Agent actions:

```text
ACKNOWLEDGED
WILL_HANDLE
ALREADY_REPLIED
WAITING_FOR_CUSTOMER
WAITING_FOR_INTERNAL
REMIND_LATER
MARK_COMPLETED
CANCEL
REOPEN
REQUEST_ESCALATION
```

Employee actions are events/requests.

They are not automatically authoritative facts.

---

# 47. Employee Comments

Example:

```text
"I already called the client."
```

This is recorded as:

```text
Employee Comment
Timestamp
Employee
Case
Agent
```

It does not become an email reply.

---

# 48. Completing a Case

When employee chooses:

```text
MARK_COMPLETED
```

require a reason.

Suggested reasons:

```text
CUSTOMER_REQUEST_RESOLVED
EMPLOYEE_RESPONDED
PHONE_CALL_HANDLED
HANDLED_OUTSIDE_EMAIL
NO_RESPONSE_REQUIRED
DUPLICATE
INCORRECT_CLASSIFICATION
CANCELLED_BY_ADMIN
CANCELLED_BY_EMPLOYEE
OTHER
```

Example:

```text
Work Status:
COMPLETED

Reply Status:
AWAITING_REPLY

Completion Reason:
HANDLED_OUTSIDE_EMAIL

Comment:
"Called customer and confirmed the order."
```

This preserves accurate history.

---

# 49. Reopening

A completed Case may be reopened if a new relevant related email arrives.

Example:

```text
CASE-000123
COMPLETED
      ↓
Customer sends new relevant message
      ↓
CASE-000123
REOPENED
      ↓
ACTION_REQUIRED
```

Previous completion event remains in history.

---

# 50. Case Ownership

Case ownership belongs to the Employee.

Not:

- Computer
- IP address
- Agent instance

Agent is an endpoint.

---

# 51. Notification Templates

IEMAS should use predefined editable notification messages rather than a generic template builder.

CMS list:

```text
Notification
Enabled
Action

New Email
First Reminder
Reminder
Overdue
Escalation Warning
Escalation
```

Supported variables:

```text
{{employee_name}}
{{email_subject}}
{{sender_name}}
{{sender_email}}
{{received_at}}
{{ai_summary}}
{{elapsed_time}}
{{reminder_count}}
{{case_id}}
```

Message text is editable.

Timing and workflow remain separate.

---

# 52. Notification Examples

## Initial

> Hey John, you have a new email received. Please check.

## First Reminder

> Hey John, have you replied already to the email you received last time?

## Reminder

> Hey John, you haven't replied to the email. Please reply.

## Overdue

Include:

- Subject
- Summary
- Sender
- Received date
- Time outstanding

## Escalation Warning

Inform the employee that escalation will occur if the required work remains unresolved.

---

# 53. Notification vs Action Required

These are different concepts.

### Notification

Something happened.

### Action Required

Employee currently has unresolved work.

Historical notifications should not remain as active work.

---

# 54. Reminder Engine

Workflow:

```text
Initial Notification
      ↓
Wait Configured Interval
      ↓
Check Current Case
      ↓
Check Reply
      ↓
Reply Found?
 ┌────┴────┐
 YES       NO
 ↓          ↓
STOP      Reminder
             ↓
           Repeat
```

Configuration should support:

- Initial delay
- Reminder interval
- Maximum reminders
- Minimum interval
- Business hours
- Weekends
- Holidays
- Time zone
- Expiration
- Escalation threshold

No infinite reminder loops.

---

# 55. Reminder Recheck Rule

Before sending any scheduled reminder, IEMAS must re-check:

- Current Case status.
- Current Reply status.
- Whether reply was verified.
- Whether Case was completed.
- Whether Case was cancelled.
- Whether employee requested another state.
- Whether another notification already occurred.
- Whether escalation changed the state.

If no longer valid:

```text
CANCEL
```

Do not send stale reminders.

---

# 56. Reminder Later

If employee chooses:

> Remind me at 3:00 PM.

IEMAS schedules a reminder.

At 3:00 PM:

```text
Check Case
   ↓
Still active?
   ↓
Still requires action?
   ↓
Still no verified reply?
   ↓
Send reminder
```

Otherwise cancel the scheduled reminder.

---

# 57. Escalation Policies

CMS:

```text
CASE MANAGEMENT
├── Cases / Work Topics
├── Case Workflow
├── Reply Verification
├── Notifications
├── Reminder Policies
└── Escalation Policies
```

Policy fields:

- Policy Name
- Description
- Enabled
- Classification Profile
- Categories
- Priority
- Trigger
- Escalation Levels
- Recipient
- Channel
- Grace Period
- Cooldown
- Maximum Level
- Test Policy

---

# 58. Escalation Levels

Example:

```text
Level 1
After 2 days
→ Employee's Supervisor

Level 2
After another 1 day
→ Department Manager

Level 3
After another 1 day
→ Department Head
```

V1 can initially support a maximum of three levels.

---

# 59. Escalation Recipient Types

Support:

```text
Employee
Employee's Supervisor
Department Manager
Specific Employee
Specific Group
```

Recipient resolution should use organizational data where possible.

---

# 60. Escalation Conditions

Before escalation:

1. Case still exists.
2. Case is active.
3. Required work is still incomplete.
4. Reply is still required where applicable.
5. No verified reply has resolved the reply requirement.
6. Case is not completed.
7. Case is not cancelled.
8. Escalation level has not already executed.
9. Policy is still enabled.
10. Recipient resolves successfully.

Recommended rule:

> Escalate only if the required response/work is still required.

---

# 61. Internal Escalation Email

Example:

```text
From:
IEMAS Notification System
<iemas-notification@sawo.com>

To:
Maria Santos <maria@company.com>

Subject:
IEMAS Escalation — Customer Email Requires Attention

IEMAS ESCALATION

Employee:
John Smith

Customer:
ABC Trading

Subject:
Request for ABC Product Pricing

Received:
September 21, 2026 08:15

Time Outstanding:
2 days

Current Status:
Awaiting Reply

Reply Verification:
No verified reply found

Escalation Level:
Level 1

Reason:
The configured response period has expired
without a verified customer reply.

Please review the case in IEMAS.

Case:
CASE-000123
```

This is an internal message only.

It must never be addressed to the customer.

---

# 62. Outbound Email Lifecycle

```text
QUEUED
  ↓
SENDING
  ↓
SENT
  ↓
DELIVERY_CONFIRMED
```

Where provider support exists.

Failure:

```text
SEND_FAILED
  ↓
RETRY
  ↓
FAILED
```

The escalation event itself remains recorded regardless of email result.

---

# 63. Escalation Audit

Every escalation becomes a Case History child event.

Store:

- Case ID
- Policy ID
- Level
- Trigger
- Recipient
- Channel
- Timestamp
- Email Message ID
- Result
- Delivery result where supported
- Retry count
- Failure reason

---

# 64. Escalation Does Not Automatically Transfer Ownership

Example:

```text
Owner:
John Smith

Escalated To:
Maria Santos
```

John remains the Case owner unless a separate reassignment operation occurs.

---

# 65. Supervisor Case Access

An escalation email may contain a Case link.

The link must require normal IEMAS authentication and authorization.

Do not expose an unauthenticated public Case URL.

Supervisor permissions determine what can be viewed or changed.

---

# 66. Case History

Case History is organized as:

```text
CASE-000123
│
├── Email Received
├── AI Classification
├── Notification Created
├── Notification Sent
├── Notification Displayed
├── Employee Acknowledged
├── Employee Action
├── Employee Comment
├── Reply Verification
├── Reminder
├── Overdue
├── Escalation
├── Case Reopened
└── Case Completed
```

History should be append-only.

Corrections are new events rather than destructive edits.

---

# 67. Separate Logs

## Case History

What happened to the Case?

## Employee Activity

What did the employee do?

## System Audit Log

What did an administrator/configuration change?

## Technical Agent Log

What happened technically on the Agent?

Examples:

- Connect
- Disconnect
- Heartbeat
- Authentication failure
- Sync
- Version
- Error
- Reconnect

---

# 68. Agent Registration

Registration process:

```text
Agent Installed
      ↓
Agent Sends Registration Request
      ↓
Server Creates PENDING
      ↓
Administrator Reviews
      ↓
APPROVE
      ↓
Server Generates Unique Credential
      ↓
Credential Provisioned to Agent
      ↓
Agent Stores Credential Securely
      ↓
Agent Authenticates
      ↓
CONNECTED
```

Client registration information:

- Email Address
- Client/Agent Name
- Server Address
- Client IP
- Agent Version
- Device metadata

Email must correspond to an enrolled IEMAS email account.

---

# 69. Agent Identity

Agent identity consists of:

- Server-issued Agent ID
- Server-issued authentication credential

It must not rely solely on:

- Email address
- IP address
- Client name

IP is metadata.

---

# 70. Agent Credential Requirements

Credential must be:

- Generated only by server.
- Unique per Agent.
- Automatically provisioned.
- Never manually selected.
- Never generated by Agent.
- Rotatable.
- Revocable.
- Invalid after revocation.

The user must not manually copy/paste a registration key as the normal workflow.

---

# 71. Agent Registration Status

```text
PENDING
APPROVED
REJECTED
REVOKED
```

Connection status:

```text
CONNECTING
CONNECTED
DISCONNECTED
```

CMS should display:

- Agent ID
- Agent Name
- Employee
- Email Account
- IP
- Registration Status
- Connection Status
- Last Connected
- Agent Version
- Registered At
- Approved By
- Approved At

---

# 72. Agent Commands

Only predefined commands are allowed:

```text
SHOW_NOTIFICATION
SHOW_REMINDER
SHOW_CASE
CANCEL_NOTIFICATION
SYNC
PING
```

No arbitrary remote execution.

---

# 73. Agent-to-Server Actions

Agent can send:

```text
ACK_NOTIFICATION
CASE_ACTION
CASE_COMMENT
SYNC
PONG
HEARTBEAT
```

Every action request should contain:

- Case ID where applicable
- Employee ID
- Agent ID
- Action
- Comment
- Client timestamp
- Server timestamp
- Idempotency/request ID

Server must validate that the Agent is authorized for the Employee and Case.

---

# 74. Offline Agent

When Agent is offline:

- Case remains on server.
- Case state remains authoritative.
- Pending notifications remain server-side.
- No assumption that employee ignored the Case.
- Server may apply explicit offline policy.
- Agent syncs current state after reconnect.

Possible policies:

```text
ONLINE_ONLY
DELIVER_WHEN_BACK_ONLINE
ESCALATE_IF_OFFLINE
```

The V1 policy must be selected before production.

---

# 75. Agent Synchronization

Startup/reconnect:

```text
Agent
  ↓
Authenticate
  ↓
Connect
  ↓
SYNC
  ↓
Server evaluates current state
  ↓
Return:
  - Action Required
  - Waiting
  - Relevant History
  - Current settings/config
  - Server time
  ↓
Agent updates UI
```

The server remains authoritative.

---

# 76. SignalR

SignalR provides real-time delivery.

It is not the database.

Important state change:

```text
Database transaction
      ↓
Commit
      ↓
SignalR notification
```

If SignalR delivery fails:

```text
Agent reconnects
      ↓
SYNC
      ↓
Current database state
```

No important state may exist only inside SignalR memory.

---

# 77. Server State and Client State

Server:

> Authoritative.

Agent:

> Cached presentation and action endpoint.

The Agent should be able to rebuild its current UI from a fresh server synchronization.

---

# 78. Idempotency

Required for:

- Email ingestion
- AI processing where retry can duplicate work
- Case creation
- Notifications
- Reminders
- Escalations
- Agent commands
- Employee actions

Use:

- Provider Message ID
- Provider conversation IDs
- Notification execution IDs
- Reminder execution IDs
- Escalation execution IDs
- Agent request IDs

---

# 79. Background Jobs

Durable background processing is required for:

- Email polling/synchronization
- Email processing
- AI classification
- Reply verification
- Reminder scheduling
- Escalation scheduling
- Outbound email
- Retry
- Reconciliation
- Agent cleanup
- Retention processing

Jobs must survive server restart.

---

# 80. Reconciliation

Periodic reconciliation protects against inconsistent state.

Examples:

## Case awaiting reply

```text
Case says AWAITING_REPLY
        ↓
Check mailbox
        ↓
Reply found?
        ↓
Update Case
```

## Pending notification

```text
Pending notification
        ↓
Check Case
        ↓
Still valid?
  ┌─────┴─────┐
 YES          NO
  ↓            ↓
Send         Cancel
```

## Agent reconnect

```text
Reconnect
   ↓
SYNC
   ↓
Current server state
```

## Failed reply verification

Retry later.

---

# 81. Reliability Requirements

System must support:

- At-least-once processing
- Idempotency
- Durable jobs
- Database transactions
- Retry policies
- AI retry
- Email retry
- Notification retry
- Provider retry
- Server restart recovery
- Agent reconnect
- Mailbox recovery
- AI provider fallback
- No lost Cases

---

# 82. AI Provider Management

Initial AI provider:

> OpenRouter

AI Model CMS must support:

- Add
- View
- Edit
- Delete
- Enable/Disable
- Test
- Default/Primary
- Task routing
- Fallback

Do not hardcode the current model catalog.

Store:

- Provider
- Model identifier
- Display name
- Enabled
- Default
- Task capability
- Timeout
- Retry policy
- Fallback order
- Configuration metadata

API keys remain server-side.

---

# 83. AI Failure Handling

If AI fails:

```text
AI request
   ↓
Failure
   ↓
Retry
   ↓
Fallback model/provider
   ↓
Still failed?
   ↓
REVIEW_REQUIRED / PROCESSING_FAILED
```

Do not silently mark an unclassified email as irrelevant.

---

# 84. Security Architecture

Minimum requirements:

- HTTPS
- Authentication
- RBAC
- Secure password handling
- Secure secrets
- Encryption where appropriate
- Read-only mailbox permissions where supported
- Server-side AI keys
- Agent authentication
- Credential rotation
- Credential revocation
- Audit logging
- Rate limiting
- Input validation
- Output validation
- CSRF protection where applicable
- Secure cookies/tokens
- No arbitrary remote execution
- Secure Agent credential storage

---

# 85. RBAC

Suggested roles:

## Super Administrator

Full access.

## Administrator

System configuration and Case administration.

## Supervisor / Manager

Authorized team/Cases and escalation information.

## Employee

Own Cases, notifications, Agent actions.

## Auditor / Read-only

Authorized history and audit access.

Permissions should cover:

- Email configuration
- AI configuration
- Case management
- Escalation management
- Agent management
- User management
- Audit access
- Emergency controls

---

# 86. CMS Navigation

```text
IEMAS
│
├── Dashboard
│
├── EMAIL MANAGEMENT
│   ├── Email Accounts
│   ├── Outbound Email
│   ├── Email Monitoring & Intake
│   └── Email Classification
│
├── CASE MANAGEMENT
│   ├── Cases / Work Topics
│   ├── Case Workflow
│   ├── Reply Verification
│   ├── Notifications
│   ├── Reminder Policies
│   └── Escalation Policies
│
├── PEOPLE & DEVICES
│   ├── Employees & Ownership
│   ├── Windows Agents
│   ├── Users & Permissions
│   └── Employee Activity
│
├── AI CONFIGURATION
│   └── AI Models
│
├── HISTORY & AUDIT
│   ├── Case History & Logs
│   └── Audit Log
│
└── SYSTEM
    ├── System Health
    ├── System Settings
    └── Maintenance / Emergency Pause
```

---

# 87. Dashboard

Dashboard should provide:

- Important Emails Today
- Open Cases
- Awaiting Reply
- Overdue
- Escalated
- Completed
- Online Employees
- Offline Employees
- Open Cases by Employee
- Escalated Cases
- Pending Agent Approvals
- AI Errors
- Email Monitoring Errors
- Outbound Email Failures
- Background Job Failures
- System Health

---

# 88. Search

Global search:

- Case ID
- Employee
- Email address
- Customer
- Subject
- Message ID
- Date
- Status

Filters:

- Employee
- Email Account
- Case
- Work Status
- Reply Status
- Event Type
- Date Range
- Priority
- Escalation
- Classification

---

# 89. Case Investigation

Case page should provide a chronological timeline.

Example:

```text
CASE-000123

08:15 Email received
08:16 AI classified
08:17 Notification sent
08:17 Agent received
08:18 Employee acknowledged
10:18 Reminder
12:05 Reply detected
12:06 Reply verified
12:06 Completed
```

Investigators should be able to determine:

- Why notification was sent.
- Why reminder was sent.
- Why Case escalated.
- Why reply was considered verified.
- Why Case remained active.
- Why notification failed.
- Which policy caused an escalation.
- Which employee action changed state.

---

# 90. Email Storage and Retention

Store enough information for:

- Case matching
- Classification
- Reply verification
- Investigation
- Audit

Retention periods remain a business decision.

Configure retention separately for:

- Raw email content
- Email metadata
- Cases
- Case History
- AI results
- Employee Activity
- Audit Logs
- Agent Technical Logs
- Outbound Email Logs

Retention operations must be auditable.

---

# 91. Emergency Pause

CMS:

```text
SYSTEM
├── System Health
├── System Settings
└── Maintenance / Emergency Pause
```

Controls:

```text
Pause Email Processing
Pause AI Classification
Pause Reminders
Pause Escalations
Pause Agent Notifications
```

Every change must be:

- Permission-controlled
- Audited
- Timestamped
- Attributed to a user

Pausing must not delete Cases.

---

# 92. Business Hours

Reminder and escalation policies should support:

- Business hours
- Weekends
- Holidays
- Time zone
- After-hours behavior

The time zone must be explicit.

Do not assume 24/7 processing for business-time rules.

---

# 93. Email Aliases and Shared Mailboxes

The architecture should distinguish:

- Primary mailbox
- Alias
- Shared mailbox
- Distribution address

The exact provider behavior must be determined during provider implementation.

Case should preserve the actual monitored Email Account through which the message was received.

---

# 94. Customer Identity

Customer identity should primarily use sender email address and provider identifiers.

Display name is not a unique identifier.

Example:

```text
Customer Name:
ABC Trading

Sender:
contact@abctrading.com
```

The email address is the stronger identity signal.

CRM integration can be added later.

---

# 95. Forwarded Emails

Forwarded customer emails require special handling.

Example:

```text
Employee forwards customer email
       ↓
IEMAS sees employee as sender
       ↓
Original customer information exists inside body
```

Do not automatically treat the forwarding employee as the customer.

For V1, forwarded messages can be:

- Separately classified.
- Associated using reliable provider metadata.
- Sent to REVIEW_REQUIRED if correlation is uncertain.

---

# 96. CC / BCC

Store:

- To
- CC
- Recipient information

Do not automatically assign the Case to every CC recipient.

Ownership should primarily derive from the monitored Email Account and configured employee ownership.

BCC may not be available in incoming mailbox data and must not be assumed.

---

# 97. New Customer Message After Escalation

If a customer reply or new related message changes the Case:

```text
ESCALATED
    ↓
New relevant email
    ↓
Re-evaluate
    ↓
Update Case
    ↓
Stop invalid future escalation
```

Previous escalation remains in history.

---

# 98. Case Number

Each Case should have:

- Internal unique ID, preferably UUID.
- Human-readable Case Number.

Example:

```text
Internal:
UUID

Display:
CASE-000123
```

Case Number should be used by humans in Agent, CMS, and escalation messages.

---

# 99. Suggested Database Domains

The final database should be organized around domains.

## Identity

```text
users
roles
permissions
user_roles
employees
departments
employee_relationships
```

## Email

```text
email_accounts
email_credentials
email_messages
email_recipients
email_attachments
email_processing
email_threads
```

## AI

```text
ai_providers
ai_models
classification_profiles
classification_categories
classification_examples
ai_classifications
```

## Cases

```text
cases
case_emails
case_status_history
case_events
case_comments
case_assignments
```

## Notifications

```text
notification_templates
notifications
notification_deliveries
notification_actions
```

## Reminders

```text
reminder_policies
reminder_jobs
reminder_executions
```

## Escalations

```text
escalation_policies
escalation_levels
escalation_executions
escalation_recipients
outbound_email_messages
```

## Agents

```text
agents
agent_credentials
agent_connections
agent_commands
agent_heartbeats
agent_logs
```

## Audit

```text
audit_logs
system_events
```

The exact columns and indexes belong in the database design document.

---

# 100. Important Relationships

```text
Employee
   ├── Email Accounts
   ├── Agents
   └── Cases

Email Account
   └── Email Messages

Email Message
   └── Case

Case
   ├── Email Messages
   ├── Events
   ├── Notifications
   ├── Reminders
   ├── Reply Verification
   ├── Employee Actions
   └── Escalations

Agent
   └── Employee

Escalation Policy
   └── Escalation Levels

Escalation Execution
   └── Case
```

---

# 101. API Design

API should be versioned.

Example:

```text
/api/v1/...
```

Logical areas:

```text
Authentication
Employees
Email Accounts
Email Processing
Classification
Cases
Notifications
Reminders
Escalations
Agents
Audit
System Health
```

Rules:

- Validate authorization on every protected endpoint.
- Never trust Employee ID from Agent without validating Agent ownership.
- Use idempotency keys for action endpoints.
- Never return secrets.
- Support pagination.
- Support filtering.
- Return consistent error structures.
- Audit security-sensitive operations.

---

# 102. SignalR Protocol

Logical server-to-agent messages:

```text
SHOW_NOTIFICATION
SHOW_REMINDER
SHOW_CASE
CANCEL_NOTIFICATION
SYNC_REQUIRED
PING
```

Logical agent-to-server messages:

```text
ACK_NOTIFICATION
CASE_ACTION
CASE_COMMENT
SYNC
PONG
HEARTBEAT
```

Important state must be persisted before sending a real-time message.

---

# 103. Transaction Ordering

For important operations:

```text
1. Validate current state.
2. Start transaction.
3. Update authoritative state.
4. Commit.
5. Publish SignalR/job event.
6. Track delivery result.
```

Never make SignalR delivery itself the state transition.

---

# 104. Notification Delivery Tracking

Notification should track the delivery lifecycle:

```text
SCHEDULED
QUEUED
SENT
DELIVERED
DISPLAYED
ACKNOWLEDGED
CANCELLED
FAILED
EXPIRED
```

Not every channel may support every state.

The Agent should report delivery/display/acknowledgement where technically possible.

---

# 105. Outbound Email Audit

Outbound escalation emails should store:

- Outbound configuration ID
- Recipient
- Subject
- Case ID
- Escalation ID
- Message ID
- Queue timestamp
- Send timestamp
- Result
- Delivery confirmation if supported
- Retry count
- Failure reason

Never store the outbound password in these records.

---

# 106. System Health

Monitor:

- Database
- Email provider
- Email synchronization
- AI provider
- Background jobs
- SignalR
- Outbound email
- Agent connections
- Queue depth
- Failed jobs
- Retry count
- Storage
- Server resource usage

Health states:

```text
HEALTHY
WARNING
FAILED
```

---

# 107. Testing Strategy

## Unit Tests

Test:

- Classification
- Case matching
- State transitions
- Reminder calculations
- Escalation calculations
- Reply verification
- Idempotency
- Recipient resolution
- Authorization

## Integration Tests

Test:

- Email provider
- AI provider
- PostgreSQL
- SignalR
- SMTP/outbound provider
- Agent authentication

## End-to-End Tests

Test:

```text
Customer Email
→ AI
→ Case
→ Agent
→ Employee Action
→ Sent Item
→ Reply Verification
→ Completion
```

---

# 108. Failure Testing

Explicitly test:

- Email provider unavailable.
- AI unavailable.
- SMTP unavailable.
- Agent offline.
- SignalR disconnect.
- Database restart.
- Server restart.
- Duplicate email.
- Duplicate Agent action.
- Failed reply verification.
- Stale reminder.
- Stale escalation.
- Invalid Agent credential.
- Revoked Agent credential.
- Missing escalation recipient.
- AI low confidence.
- Case already completed when scheduled job executes.

---

# 109. Complete Acceptance Scenario

A production acceptance test should demonstrate:

```text
1. Customer sends relevant email.
2. IEMAS detects it.
3. Email is stored.
4. AI classifies it.
5. Case is created.
6. Employee ownership is resolved.
7. Agent receives notification.
8. Employee acknowledges.
9. Employee does not reply.
10. Reminder is scheduled.
11. Mailbox is checked.
12. No reply is found.
13. Case becomes overdue.
14. Escalation policy triggers.
15. Supervisor receives internal escalation email.
16. Escalation is recorded.
17. Employee later replies through Outlook/Gmail.
18. IEMAS detects outgoing reply.
19. Reply is verified.
20. Reminders stop.
21. Further escalation stops.
22. Agent removes Case from Action Required.
23. Case remains in History.
```

---

# 110. Operational Safety Requirements

IEMAS must never:

- Send customer replies.
- Automatically draft/send customer responses.
- Delete customer emails.
- Modify customer emails.
- Automatically forward customer emails.
- Execute arbitrary remote commands.
- Treat employee claims as verified replies.
- Treat READ as COMPLETED.
- Match Case by subject alone.
- Lose Case state because Agent is offline.
- Expose credentials.
- Put server API keys on Agent.
- Silently suppress a Case without an auditable reason.

---

# 111. Build Phases

## Phase 1 — Foundation

Build:

- Repository
- Solution structure
- Docker
- PostgreSQL
- Configuration
- Authentication
- RBAC
- Logging
- Audit foundation

## Phase 2 — People and Email Accounts

Build:

- Employees
- Departments
- Ownership
- Organizational relationships
- Email Accounts
- Secure credentials
- Provider adapter abstraction
- Test Connection

## Phase 3 — Email Intake

Build:

- Mail synchronization
- Message normalization
- Email storage
- Duplicate protection
- Thread metadata

## Phase 4 — AI

Build:

- OpenRouter integration
- AI provider/model management
- Classification Profiles
- Classification pipeline
- Confidence handling
- AI audit

## Phase 5 — Cases

Build:

- Case creation
- Case matching
- Case status
- Case history
- Reopening
- Search
- Investigation

## Phase 6 — Reply Verification

Build:

- Sent mailbox access
- Thread matching
- Reply verification
- Verification states
- Reconciliation

## Phase 7 — Windows Agent

Build:

- Agent registration
- Server-issued credentials
- SignalR
- Heartbeat
- Sync
- Notifications
- Case actions
- Comments
- Agent UI
- Windows integration

## Phase 8 — Reminder Engine

Build:

- Reminder policies
- Scheduling
- Recheck
- Cancellation
- Retry
- Business hours

## Phase 9 — Escalation

Build:

- Escalation policies
- Levels
- Organizational recipient resolution
- Outbound Email
- Retry
- Internal email audit
- Case escalation history

## Phase 10 — Hardening

Build:

- Security hardening
- Recovery
- Reconciliation
- Performance
- Audit
- Retention
- Monitoring

## Phase 11 — Testing

Perform:

- Unit tests
- Integration tests
- E2E tests
- Security tests
- Recovery tests
- Agent tests
- Load/performance tests

## Phase 12 — Production Deployment

Prepare:

- Production Docker
- Secrets
- HTTPS
- Database backup
- Monitoring
- Logging
- Agent installer
- Upgrade procedure
- Rollback procedure
- Disaster recovery procedure

---

# 112. Remaining Business Decisions

The architecture is sufficiently defined to begin technical specification and foundation development, but these business decisions should be frozen before production:

1. Exact V1 email providers.
2. Authentication method for each provider.
3. AI confidence thresholds.
4. Medium-confidence behavior.
5. Attachment handling scope.
6. Business hours.
7. Weekend/holiday rules.
8. Time zones.
9. Multiple-Agent policy.
10. Final Case/thread matching rules for ambiguous messages.
11. Data retention periods.
12. Organizational escalation structure.
13. Exact escalation levels.
14. Maximum reminder count.
15. Offline Agent policy.
16. Final CMS frontend framework.
17. Background-job implementation.
18. Production infrastructure.
19. Backup/restore requirements.
20. Disaster recovery targets.

These should be recorded explicitly rather than hidden as developer assumptions.

---

# 113. Recommended Detailed Technical Documents

This master document should be followed by these implementation documents:

```text
IEMAS_System_Specification.md
IEMAS_State_Machine.md
IEMAS_Database_Design.md
IEMAS_API_Specification.md
IEMAS_SignalR_Protocol.md
IEMAS_Agent_Specification.md
IEMAS_Email_Provider_Specification.md
IEMAS_AI_Specification.md
IEMAS_Reminder_Escalation_Specification.md
IEMAS_Security_Design.md
IEMAS_Deployment_Specification.md
IEMAS_Test_And_Acceptance_Plan.md
```

These documents should be derived from this master baseline rather than independently redefining requirements.

---

# 114. Final End-to-End System

The complete intended behavior is:

```text
                         CUSTOMER
                            │
                            ▼
                    CUSTOMER EMAIL
                            │
                            ▼
                    EMAIL PROVIDER
                            │
                            ▼
                 ┌────────────────────┐
                 │ IEMAS EMAIL MONITOR│
                 └─────────┬──────────┘
                           │
                           ▼
                 NORMALIZE / DEDUPLICATE
                           │
                           ▼
                   AI CLASSIFICATION
                           │
                    ┌──────┴──────┐
                    │             │
               NOT RELEVANT    RELEVANT
                    │             │
                    ▼             ▼
                  STORE       CASE MATCHING
                                  │
                         ┌────────┴────────┐
                         │                 │
                    EXISTING CASE       NEW CASE
                         │                 │
                         └────────┬────────┘
                                  ▼
                            CASE WORKFLOW
                                  │
                                  ▼
                         WINDOWS CLIENT AGENT
                                  │
                                  ▼
                              EMPLOYEE
                                  │
                    ┌─────────────┴─────────────┐
                    │                           │
             Employee Action             Normal Email Client
                    │                           │
                    │                           ▼
                    │                    Customer Reply
                    │                           │
                    │                           ▼
                    │                    Sent Items / API
                    │                           │
                    │                           ▼
                    │                  REPLY VERIFICATION
                    │                           │
                    └─────────────┬─────────────┘
                                  │
                        ┌─────────┴─────────┐
                        │                   │
                    RESOLVED            UNRESOLVED
                        │                   │
                        ▼                   ▼
                    COMPLETE             REMINDER
                                            │
                                            ▼
                                         OVERDUE
                                            │
                                            ▼
                                        ESCALATION
                                            │
                             ┌──────────────┴──────────────┐
                             │                             │
                         WINDOWS AGENT               INTERNAL EMAIL
                                                           │
                                                           ▼
                                                   SUPERVISOR / MANAGER
```

---

# 115. Final System Boundary Statement

IEMAS is a **monitoring, understanding, verification, workflow, notification, and escalation platform**.

It is not a customer communication bot.

The employee remains responsible for customer communication.

The normal email client remains responsible for reading and sending customer email.

IEMAS is responsible for determining:

- What arrived.
- Whether it appears relevant.
- Who is responsible.
- Whether work remains outstanding.
- Whether a real reply was detected.
- When reminders are needed.
- When escalation is required.
- What happened throughout the Case lifecycle.

The authoritative flow is:

```text
EMAIL
→ UNDERSTAND
→ CLASSIFY
→ CASE
→ NOTIFY
→ VERIFY
→ REMIND
→ ESCALATE
→ RESOLVE
→ AUDIT
```

This specification is the baseline reference for implementation.
