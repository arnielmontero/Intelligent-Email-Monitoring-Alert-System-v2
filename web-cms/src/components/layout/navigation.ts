export type NavIcon = "dashboard" | "mail" | "cases" | "people" | "ai" | "history" | "system" | "help";

export interface NavItem {
  label: string;
  path: string;
  /** What the page is for — shown on the Help page. */
  help: string;
  /** Typical things people do there. */
  tasks?: string[];
}

export interface NavSection {
  id: string;
  label: string;
  icon: NavIcon;
  items: NavItem[];
}

/** The CMS menu, and the Help page's content — one source so the two never drift apart. */
export const NAV_SECTIONS: NavSection[] = [
  {
    id: "dashboard",
    label: "Dashboard",
    icon: "dashboard",
    items: [
      {
        label: "Dashboard",
        path: "/",
        help: "Today at a glance: important emails, open Cases, Cases awaiting a reply, overdue, escalated and completed today; employees online and offline and Agents waiting for approval; and errors from the AI, mailboxes and background jobs. Click a card to open the matching page. Updates every 60 seconds.",
      },
    ],
  },
  {
    id: "email",
    label: "Email Management",
    icon: "mail",
    items: [
      {
        label: "Email Accounts",
        path: "/email-accounts",
        help: "The mailboxes IEMAS watches for customer email. Each mailbox has an owner — the employee whose PC gets its alerts. The cut-off date means older mailbox history is kept but never becomes Cases.",
        tasks: ["Add a mailbox (IMAP server, username, password)", "Set or change the owner with Edit", "Test the connection", "Deactivate, then Delete, a mailbox you no longer use"],
      },
      {
        label: "Outbound Email",
        path: "/outbound-email",
        help: "An SMTP mailbox set aside for IEMAS's own outgoing email. Right now it is only stored and connection-tested: IEMAS does not send any email yet — employees are alerted by pop-ups on their PC. It is never checked for customer email.",
        tasks: ["Add the SMTP server and password", "Test the connection"],
      },
      {
        label: "Email Monitoring & Intake",
        path: "/email-monitoring",
        help: "Shows each mailbox being checked for new email. IEMAS checks every 2 minutes by itself; Run Now checks one mailbox immediately and shows how many emails were fetched. New email is then read by the AI (every 2 minutes) and turned into Cases (every 2 minutes), so a new email can take up to about 6 minutes to become a Case.",
        tasks: ["Run Now after adding or fixing a mailbox", "See why a mailbox isn't being checked (monitoring off, deactivated)"],
      },
      {
        label: "Email Classification",
        path: "/email-classification",
        help: "Classification profiles tell the AI what kind of email matters for a mailbox: the categories it may use and the words that make an email important or not. Click a profile's categories to see them. Profiles can be created, enabled/disabled and deleted here, but not edited — to change one, create a new profile and select it on the mailbox. The Test box tries a sample email without creating anything.",
        tasks: ["See a profile's categories and include/exclude words", "Create a profile and select it on a mailbox (Email Accounts → Edit)", "Test a sample email to see what the AI decides"],
      },
    ],
  },
  {
    id: "cases",
    label: "Case Management",
    icon: "cases",
    items: [
      {
        label: "Cases / Work Topics",
        path: "/cases",
        help: "A Case is one piece of customer work with all the emails that belong to it. Cases are created automatically from important email. Click a row to see the emails, the AI summary and the full timeline.",
        tasks: ["Search by case number, subject or customer", "Open a Case to read the emails", "Mark a Case completed with a reason"],
      },
      {
        label: "Case Workflow",
        path: "/case-workflow",
        help: "The path every customer email takes — received, checked by the AI, turned into a Case, worked on, completed — with how many are at each stage right now. Click a stage to see those Cases. Below: the important emails waiting to become a Case, and what happened to each recently.",
        tasks: ["See where work is piling up", "Run Now to turn waiting emails into Cases immediately", "Open a Case straight from the email list"],
      },
      {
        label: "Reply Verification",
        path: "/reply-verification",
        help: "Did the employee really reply? Every 5 minutes IEMAS looks in each mailbox's Sent folder for a reply to every open Case, and keeps looking until one is found. Clicking \"Already replied\" on the PC is only a claim until the reply is found. Once found, reminders and escalation stop. If a mailbox can't be reached the Case shows \"Couldn't check\", never a false \"no reply\".",
        tasks: ["See which Cases still have no reply, and who owns them", "Check Now right after an employee says they replied", "Spot a mailbox whose Sent folder can't be read"],
      },
      {
        label: "Notifications",
        path: "/notifications",
        help: "The pop-up messages employees receive on their PC through the Windows Agent: new email, first reminder, reminder, overdue, escalation warning and escalation. Edit the wording of each message, preview it, turn a type off, and see in the delivery log what was sent, queued or acknowledged.",
        tasks: ["Edit a message and preview it", "Turn a message type off", "Check whether an employee received an alert"],
      },
      {
        label: "Reminder Policies",
        path: "/reminder-policies",
        help: "How often the Case owner is reminded (by pop-up) while no reply has been found in the Sent folder: delay before the first reminder, time between reminders, maximum number of reminders, optional expiry, business hours with time zone, and whether weekends count. Checked every 5 minutes; reminders stop as soon as a reply is found. Holidays can't be entered in the CMS yet.",
      },
      {
        label: "Escalation Policies",
        path: "/escalation-policies",
        help: "What happens when a Case still has no reply after a number of reminders: after how many reminders and how long a grace period, and up to 3 levels, each with a recipient (the owner, their supervisor, the department manager, a specific employee or a group). Checked every 10 minutes. The Case is marked Escalated and the owner gets an Escalation pop-up; the chosen recipient is recorded in Escalation History but is not contacted automatically yet (no pop-up or email to them). The Channel field is stored only.",
        tasks: ["Set after how many reminders a Case escalates", "Choose who each level is for", "Test a policy against a Case without recording anything"],
      },
      {
        label: "Escalation Groups",
        path: "/escalation-groups",
        help: "Named groups of employees, for example \"Sales managers\", that an escalation level can name as its recipient. The group is recorded in Escalation History; its members are not contacted automatically yet.",
      },
      {
        label: "Escalation History",
        path: "/escalation-history",
        help: "Every escalation check that did something — carried out, skipped, or no recipient could be found — with the recipient it resolved to and the reason. This is where to see who a Case should be escalated to.",
      },
    ],
  },
  {
    id: "people",
    label: "People & Devices",
    icon: "people",
    items: [
      {
        label: "Employees & Ownership",
        path: "/employees",
        help: "The people who own mailboxes and Cases, with their department and supervisor (used for escalation). Deactivate someone who leaves; Delete only works when nothing refers to them.",
      },
      {
        label: "Windows Agents",
        path: "/agents",
        help: "The IEMAS Agent program on each employee's PC. A new PC appears as Pending: pick the employee who owns its email account and Approve. It then connects by itself. Revoke cuts off a lost or old PC immediately.",
        tasks: ["Approve a new PC", "Revoke a lost PC", "Check whether an Agent is connected"],
      },
      {
        label: "Users & Permissions",
        path: "/users",
        help: "Who can sign in to this CMS and what each role may do (Super Administrator, Administrator, Supervisor, Auditor). Deactivating a user signs them out immediately.",
        tasks: ["Add a user and give them roles", "Reset a password", "Change your own password"],
      },
      {
        label: "Employee Activity",
        path: "/employee-activity",
        help: "What each employee did from their PC — acknowledged, completed, commented — plus who is online and how many open Cases they have.",
      },
    ],
  },
  {
    id: "ai",
    label: "AI Configuration",
    icon: "ai",
    items: [
      {
        label: "AI Models",
        path: "/ai-models",
        help: "The OpenRouter connection (API key) and the AI models used to read email. If a model fails, the next one in fallback order is tried.",
        tasks: ["Paste the OpenRouter API key and Test connection", "Pick a model from the list", "Edit fallback order, timeout and retries"],
      },
      {
        label: "AI Usage & Cost",
        path: "/ai-usage",
        help: "What the AI costs: totals for today, the last 7 and 30 days and this month, cost per model, the OpenRouter account balance, and every call (10 per page).",
      },
    ],
  },
  {
    id: "history",
    label: "History & Audit",
    icon: "history",
    items: [
      {
        label: "Case History & Logs",
        path: "/case-history",
        help: "Everything that happened to every Case — email received, reminders, escalations, employee actions. Search by customer, mailbox, owner or text; click a row to open that Case.",
      },
      {
        label: "Audit Log",
        path: "/audit-log",
        help: "Every change an administrator made — settings, users, accounts, policies — with who and when. It can't be edited.",
      },
    ],
  },
  {
    id: "system",
    label: "System",
    icon: "system",
    items: [
      {
        label: "System Health",
        path: "/system-health",
        help: "Whether the database, mailboxes, AI provider and background jobs are working right now.",
      },
      {
        label: "System Settings",
        path: "/system-settings",
        help: "General settings: organization name, time zone (used for dates in messages) and minimum password length.",
      },
      {
        label: "System Configuration",
        path: "/system-configuration",
        help: "Which emails become work. The AI email rules decide what counts as a legitimate email and when a reply is needed; Ignored senders lists domains or addresses whose email is never turned into Cases or alerts.",
        tasks: ["Add a rule such as \"Newsletters from suppliers → Not legitimate\"", "Ignore a sender domain", "Choose whether only email needing a reply becomes a Case"],
      },
      {
        label: "Maintenance / Emergency Pause",
        path: "/maintenance",
        help: "Emergency switches to pause email fetching, AI classification, reminders, escalations or pop-ups — for example during a problem or maintenance. Nothing is deleted; work resumes where it stopped. Super administrators also find two tools here. Generate sample data adds sample customer emails (marked [Sample], from random senders and domains) and, the first time, a sample team — a Sample Sales department with a manager, a supervisor and three staff with sample mailboxes and PCs — to try the whole flow — Cases, pop-ups, reply checks, reminders, escalation — either already classified (no AI cost, Cases made immediately) or read by the AI like real mail. Reset email data deletes all emails, Cases and their history and starts fresh from now; tick the sample team and any test records to remove them too. Your real employees, mailboxes, PCs, users, settings and policies are kept.",
        tasks: ["Pause one engine with a reason, then Resume", "Generate sample emails to test the system", "Reset email data after testing (type RESET to confirm), optionally removing leftover test employees, mailboxes and agents"],
      },
    ],
  },
  {
    id: "help",
    label: "Help",
    icon: "help",
    items: [
      {
        label: "Help",
        path: "/help",
        help: "This page.",
      },
    ],
  },
];

/** Anchor used on the Help page for a menu item. */
export const helpAnchor = (path: string) => `help-${path === "/" ? "dashboard" : path.slice(1)}`;
