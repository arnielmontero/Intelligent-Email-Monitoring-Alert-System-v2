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
        help: "Today at a glance: important emails received, open Cases, what is waiting for a reply, overdue or escalated, which employees are online, and anything failing (AI, mailboxes, background jobs).",
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
        help: "The mailbox IEMAS uses to send its own emails, such as escalation notices. Use a dedicated notification mailbox, not a personal one; it is never checked for customer email.",
      },
      {
        label: "Email Monitoring & Intake",
        path: "/email-monitoring",
        help: "Shows each mailbox being checked for new email. IEMAS checks every 2 minutes by itself; Run Now checks immediately and shows how many emails were fetched.",
        tasks: ["Run Now after adding or fixing a mailbox", "See why a mailbox isn't being checked (monitoring off, deactivated)"],
      },
      {
        label: "Email Classification",
        path: "/email-classification",
        help: "Classification profiles tell the AI what kind of email matters for a mailbox: categories, words to include or exclude, and how sure it must be. The Test box lets you try a sample email without creating anything.",
        tasks: ["Edit the Sales profile's include/exclude words", "Test a sample email to see what the AI decides"],
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
        help: "The pop-up messages employees receive on their PC (new email, reminders, escalation warning). Edit the wording of each message and see what was delivered, queued or acknowledged.",
        tasks: ["Edit a message and preview it", "Turn a message type off", "Check whether an employee received an alert"],
      },
      {
        label: "Reminder Policies",
        path: "/reminder-policies",
        help: "How often employees are reminded about a Case that still needs a reply: first reminder, time between reminders, maximum number, business hours, weekends and holidays.",
      },
      {
        label: "Escalation Policies",
        path: "/escalation-policies",
        help: "What happens when a Case still has no reply after its reminders: who is told (supervisor, manager, a specific person or group), after how long, and up to 3 levels.",
      },
      {
        label: "Escalation Groups",
        path: "/escalation-groups",
        help: "Named groups of employees an escalation can notify, for example \"Sales managers\".",
      },
      {
        label: "Escalation History",
        path: "/escalation-history",
        help: "Every escalation attempt — sent, skipped or no recipient found — with the reason, for looking back.",
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
        help: "Emergency switches to pause email fetching, AI classification, reminders, escalations or pop-ups — for example during a problem or maintenance. Nothing is deleted; work resumes where it stopped.",
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
