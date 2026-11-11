/**
 * The short introduction shown the first time someone opens each CMS page (and again from "Page guide" in the
 * top bar). Plain language, written for the people who use the page every day, not for developers:
 * what the page is for, then the few things they actually come here to do.
 */
export interface PageGuide {
  /** One or two sentences: what this page is for. */
  intro: string;
  /** The main things people do here, as short "how to" lines. */
  steps: string[];
  /** Optional extra hint shown in muted text. */
  tip?: string;
}

export const PAGE_GUIDES: Record<string, PageGuide> = {
  "/": {
    intro: "Your daily overview. Each card is a live count that updates every minute; click a card to open the list behind it.",
    steps: [
      "Orange and red cards need attention first: Overdue, Escalated, errors and PCs awaiting approval.",
      "\"Awaiting reply\" means no reply to the customer has been found in the Sent folder yet.",
      "\"Open Cases by employee\" at the bottom shows who has the most work.",
    ],
    tip: "Hover the ? on any card for a plain explanation of the number.",
  },
  "/inquiry-supervision": {
    intro: "Every customer inquiry is followed here until the agent confirms it is resolved. A first reply does not end it. This is where you resolve inquiries the AI flagged as \"Potentially resolved\".",
    steps: [
      "Use the cards or the agent table to find inquiries: click any number to list them below.",
      "Click an inquiry row to open it on the right and read the whole conversation, customer and agent.",
      "Resolve it with Confirm resolved, or press Not resolved yet if the AI guessed wrong.",
      "Use Reassign to hand an inquiry to another agent. Deadlines keep running.",
    ],
    tip: "Orange stages mean the agent owes the next step. Red numbers (Overdue, Escalated) are the ones to chase.",
  },
  "/cases": {
    intro: "A Case is one customer conversation with all of its emails. Cases are created automatically from important email.",
    steps: [
      "Filter by status or search by case number, subject or customer email.",
      "Click a row to read the conversation, the AI's reasoning and the full timeline.",
      "Close a Case by hand with Mark completed and a reason (in the Case panel).",
    ],
  },
  "/case-workflow": {
    intro: "The path every customer email takes: received, checked, turned into a Case, worked on, completed, with how many are at each step right now.",
    steps: [
      "Look for orange or red cards to see where work is piling up; click a card to open those Cases.",
      "\"Emails waiting to become a Case\" lists important email not yet in a Case. Run Now processes it immediately.",
      "The bottom table shows what happened to each recent email and links to its Case.",
    ],
  },
  "/reply-verification": {
    intro: "Checks whether employees really replied. Every 5 minutes IEMAS looks in each mailbox's Sent folder for a reply to every open Case. This is where you fix \"no reply\" problems.",
    steps: [
      "Click a card to list those Cases, e.g. \"No reply yet\".",
      "After an employee says they replied, press Check Now to look in the Sent folders straight away.",
      "\"Couldn't check\" means a mailbox could not be read. Fix its password on Email Accounts.",
    ],
    tip: "Clicking \"Already replied\" on the PC is only a claim until the reply is found here.",
  },
  "/escalation-history": {
    intro: "Every time IEMAS considered escalating a Case: who it went to, or why it was skipped.",
    steps: [
      "Filter by Outcome to see only the escalations that went out, or only the skipped ones.",
      "Read Recipient and Skip Reason to understand who was told and why.",
      "Run Now runs the escalation check immediately.",
    ],
  },
  "/email-monitoring": {
    intro: "Shows each mailbox IEMAS checks for new email. It checks every minute by itself.",
    steps: [
      "Press Run Now on a mailbox to check it immediately, e.g. right after adding or fixing it.",
      "If a mailbox is not being checked, turn Monitoring on for it on Email Accounts.",
    ],
  },
  "/employee-activity": {
    intro: "What each employee did from their PC: pop-ups acknowledged, Cases completed, comments added, and who is online.",
    steps: [
      "Pick a period, an employee or an activity type, then press Apply.",
      "Use the Summary table to compare employees; the Activity list shows each action.",
    ],
  },
  "/case-history": {
    intro: "Everything that ever happened to every Case, in one searchable list.",
    steps: [
      "Search by customer, case number, mailbox, owner or text, or narrow by date and event type.",
      "Click a row to open that Case.",
    ],
  },
  "/ai-usage": {
    intro: "What the AI costs. Every AI request is recorded with the price the provider charged.",
    steps: [
      "The top cards show spend for today, the week and the month, plus your OpenRouter account balance.",
      "Pick a date range under Spending analytics to see daily charts and where the money goes.",
      "\"What the built-in filter saves\" shows how many emails were decided for free, without the AI.",
      "Use the Calls list at the bottom to find failed or expensive requests.",
    ],
    tip: "Hover the ? on any card for what that figure means.",
  },
  "/audit-log": {
    intro: "A permanent record of every change an administrator made: settings, users, mailboxes and policies, with who and when. It cannot be edited.",
    steps: [
      "Type part of an action (e.g. USER_) or an entity type (e.g. Agent) and press Filter.",
    ],
  },
  "/email-accounts": {
    intro: "The mailboxes IEMAS watches for customer email. Each mailbox has an owner, the employee who gets its alerts and Cases.",
    steps: [
      "Press + Add email account and fill in the IMAP server, username and password.",
      "Press Test to check the login works.",
      "Use Edit to change the owner or the cut-off date. Older email never becomes Cases.",
      "To remove a mailbox, Deactivate it first, then Delete.",
    ],
  },
  "/email-classification": {
    intro: "Classification profiles tell the AI what kind of email matters for a mailbox: its categories and what counts as important.",
    steps: [
      "Click a profile's categories to see its include and exclude words.",
      "Create a new profile, then select it on a mailbox (Email Accounts, Edit). Profiles cannot be edited later.",
      "Use Test classification to try a sample email without creating anything.",
    ],
  },
  "/system-configuration": {
    intro: "Decide which emails become work. Rules are plain sentences the AI follows; the ignore list blocks senders completely.",
    steps: [
      "Add a rule such as \"Newsletters from suppliers\" meaning Not legitimate.",
      "Edit or Delete a rule any time. Changes apply to new email only.",
      "Add a domain or address under Email filtering to ignore it completely.",
    ],
  },
  "/outbound-email": {
    intro: "The mailbox IEMAS uses to send its own emails, mainly escalation emails to managers. It never emails customers.",
    steps: [
      "Press + Configure Outbound Email and enter the SMTP server and password.",
      "Press Test Connection to check it works.",
      "If escalation emails fail, the reason appears in Escalation History.",
    ],
  },
  "/notifications": {
    intro: "The pop-up messages employees get on their PC: new email, reminders, overdue and escalation warnings.",
    steps: [
      "Templates: press Edit to change a message's wording; use Insert to add details like the case number.",
      "Turn a message type Off to stop sending it.",
      "Delivery log: check whether an employee received and acknowledged a pop-up.",
    ],
  },
  "/reminder-policies": {
    intro: "How often a Case owner is reminded while no reply has been found, and the business hours that count.",
    steps: [
      "Edit the default policy to change the first-reminder delay, the interval and the maximum number.",
      "Set business hours and weekends so reminders are not sent at night.",
    ],
    tip: "The default policy's business hours and holidays are also the calendar Inquiry Supervision uses for its deadlines.",
  },
  "/escalation-policies": {
    intro: "What happens when a Case is still unanswered after the reminders: who is told, and when.",
    steps: [
      "Set after how many reminders, and after what grace period, a Case escalates.",
      "Under Levels, choose who is told at each step: supervisor, department manager, a person or a group.",
      "Use Test Policy with a Case ID to see who would be told, without sending anything.",
    ],
    tip: "Escalation emails are sent through the Outbound Email account.",
  },
  "/escalation-groups": {
    intro: "Named groups of people, e.g. \"Sales managers\", that an escalation level can send to.",
    steps: [
      "Press Add Escalation Group, give it a name and tick its members.",
      "Then pick the group as a recipient on Escalation Policies.",
    ],
  },
  "/employees": {
    intro: "The people who own mailboxes and Cases, with their department and supervisor. The supervisor is used for escalation.",
    steps: [
      "Add an employee, then give them a mailbox (Email Accounts) and approve their PC (Windows Agents).",
      "Set the supervisor correctly: they are told when this person's Cases are escalated.",
      "Deactivate someone who leaves. Delete works only when nothing refers to them.",
    ],
  },
  "/agents": {
    intro: "The IEMAS Agent program on each employee's PC, which shows the pop-ups. A new PC appears here as Pending.",
    steps: [
      "For a Pending PC, choose the employee who uses it and press Approve.",
      "Revoke a lost or replaced PC to cut it off immediately.",
      "Check the Connection column to see whether a PC is online.",
    ],
  },
  "/users": {
    intro: "Who can sign in to this CMS, and what each person may do.",
    steps: [
      "Add a user with an email, a first password and one or more roles.",
      "Reset password when someone forgets theirs. Deactivate signs them out at once.",
      "Change your own password at the bottom of the page.",
    ],
  },
  "/setup": {
    intro: "A checklist of everything the system needs before it works: AI key, mailboxes, outbound email, business hours and escalation recipients.",
    steps: [
      "Work down the list. Anything not ticked still needs doing.",
      "Each item links to the screen where you fix it.",
    ],
  },
  "/ai-models": {
    intro: "The AI connection (OpenRouter API key) and the AI models used to read email.",
    steps: [
      "Paste the OpenRouter API key, press Save, then Test connection.",
      "Choose a default model. If it fails, the others are tried in fallback order.",
      "Use Model analysis to compare models on accuracy, speed and cost before switching.",
    ],
  },
  "/system-health": {
    intro: "Whether every part of IEMAS is working right now: database, mailboxes, AI provider and background tasks.",
    steps: [
      "Anything not green needs attention. The Details column says what is wrong.",
      "A non-zero Failed count under Background jobs means a scheduled task keeps failing; tell a developer.",
    ],
  },
  "/system-settings": {
    intro: "General settings: organization name, time zone, inquiry detection words and inquiry supervision timers.",
    steps: [
      "Change a value, then press Save changes at the bottom. Changed rows show a dot.",
      "Reset puts a single setting back to its default.",
    ],
    tip: "Each setting has a short explanation under its name.",
  },
  "/maintenance": {
    intro: "Emergency switches to pause parts of the system, e.g. during a mail-server problem. Nothing is deleted; work resumes where it stopped.",
    steps: [
      "Type a reason and press Pause on the part you want to stop; press Resume when done.",
      "Generate sample data adds fake [Sample] emails so you can try the system safely.",
      "Reset email data deletes all emails and Cases. Type RESET to confirm; people and settings are kept.",
    ],
  },
  "/help": {
    intro: "A description of every page in the CMS. Use the \"Help for this page\" link in the top bar to jump straight to the right section.",
    steps: [],
  },
};
