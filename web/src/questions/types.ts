export type QuestionStatus = "open" | "answered" | "closed_without_answer";
export interface QuestionCounts { open: number; answered: number; closedWithoutAnswer: number; total: number }
export interface QuestionListItem { questionId: string; title: string; status: QuestionStatus; askedByName: string; askedAt: string; directedToUserName: string | null; directedToRoleLabel: string | null; replyCount: number }
export interface QuestionPage { recordKind: string; recordId: string; recordName: string; items: QuestionListItem[]; nextCursor: string | null; counts: QuestionCounts }
export interface AllowedActions { canEditQuestion: boolean; canReply: boolean; canAcceptAnswer: boolean; canCloseWithoutAnswer: boolean; canReopen: boolean; canTurnIntoEntry: boolean; entryId: string | null }
export interface QuestionAttachment { fileId: string; fileName: string; contentType: string; contentLength: number; uploadedByName: string; uploadedAt: string }
export interface QuestionReply { replyId: string; body: string; authorName: string; at: string; editedAt: string | null; revisionCount: number; canEdit: boolean; attachments: QuestionAttachment[] }
export interface QuestionThread { questionId: string; recordKind: string; recordId: string; recordName: string; title: string; body: string; status: QuestionStatus; directedToUserName: string | null; directedToRoleLabel: string | null; askedByName: string; askedAt: string; acceptedReplyId: string | null; closedReason: string | null; closedByName: string | null; closedAt: string | null; questionRevisionCount: number; allowedActions: AllowedActions; attachments: QuestionAttachment[]; replies: QuestionReply[] }
export interface Revision { revisionId: string; body: string; revisedByName: string; revisedAt: string }
export interface StaffChoice { userId: string; name: string; roles: string[] }
export interface RoleChoice { role: string; label: string; people: number }
export interface AddresseePage { people: { items: StaffChoice[]; nextCursor: string | null }; roles: RoleChoice[] }
export interface SearchQuestion { questionId: string; title: string; status: QuestionStatus; recordKind: string; recordId: string; recordName: string; answer: string; rank: number; askedAt: string }
export interface SearchPage { reviewedEntries: KnowledgeEntryListItem[]; questions: SearchQuestion[]; nextCursor: string | null }
export interface RankedCount { key: string; label: string; count: number }
export interface MyQuestion { questionId: string; title: string; status: QuestionStatus; recordKind: string; recordId: string; recordName: string; askedByName: string; directedTo: string; askedAt: string; lastActivityAt: string; replyCount: number; waitingDays: number }
export interface MyQuestionPage { items: MyQuestion[]; nextCursor: string | null; summary: { waitingByAge: RankedCount[]; waitingByRecordKind: RankedCount[] } }
export type QuestionNotificationMode = "immediately" | "daily_digest" | "off";
export interface QuestionNotificationPreference { personalDefault: QuestionNotificationMode; roleDefault: QuestionNotificationMode; choice: QuestionNotificationMode | null }
export interface QuestionBellItem { id: string; questionId: string | null; entryId: string | null; title: string; recordKind: string; recordId: string | null; recordName: string; reason: string; activityAt: string; link: string }
export interface QuestionBellSummary { count: number; items: QuestionBellItem[] }

export type EntryStatus = "draft" | "in_review" | "published" | "retired";
export type EntryTagKind = "role" | "task" | "item_family" | "claim_cause";
export interface EntryTag { kind: EntryTagKind; value: string; label: string }
export interface EntryTagInput { kind: EntryTagKind; value: string }
export interface WrittenAgainst { kind: "policy_version" | "screen"; reference: string; label: string; resolved?: boolean }
export interface EntryRecordLink { recordKind: string; recordId: string; recordName: string; source: boolean }
export interface EntryRound { round: number; version: number; submittedByName: string; submittedAt: string; decision: "approve" | "send_back" | null; reviewerName: string | null; reviewedAt: string | null; note: string | null }
export interface EntryVersion { version: number; savedByName: string; savedAt: string }
export interface EntryReviewReason { reasonId: string; kind: "policy_version_superseded" | "screen_changed"; label: string; detail: string; flaggedAt: string }
export interface EntryAllowedActions { canEdit: boolean; canSubmit: boolean; canApprove: boolean; canSendBack: boolean; canRetire: boolean; canConfirmStillCorrect: boolean }
export interface KnowledgeEntry {
  entryId: string; title: string; problem: string; answer: string; steps: string; status: EntryStatus; currentVersion: number;
  authorName: string; createdAt: string; lastSavedAt: string; publishedByName: string | null; publishedAt: string | null; retirementNote: string | null;
  tags: EntryTag[]; writtenAgainst: WrittenAgainst[]; recordLinks: { visible: EntryRecordLink[]; hiddenCount: number };
  source: { questionId: string; title: string; recordKind: string; recordId: string; recordName: string | null; acceptedAnswer: string };
  reviewRounds: EntryRound[]; versions: EntryVersion[]; reviewerRoles: string[]; caseDisclosureTier: string;
  needsReview: boolean; reviewReasons: EntryReviewReason[]; allowedActions: EntryAllowedActions;
}
export interface KnowledgeEntryListItem { entryId: string; title: string; answer: string; currentVersion: number; authorName: string; reviewerName: string; publishedAt: string; tags: EntryTag[]; rank: number; needsReview: boolean; reviewReasons: EntryReviewReason[] }
export interface KnowledgeFacet { value: string; label: string; count: number }
export interface KnowledgeEntryPage { items: KnowledgeEntryListItem[]; nextCursor: string | null; roles: KnowledgeFacet[]; tasks: KnowledgeFacet[] }
export interface EntryReviewItem { entryId: string; title: string; currentVersion: number; authorName: string; activityAt: string; round: number | null; status: EntryStatus; needsReview: boolean; reviewReasons: EntryReviewReason[]; canConfirmStillCorrect: boolean }
export interface EntryReviewPage { items: EntryReviewItem[]; nextCursor: string | null }
export interface EntryFieldChange { field: string; before: string; after: string }
export interface EntryTagChoices { roles: EntryTag[]; tasks: EntryTag[]; itemFamilies: EntryTag[]; claimCauses: EntryTag[] }
export interface EntryDraftRequest { title: string; problem: string; answer: string; steps: string; tags: EntryTagInput[]; recordLinks: Array<{ recordKind: string; recordId: string }>; writtenAgainst: WrittenAgainst[] }
export interface KnowledgeScreenReference { reference: string; label: string; publishedEntries: number; lastChangedAt: string | null; lastChangedByName: string | null }
export interface KnowledgeScreenReferencePage { items: KnowledgeScreenReference[]; nextCursor: string | null }
export interface ReferenceChoice { reference: string; label: string }
export interface ReferenceChoicePage { items: ReferenceChoice[]; nextCursor: string | null }
export interface PolicyVersionChoice { policyVersionId: string; label: string }
export interface PolicyVersionChoicePage { items: PolicyVersionChoice[]; nextCursor: string | null }

export interface TrainingPathSummary { pathId: string; role: string; roleLabel: string; introduction: string; version: number; entries: number; readEntries: number; finished: boolean; newMember: boolean; canMaintain: boolean }
export interface TrainingPathList { items: TrainingPathSummary[]; canMaintain: boolean }
export interface TrainingPathEntry { position: number; entryId: string; title: string; taskLabel: string | null; publishedVersion: number; read: boolean; readAt: string | null; needsReview: boolean; warning: string | null }
export interface TrainingPath { pathId: string; role: string; roleLabel: string; introduction: string; version: number; entries: TrainingPathEntry[]; readEntries: number; finished: boolean; newMember: boolean; welcomeDismissed: boolean; canMaintain: boolean; canViewTeam: boolean }
export interface TrainingWelcome { pathId: string; role: string; roleLabel: string; introduction: string; entries: number; readEntries: number }
export interface TrainingTeamMember { name: string; joinedAt: string; newMember: boolean; readEntries: number; totalEntries: number; lastReadAt: string | null; finished: boolean }
export interface TrainingTeam { role: string; roleLabel: string; people: TrainingTeamMember[]; nextCursor: string | null; questionsAskedInFirstThirtyDays: number }

export type KnowledgeReportPeriod = "30_days" | "90_days" | "12_months";
export interface KnowledgeReportFilters { period: KnowledgeReportPeriod; directedRole?: string; recordKind?: string; topic?: string }
export interface KnowledgeReportTiming { medianSeconds: number | null; ninetiethPercentileSeconds: number | null; samples: number }
export interface KnowledgeReportRank { key: string; label: string; count: number }
export interface KnowledgeReportRecurrence { entryId: string; title: string; reviewedBy: string; askedAgain: number; pointedToIt: number; lastAskedAt: string }
export interface KnowledgeReportAge { key: string; label: string; count: number }
export interface KnowledgeReportEntryRead { entryId: string; title: string; reads: number }
export interface KnowledgeReport {
  window: { from: string; through: string; timeZone: string; fromUtc: string; toUtc: string };
  questions: number; firstReply: KnowledgeReportTiming; acceptedAnswer: KnowledgeReportTiming; unanswered: number; pointedToAnEntry: number;
  byDirectedRole: KnowledgeReportRank[]; byRecordKind: KnowledgeReportRank[]; byTopic: KnowledgeReportRank[];
  askedAgainDespiteEntry: KnowledgeReportRecurrence[]; unansweredByAge: KnowledgeReportAge[];
  readByNewTeamMembers: { newMembers: number; reads: number; entries: KnowledgeReportEntryRead[] };
  similarEntryMinimumRank: number; visibilityNote: string; similarityNote: string;
}
