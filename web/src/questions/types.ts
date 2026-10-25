import * as z from "zod/mini";
export const entryStatus = z.enum(["draft", "in_review", "published", "retired"]);
export type EntryStatus = z.infer<typeof entryStatus>;
export const entryTagKind = z.enum(["role", "task", "item_family", "claim_cause"]);
export type EntryTagKind = z.infer<typeof entryTagKind>;
export const questionNotificationMode = z.enum(["immediately", "daily_digest", "off"]);
export type QuestionNotificationMode = z.infer<typeof questionNotificationMode>;
export const questionStatus = z.enum(["open", "answered", "closed_without_answer"]);
export type QuestionStatus = z.infer<typeof questionStatus>;
export const questionCounts = z.object({ open: z.number(), answered: z.number(), closedWithoutAnswer: z.number(),
  total: z.number() });
export type QuestionCounts = z.infer<typeof questionCounts>;
export const questionListItem = z.object({ questionId: z.string(), title: z.string(), status: questionStatus,
  askedByName: z.string(), askedAt: z.string(), directedToUserName: z.nullable(z.string()),
  directedToRoleLabel: z.nullable(z.string()), replyCount: z.number() });
export type QuestionListItem = z.infer<typeof questionListItem>;
export const questionPage = z.object({ recordKind: z.string(), recordId: z.string(), recordName: z.string(),
  items: z.array(questionListItem), nextCursor: z.nullable(z.string()), counts: questionCounts });
export type QuestionPage = z.infer<typeof questionPage>;
export const allowedActions = z.object({ canEditQuestion: z.boolean(), canReply: z.boolean(),
  canAcceptAnswer: z.boolean(), canCloseWithoutAnswer: z.boolean(), canReopen: z.boolean(),
  canTurnIntoEntry: z.boolean(), entryId: z.nullable(z.string()) });
export type AllowedActions = z.infer<typeof allowedActions>;
export const questionAttachment = z.object({ fileId: z.string(), fileName: z.string(), contentType: z.string(),
  contentLength: z.number(), uploadedByName: z.string(), uploadedAt: z.string() });
export type QuestionAttachment = z.infer<typeof questionAttachment>;
export const questionReply = z.object({ replyId: z.string(), body: z.string(), authorName: z.string(), at: z.string(),
  editedAt: z.nullable(z.string()), revisionCount: z.number(), canEdit: z.boolean(),
  attachments: z.array(questionAttachment) });
export type QuestionReply = z.infer<typeof questionReply>;
export const questionThread = z.object({ questionId: z.string(), recordKind: z.string(), recordId: z.string(),
  recordName: z.string(), title: z.string(), body: z.string(), status: questionStatus,
  directedToUserName: z.nullable(z.string()), directedToRoleLabel: z.nullable(z.string()), askedByName: z.string(),
  askedAt: z.string(), acceptedReplyId: z.nullable(z.string()), closedReason: z.nullable(z.string()),
  closedByName: z.nullable(z.string()), closedAt: z.nullable(z.string()), questionRevisionCount: z.number(),
  allowedActions: allowedActions, attachments: z.array(questionAttachment), replies: z.array(questionReply) });
export type QuestionThread = z.infer<typeof questionThread>;
export const revision = z.object({ revisionId: z.string(), body: z.string(), revisedByName: z.string(),
  revisedAt: z.string() });
export type Revision = z.infer<typeof revision>;
export const staffChoice = z.object({ userId: z.string(), name: z.string(), roles: z.array(z.string()) });
export type StaffChoice = z.infer<typeof staffChoice>;
export const roleChoice = z.object({ role: z.string(), label: z.string(), people: z.number() });
export type RoleChoice = z.infer<typeof roleChoice>;
export const addresseePage = z.object({ people: z.object({ items: z.array(staffChoice),
  nextCursor: z.nullable(z.string()) }), roles: z.array(roleChoice) });
export type AddresseePage = z.infer<typeof addresseePage>;
export const searchQuestion = z.object({ questionId: z.string(), title: z.string(), status: questionStatus,
  recordKind: z.string(), recordId: z.string(), recordName: z.string(), answer: z.string(), rank: z.number(),
  askedAt: z.string() });
export type SearchQuestion = z.infer<typeof searchQuestion>;
export const entryReviewReason = z.object({ reasonId: z.string(), kind: z.enum(["policy_version_superseded",
  "screen_changed"]), label: z.string(), detail: z.string(), flaggedAt: z.string() });
export type EntryReviewReason = z.infer<typeof entryReviewReason>;
export const entryTag = z.object({ kind: entryTagKind, value: z.string(), label: z.string() });
export type EntryTag = z.infer<typeof entryTag>;
export const knowledgeEntryListItem = z.object({ entryId: z.string(), title: z.string(), answer: z.string(),
  currentVersion: z.number(), authorName: z.string(), reviewerName: z.string(), publishedAt: z.string(),
  tags: z.array(entryTag), rank: z.number(), needsReview: z.boolean(), reviewReasons: z.array(entryReviewReason) });
export type KnowledgeEntryListItem = z.infer<typeof knowledgeEntryListItem>;
export const searchPage = z.object({ reviewedEntries: z.array(knowledgeEntryListItem),
  questions: z.array(searchQuestion), nextCursor: z.nullable(z.string()) });
export type SearchPage = z.infer<typeof searchPage>;
export const rankedCount = z.object({ key: z.string(), label: z.string(), count: z.number() });
export type RankedCount = z.infer<typeof rankedCount>;
export const myQuestion = z.object({ questionId: z.string(), title: z.string(), status: questionStatus,
  recordKind: z.string(), recordId: z.string(), recordName: z.string(), askedByName: z.string(),
  directedTo: z.string(), askedAt: z.string(), lastActivityAt: z.string(), replyCount: z.number(),
  waitingDays: z.number() });
export type MyQuestion = z.infer<typeof myQuestion>;
export const myQuestionPage = z.object({ items: z.array(myQuestion), nextCursor: z.nullable(z.string()),
  summary: z.object({ waitingByAge: z.array(rankedCount), waitingByRecordKind: z.array(rankedCount) }) });
export type MyQuestionPage = z.infer<typeof myQuestionPage>;
export const questionNotificationPreference = z.object({ personalDefault: questionNotificationMode,
  roleDefault: questionNotificationMode, choice: z.nullable(questionNotificationMode) });
export type QuestionNotificationPreference = z.infer<typeof questionNotificationPreference>;
export const questionBellItem = z.object({ id: z.string(), questionId: z.nullable(z.string()),
  entryId: z.nullable(z.string()), title: z.string(), recordKind: z.string(), recordId: z.nullable(z.string()),
  recordName: z.string(), reason: z.string(), activityAt: z.string(), link: z.string() });
export type QuestionBellItem = z.infer<typeof questionBellItem>;
export const questionBellSummary = z.object({ count: z.number(), items: z.array(questionBellItem) });
export type QuestionBellSummary = z.infer<typeof questionBellSummary>;
export const entryTagInput = z.object({ kind: entryTagKind, value: z.string() });
export type EntryTagInput = z.infer<typeof entryTagInput>;
export const writtenAgainst = z.object({ kind: z.enum(["policy_version", "screen"]), reference: z.string(),
  label: z.string(), resolved: z.optional(z.boolean()) });
export type WrittenAgainst = z.infer<typeof writtenAgainst>;
export const entryRecordLink = z.object({ recordKind: z.string(), recordId: z.string(), recordName: z.string(),
  source: z.boolean() });
export type EntryRecordLink = z.infer<typeof entryRecordLink>;
export const entryRound = z.object({ round: z.number(), version: z.number(), submittedByName: z.string(),
  submittedAt: z.string(), decision: z.nullable(z.enum(["approve", "send_back"])),
  reviewerName: z.nullable(z.string()), reviewedAt: z.nullable(z.string()), note: z.nullable(z.string()) });
export type EntryRound = z.infer<typeof entryRound>;
export const entryVersion = z.object({ version: z.number(), savedByName: z.string(), savedAt: z.string() });
export type EntryVersion = z.infer<typeof entryVersion>;
export const entryAllowedActions = z.object({ canEdit: z.boolean(), canSubmit: z.boolean(), canApprove: z.boolean(),
  canSendBack: z.boolean(), canRetire: z.boolean(), canConfirmStillCorrect: z.boolean() });
export type EntryAllowedActions = z.infer<typeof entryAllowedActions>;
export const knowledgeEntry = z.object({ entryId: z.string(), title: z.string(), problem: z.string(),
  answer: z.string(), steps: z.string(), status: entryStatus, currentVersion: z.number(), authorName: z.string(),
  createdAt: z.string(), lastSavedAt: z.string(), publishedByName: z.nullable(z.string()),
  publishedAt: z.nullable(z.string()), retirementNote: z.nullable(z.string()), tags: z.array(entryTag),
  writtenAgainst: z.array(writtenAgainst), recordLinks: z.object({ visible: z.array(entryRecordLink),
  hiddenCount: z.number() }), source: z.object({ questionId: z.string(), title: z.string(), recordKind: z.string(),
  recordId: z.string(), recordName: z.nullable(z.string()), acceptedAnswer: z.string() }),
  reviewRounds: z.array(entryRound), versions: z.array(entryVersion), reviewerRoles: z.array(z.string()),
  caseDisclosureTier: z.string(), needsReview: z.boolean(), reviewReasons: z.array(entryReviewReason),
  allowedActions: entryAllowedActions });
export type KnowledgeEntry = z.infer<typeof knowledgeEntry>;
export const knowledgeFacet = z.object({ value: z.string(), label: z.string(), count: z.number() });
export type KnowledgeFacet = z.infer<typeof knowledgeFacet>;
export const knowledgeEntryPage = z.object({ items: z.array(knowledgeEntryListItem),
  nextCursor: z.nullable(z.string()), roles: z.array(knowledgeFacet), tasks: z.array(knowledgeFacet) });
export type KnowledgeEntryPage = z.infer<typeof knowledgeEntryPage>;
export const entryReviewItem = z.object({ entryId: z.string(), title: z.string(), currentVersion: z.number(),
  authorName: z.string(), activityAt: z.string(), round: z.nullable(z.number()), status: entryStatus,
  needsReview: z.boolean(), reviewReasons: z.array(entryReviewReason), canConfirmStillCorrect: z.boolean() });
export type EntryReviewItem = z.infer<typeof entryReviewItem>;
export const entryReviewPage = z.object({ items: z.array(entryReviewItem), nextCursor: z.nullable(z.string()) });
export type EntryReviewPage = z.infer<typeof entryReviewPage>;
export const entryFieldChange = z.object({ field: z.string(), before: z.string(), after: z.string() });
export type EntryFieldChange = z.infer<typeof entryFieldChange>;
export const entryTagChoices = z.object({ roles: z.array(entryTag), tasks: z.array(entryTag),
  itemFamilies: z.array(entryTag), claimCauses: z.array(entryTag) });
export type EntryTagChoices = z.infer<typeof entryTagChoices>;
export const entryDraftRequest = z.object({ title: z.string(), problem: z.string(), answer: z.string(),
  steps: z.string(), tags: z.array(entryTagInput), recordLinks: z.array(z.object({ recordKind: z.string(),
  recordId: z.string() })), writtenAgainst: z.array(writtenAgainst) });
export type EntryDraftRequest = z.infer<typeof entryDraftRequest>;
export const knowledgeScreenReference = z.object({ reference: z.string(), label: z.string(),
  publishedEntries: z.number(), lastChangedAt: z.nullable(z.string()), lastChangedByName: z.nullable(z.string()) });
export type KnowledgeScreenReference = z.infer<typeof knowledgeScreenReference>;
export const knowledgeScreenReferencePage = z.object({ items: z.array(knowledgeScreenReference),
  nextCursor: z.nullable(z.string()) });
export type KnowledgeScreenReferencePage = z.infer<typeof knowledgeScreenReferencePage>;
export const referenceChoice = z.object({ reference: z.string(), label: z.string() });
export type ReferenceChoice = z.infer<typeof referenceChoice>;
export const referenceChoicePage = z.object({ items: z.array(referenceChoice), nextCursor: z.nullable(z.string()) });
export type ReferenceChoicePage = z.infer<typeof referenceChoicePage>;
export const policyVersionChoice = z.object({ policyVersionId: z.string(), label: z.string() });
export type PolicyVersionChoice = z.infer<typeof policyVersionChoice>;
export const policyVersionChoicePage = z.object({ items: z.array(policyVersionChoice),
  nextCursor: z.nullable(z.string()) });
export type PolicyVersionChoicePage = z.infer<typeof policyVersionChoicePage>;
export const trainingPathSummary = z.object({ pathId: z.string(), role: z.string(), roleLabel: z.string(),
  introduction: z.string(), version: z.number(), entries: z.number(), readEntries: z.number(), finished: z.boolean(),
  newMember: z.boolean(), canMaintain: z.boolean() });
export type TrainingPathSummary = z.infer<typeof trainingPathSummary>;
export const trainingPathList = z.object({ items: z.array(trainingPathSummary), canMaintain: z.boolean() });
export type TrainingPathList = z.infer<typeof trainingPathList>;
export const trainingPathEntry = z.object({ position: z.number(), entryId: z.string(), title: z.string(),
  taskLabel: z.nullable(z.string()), publishedVersion: z.number(), read: z.boolean(), readAt: z.nullable(z.string()),
  needsReview: z.boolean(), warning: z.nullable(z.string()) });
export type TrainingPathEntry = z.infer<typeof trainingPathEntry>;
export const trainingPath = z.object({ pathId: z.string(), role: z.string(), roleLabel: z.string(),
  introduction: z.string(), version: z.number(), entries: z.array(trainingPathEntry), readEntries: z.number(),
  finished: z.boolean(), newMember: z.boolean(), welcomeDismissed: z.boolean(), canMaintain: z.boolean(),
  canViewTeam: z.boolean() });
export type TrainingPath = z.infer<typeof trainingPath>;
export const trainingWelcome = z.object({ pathId: z.string(), role: z.string(), roleLabel: z.string(),
  introduction: z.string(), entries: z.number(), readEntries: z.number() });
export type TrainingWelcome = z.infer<typeof trainingWelcome>;
export const trainingTeamMember = z.object({ name: z.string(), joinedAt: z.string(), newMember: z.boolean(),
  readEntries: z.number(), totalEntries: z.number(), lastReadAt: z.nullable(z.string()), finished: z.boolean() });
export type TrainingTeamMember = z.infer<typeof trainingTeamMember>;
export const trainingTeam = z.object({ role: z.string(), roleLabel: z.string(), people: z.array(trainingTeamMember),
  nextCursor: z.nullable(z.string()), questionsAskedInFirstThirtyDays: z.number() });
export type TrainingTeam = z.infer<typeof trainingTeam>;
export const knowledgeReportTiming = z.object({ medianSeconds: z.nullable(z.number()),
  ninetiethPercentileSeconds: z.nullable(z.number()), samples: z.number() });
export type KnowledgeReportTiming = z.infer<typeof knowledgeReportTiming>;
export const knowledgeReportRank = z.object({ key: z.string(), label: z.string(), count: z.number() });
export type KnowledgeReportRank = z.infer<typeof knowledgeReportRank>;
export const knowledgeReportRecurrence = z.object({ entryId: z.string(), title: z.string(), reviewedBy: z.string(),
  askedAgain: z.number(), pointedToIt: z.number(), lastAskedAt: z.string() });
export type KnowledgeReportRecurrence = z.infer<typeof knowledgeReportRecurrence>;
export const knowledgeReportAge = z.object({ key: z.string(), label: z.string(), count: z.number() });
export type KnowledgeReportAge = z.infer<typeof knowledgeReportAge>;
export const knowledgeReportEntryRead = z.object({ entryId: z.string(), title: z.string(), reads: z.number() });
export type KnowledgeReportEntryRead = z.infer<typeof knowledgeReportEntryRead>;
export const knowledgeReport = z.object({ window: z.object({ from: z.string(), through: z.string(),
  timeZone: z.string(), fromUtc: z.string(), toUtc: z.string() }), questions: z.number(),
  firstReply: knowledgeReportTiming, acceptedAnswer: knowledgeReportTiming, unanswered: z.number(),
  pointedToAnEntry: z.number(), byDirectedRole: z.array(knowledgeReportRank),
  byRecordKind: z.array(knowledgeReportRank), byTopic: z.array(knowledgeReportRank),
  askedAgainDespiteEntry: z.array(knowledgeReportRecurrence), unansweredByAge: z.array(knowledgeReportAge),
  readByNewTeamMembers: z.object({ newMembers: z.number(), reads: z.number(),
  entries: z.array(knowledgeReportEntryRead) }), similarEntryMinimumRank: z.number(), visibilityNote: z.string(),
  similarityNote: z.string() });
export type KnowledgeReport = z.infer<typeof knowledgeReport>;

export type KnowledgeReportPeriod = "30_days" | "90_days" | "12_months";
export interface KnowledgeReportFilters { period: KnowledgeReportPeriod; directedRole?: string; recordKind?: string; topic?: string }
