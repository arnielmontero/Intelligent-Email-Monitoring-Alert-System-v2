export interface DepartmentDto {
  id: string;
  name: string;
  description: string | null;
  managerEmployeeId: string | null;
  managerEmployeeName: string | null;
}

export interface EmployeeDto {
  id: string;
  fullName: string;
  email: string;
  isActive: boolean;
  departmentId: string | null;
  departmentName: string | null;
  supervisorEmployeeId: string | null;
  supervisorEmployeeName: string | null;
  managerEmployeeId: string | null;
  managerEmployeeName: string | null;
}

export type EmailAccountPurpose = 0 | 1; // Inbound | Outbound
export type EmailAccountKind = 0 | 1 | 2 | 3; // Primary | Alias | Shared | Distribution
export type EmailProtocol = 0 | 1; // Imap | MicrosoftGraph
export type EmailAuthMethod = 0 | 1 | 2; // Password | OAuth2 | AppPassword

export interface EmailAccountDto {
  id: string;
  emailAddress: string;
  displayName: string | null;
  purpose: EmailAccountPurpose;
  kind: EmailAccountKind;
  protocol: EmailProtocol;
  host: string;
  port: number;
  encryption: string;
  username: string;
  authMethod: EmailAuthMethod;
  ownerEmployeeId: string | null;
  ownerEmployeeName: string | null;
  classificationProfileName: string | null;
  monitoringEnabled: boolean;
  isActive: boolean;
  hasCredential: boolean;
  lastTestedAt: string | null;
  lastTestSucceeded: boolean | null;
  lastTestError: string | null;
}

export interface TestConnectionResult {
  succeeded: boolean;
  errorMessage: string | null;
  durationMs: number;
}

export interface ClassificationProfileDto {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  categories: string;
  includeDefinitions: string;
  excludeDefinitions: string;
  exampleSubject: string | null;
  exampleContent: string | null;
  expectedClassification: string | null;
  highConfidenceThreshold: number | null;
  mediumConfidenceThreshold: number | null;
  treatMediumConfidenceAsReviewRequired: boolean | null;
}

export interface TestClassificationResult {
  relevant: boolean;
  category: string;
  actionRequired: boolean;
  priority: string;
  confidence: number;
  summary: string;
  deterministicFilterOutcome: string;
  finalDecision: string;
  error: string | null;
}

export interface AiModelDto {
  id: string;
  provider: string;
  modelIdentifier: string;
  displayName: string;
  enabled: boolean;
  isDefault: boolean;
  taskCapability: string;
  timeoutSeconds: number;
  maxRetries: number;
  fallbackOrder: number;
}

export interface TestAiModelResult {
  succeeded: boolean;
  errorMessage: string | null;
  durationMs: number;
}

// §40 status enums
export type CaseWorkStatus = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;
export type CaseReplyStatus = 0 | 1 | 2 | 3 | 4 | 5;
export type CaseNotificationStatus = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
export type CaseCompletionReason = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
export type CaseMatchSignal = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;
export type CaseEventType = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;

// §42 Reply Verification
export type ReplyVerificationOutcome = 0 | 1 | 2 | 3;
export type ReplyMatchSignal = 0 | 1 | 2 | 3 | 4 | 5;

export const REPLY_VERIFICATION_OUTCOME_LABELS: Record<ReplyVerificationOutcome, string> = {
  0: "Verified Reply", 1: "No Reply Found", 2: "Verification Pending", 3: "Verification Failed",
};

export const REPLY_MATCH_SIGNAL_LABELS: Record<ReplyMatchSignal, string> = {
  0: "Thread ID", 1: "In-Reply-To", 2: "References", 3: "Message-ID Relationship",
  4: "Recipient/Account", 5: "No Match",
};

export const CASE_WORK_STATUS_LABELS: Record<CaseWorkStatus, string> = {
  0: "New", 1: "Action Required", 2: "In Progress", 3: "Waiting for Customer",
  4: "Waiting for Internal", 5: "Waiting for Approval", 6: "Overdue", 7: "Escalated",
  8: "Completed", 9: "Cancelled", 10: "Expired", 11: "Review Required",
};

export const CASE_REPLY_STATUS_LABELS: Record<CaseReplyStatus, string> = {
  0: "N/A", 1: "Awaiting Reply", 2: "Verification Pending", 3: "Verification Failed",
  4: "Reply Not Found", 5: "Replied",
};

export const CASE_MATCH_SIGNAL_LABELS: Record<CaseMatchSignal, string> = {
  0: "Thread ID", 1: "In-Reply-To", 2: "References", 3: "Message-ID Relationship",
  4: "Same Participant/Account", 5: "Recent Conversation", 6: "Subject (weak signal)", 7: "New Case",
};

export const CASE_EVENT_TYPE_LABELS: Record<CaseEventType, string> = {
  0: "Email Received", 1: "Created", 2: "Updated", 3: "Status Changed",
  4: "Reopened", 5: "Completed", 6: "Cancelled", 7: "Reply Verification",
};

export interface CaseDto {
  id: string;
  caseNumber: string;
  emailAccountId: string;
  emailAccountAddress: string;
  customerEmailAddress: string;
  customerDisplayName: string | null;
  ownerEmployeeId: string | null;
  ownerEmployeeName: string | null;
  subject: string;
  workStatus: CaseWorkStatus;
  replyStatus: CaseReplyStatus;
  notificationStatus: CaseNotificationStatus;
  firstEmailReceivedAt: string;
  lastActivityAt: string;
  completionReason: CaseCompletionReason | null;
  completionComment: string | null;
  completedAt: string | null;
  reopenCount: number;
  emailCount: number;
}

export interface CaseEventDto {
  id: string;
  eventType: CaseEventType;
  detail: string;
  actorEmployeeId: string | null;
  occurredAt: string;
}

export interface CaseEmailDto {
  emailMessageId: string;
  subject: string;
  fromAddress: string;
  receivedAt: string;
  matchSignal: CaseMatchSignal;
  matchDetail: string | null;
}

export interface ReplyVerificationAttemptDto {
  id: string;
  outcome: ReplyVerificationOutcome;
  matchedSentMessageId: string | null;
  matchSignal: ReplyMatchSignal;
  matchDetail: string | null;
  errorDetail: string | null;
  attemptedAt: string;
  durationMs: number;
}

export interface CaseDetailDto {
  case: CaseDto;
  emails: CaseEmailDto[];
  history: CaseEventDto[];
  verificationAttempts: ReplyVerificationAttemptDto[];
}

// §71 Agent Registration / Connection status
export type AgentRegistrationStatus = 0 | 1 | 2 | 3; // Pending | Approved | Rejected | Revoked
export type AgentConnectionStatus = 0 | 1 | 2; // Connecting | Connected | Disconnected
export type AgentLogEventType = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;

export const AGENT_REGISTRATION_STATUS_LABELS: Record<AgentRegistrationStatus, string> = {
  0: "Pending", 1: "Approved", 2: "Rejected", 3: "Revoked",
};

export const AGENT_CONNECTION_STATUS_LABELS: Record<AgentConnectionStatus, string> = {
  0: "Connecting", 1: "Connected", 2: "Disconnected",
};

export const AGENT_LOG_EVENT_TYPE_LABELS: Record<AgentLogEventType, string> = {
  0: "Registration Requested", 1: "Approved", 2: "Rejected", 3: "Revoked",
  4: "Authentication Succeeded", 5: "Authentication Failed", 6: "Connected", 7: "Disconnected",
  8: "Heartbeat", 9: "Sync", 10: "Version Reported", 11: "Error",
};

export interface AgentDto {
  id: string;
  clientName: string;
  enrollmentEmailAddress: string;
  employeeId: string | null;
  employeeName: string | null;
  serverAddress: string | null;
  lastKnownClientIp: string | null;
  agentVersion: string | null;
  registrationStatus: AgentRegistrationStatus;
  connectionStatus: AgentConnectionStatus;
  registeredAt: string;
  approvedByUserId: string | null;
  approvedByUserEmail: string | null;
  approvedAt: string | null;
  lastConnectedAt: string | null;
  lastHeartbeatAt: string | null;
}

export interface AgentLogDto {
  id: string;
  eventType: AgentLogEventType;
  detail: string | null;
  ipAddress: string | null;
  occurredAt: string;
}

// §54 Reminder Engine
export type ReminderStatus = 0 | 1 | 2 | 3 | 4; // Scheduled | Sent | Cancelled | Failed | Expired
export type ReminderTrigger = 0 | 1 | 2; // InitialActionRequired | FollowUp | EmployeeRequested
export type ReminderCancelReason = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;

export const REMINDER_STATUS_LABELS: Record<ReminderStatus, string> = {
  0: "Scheduled", 1: "Sent", 2: "Cancelled", 3: "Failed", 4: "Expired",
};

export const REMINDER_TRIGGER_LABELS: Record<ReminderTrigger, string> = {
  0: "Initial (Action Required)", 1: "Follow-up", 2: "Employee Requested",
};

export const REMINDER_CANCEL_REASON_LABELS: Record<ReminderCancelReason, string> = {
  0: "Reply Verified", 1: "Case Completed", 2: "Case Cancelled", 3: "No Longer Actionable",
  4: "Superseded by Newer Reminder", 5: "Case Escalated", 6: "Max Reminders Reached",
  7: "Policy No Longer Applies", 8: "Manually Cancelled", 9: "Case Not Found",
};

export interface ReminderPolicyHolidayDto {
  id: string;
  date: string;
  label: string | null;
}

export interface ReminderPolicyDto {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  isDefault: boolean;
  classificationProfileId: string | null;
  classificationProfileName: string | null;
  initialDelay: string;
  reminderInterval: string;
  maxReminders: number;
  minimumInterval: string;
  restrictToBusinessHours: boolean;
  businessHoursStart: string;
  businessHoursEnd: string;
  excludeWeekends: boolean;
  timeZoneId: string;
  expirationWindow: string | null;
  escalationThresholdReminderCount: number | null;
  holidays: ReminderPolicyHolidayDto[];
}

export interface ReminderDto {
  id: string;
  caseId: string;
  caseNumber: string;
  reminderPolicyId: string | null;
  trigger: ReminderTrigger;
  status: ReminderStatus;
  sequenceNumber: number;
  scheduledForUtc: string;
  requestedForUtc: string | null;
  executedAtUtc: string | null;
  cancelledAtUtc: string | null;
  cancelReason: ReminderCancelReason | null;
  cancelDetail: string | null;
  deliveryAttempts: number;
  lastFailureDetail: string | null;
}

// §57-§65 Escalation
export type EscalationRecipientType = 0 | 1 | 2 | 3 | 4; // Employee | EmployeeSupervisor | DepartmentManager | SpecificEmployee | SpecificGroup
export type EscalationOutcome = 0 | 1 | 2 | 3; // Executed | Skipped | RecipientUnresolved | Failed
export type EscalationSkipReason = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;
export type ClassificationPriority = 0 | 1 | 2; // Low | Medium | High

export const ESCALATION_RECIPIENT_TYPE_LABELS: Record<EscalationRecipientType, string> = {
  0: "Employee (Owner)", 1: "Employee's Supervisor", 2: "Department Manager", 3: "Specific Employee", 4: "Specific Group",
};

export const ESCALATION_OUTCOME_LABELS: Record<EscalationOutcome, string> = {
  0: "Executed", 1: "Skipped", 2: "Recipient Unresolved", 3: "Failed",
};

export const ESCALATION_SKIP_REASON_LABELS: Record<EscalationSkipReason, string> = {
  0: "Case Not Found", 1: "Case Not Active", 2: "Reply Verified", 3: "Level Already Executed",
  4: "Policy Disabled", 5: "Threshold Not Reached", 6: "Cooldown Active", 7: "Maximum Level Reached",
  8: "No Applicable Policy",
};

export const CLASSIFICATION_PRIORITY_LABELS: Record<ClassificationPriority, string> = {
  0: "Low", 1: "Medium", 2: "High",
};

export interface EscalationLevelDto {
  id: string;
  level: number;
  delayAfterPreviousLevel: string;
  recipientType: EscalationRecipientType;
  specificEmployeeId: string | null;
  specificEmployeeName: string | null;
  specificGroupId: string | null;
  specificGroupName: string | null;
}

export interface SaveEscalationLevelRequest {
  level: number;
  delayAfterPreviousLevel: string;
  recipientType: EscalationRecipientType;
  specificEmployeeId: string | null;
  specificGroupId: string | null;
}

export interface EscalationPolicyDto {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  isDefault: boolean;
  classificationProfileId: string | null;
  classificationProfileName: string | null;
  categories: string | null;
  priority: ClassificationPriority | null;
  triggerReminderCount: number;
  gracePeriod: string;
  cooldown: string;
  maximumLevel: number;
  channel: string;
  levels: EscalationLevelDto[];
}

export interface SaveEscalationPolicyRequest {
  name: string;
  description: string | null;
  enabled: boolean;
  isDefault: boolean;
  classificationProfileId: string | null;
  categories: string | null;
  priority: ClassificationPriority | null;
  triggerReminderCount: number;
  gracePeriod: string;
  cooldown: string;
  maximumLevel: number;
  channel: string;
  levels: SaveEscalationLevelRequest[];
}

export interface EscalationGroupMemberDto {
  employeeId: string;
  employeeName: string;
}

export interface EscalationGroupDto {
  id: string;
  name: string;
  members: EscalationGroupMemberDto[];
}

export interface SaveEscalationGroupRequest {
  name: string;
  employeeIds: string[];
}

export interface EscalationEventDto {
  id: string;
  caseId: string;
  caseNumber: string;
  escalationPolicyId: string | null;
  escalationPolicyName: string | null;
  level: number;
  trigger: string;
  recipientType: EscalationRecipientType | null;
  recipientDisplay: string | null;
  channel: string | null;
  outcome: EscalationOutcome;
  skipReason: EscalationSkipReason | null;
  detail: string | null;
  occurredAt: string;
}

export interface EscalationRunResult {
  considered: number;
  executed: number;
  skipped: number;
  recipientUnresolved: number;
  failed: number;
  durationMs: number;
}

export interface TestEscalationPolicyResult {
  wouldEscalate: boolean;
  eligibleLevel: number | null;
  recipientDisplay: string | null;
  reason: string;
}

// Requirements §67/§84/§85/§86 — System Audit Log (HISTORY & AUDIT nav). Append-only, read-only in the CMS.
export interface AuditLogDto {
  id: string;
  userId: string | null;
  userEmail: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  details: string | null;
  ipAddress: string | null;
  occurredAt: string;
}

// §20/§33 — Case Workflow manual trigger (normal operation is the Hangfire recurring job).
export interface CaseRunResult {
  consideredCount: number;
  createdCount: number;
  updatedCount: number;
  reopenedCount: number;
  durationMs: number;
}

// §42 — Reply Verification manual trigger.
export interface ReplyVerificationRunResult {
  consideredCount: number;
  verifiedCount: number;
  noReplyFoundCount: number;
  pendingCount: number;
  failedCount: number;
  durationMs: number;
}

// §87 — Dashboard.
export interface EmployeeOpenCaseCountDto {
  employeeId: string;
  employeeName: string;
  openCaseCount: number;
}

export interface DashboardSummaryDto {
  importantEmailsToday: number;
  openCases: number;
  awaitingReply: number;
  overdue: number;
  escalated: number;
  completedToday: number;
  onlineEmployees: number;
  offlineEmployees: number;
  openCasesByEmployee: EmployeeOpenCaseCountDto[];
  pendingAgentApprovals: number;
  aiErrors: number;
  emailMonitoringErrors: number;
  backgroundJobFailures: number;
}

// §106 — System Health (SYSTEM nav).
export interface HealthCheckEntryDto {
  name: string;
  status: string;
  description: string | null;
  durationMs: number;
  data: Record<string, unknown> | null;
}

export interface SystemHealthDto {
  status: string;
  totalDurationMs: number;
  checks: HealthCheckEntryDto[];
  failedJobCount: number | null;
  scheduledJobCount: number | null;
  processingJobCount: number | null;
}
