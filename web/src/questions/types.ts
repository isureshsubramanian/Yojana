export type QuestionStatus = "open" | "answered" | "closed_without_answer";
export interface QuestionCounts { open: number; answered: number; closedWithoutAnswer: number; total: number }
export interface QuestionListItem { questionId: string; title: string; status: QuestionStatus; askedByName: string; askedAt: string; directedToUserName: string | null; directedToRoleLabel: string | null; replyCount: number }
export interface QuestionPage { recordKind: string; recordId: string; recordName: string; items: QuestionListItem[]; nextCursor: string | null; counts: QuestionCounts }
export interface AllowedActions { canEditQuestion: boolean; canReply: boolean; canAcceptAnswer: boolean; canCloseWithoutAnswer: boolean; canReopen: boolean; canTurnIntoEntry: boolean; entryId: string | null }
export interface QuestionReply { replyId: string; body: string; authorName: string; at: string; editedAt: string | null; revisionCount: number; canEdit: boolean }
export interface QuestionThread { questionId: string; recordKind: string; recordId: string; recordName: string; title: string; body: string; status: QuestionStatus; directedToUserName: string | null; directedToRoleLabel: string | null; askedByName: string; askedAt: string; acceptedReplyId: string | null; closedReason: string | null; closedByName: string | null; closedAt: string | null; questionRevisionCount: number; allowedActions: AllowedActions; replies: QuestionReply[] }
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
export interface QuestionBellItem { questionId: string; title: string; recordKind: string; recordId: string; recordName: string; reason: string; activityAt: string; link: string }
export interface QuestionBellSummary { count: number; items: QuestionBellItem[] }

export type EntryStatus = "draft" | "in_review" | "published" | "retired";
export type EntryTagKind = "role" | "task" | "item_family" | "claim_cause";
export interface EntryTag { kind: EntryTagKind; value: string; label: string }
export interface EntryTagInput { kind: EntryTagKind; value: string }
export interface WrittenAgainst { kind: "policy_version" | "screen"; reference: string; label: string }
export interface EntryRecordLink { recordKind: string; recordId: string; recordName: string; source: boolean }
export interface EntryRound { round: number; version: number; submittedByName: string; submittedAt: string; decision: "approve" | "send_back" | null; reviewerName: string | null; reviewedAt: string | null; note: string | null }
export interface EntryVersion { version: number; savedByName: string; savedAt: string }
export interface EntryAllowedActions { canEdit: boolean; canSubmit: boolean; canApprove: boolean; canSendBack: boolean; canRetire: boolean }
export interface KnowledgeEntry {
  entryId: string; title: string; problem: string; answer: string; steps: string; status: EntryStatus; currentVersion: number;
  authorName: string; createdAt: string; lastSavedAt: string; publishedByName: string | null; publishedAt: string | null; retirementNote: string | null;
  tags: EntryTag[]; writtenAgainst: WrittenAgainst[]; recordLinks: { visible: EntryRecordLink[]; hiddenCount: number };
  source: { questionId: string; title: string; recordKind: string; recordId: string; recordName: string | null; acceptedAnswer: string };
  reviewRounds: EntryRound[]; versions: EntryVersion[]; reviewerRoles: string[]; caseDisclosureTier: string; allowedActions: EntryAllowedActions;
}
export interface KnowledgeEntryListItem { entryId: string; title: string; answer: string; currentVersion: number; authorName: string; reviewerName: string; publishedAt: string; tags: EntryTag[]; rank: number }
export interface KnowledgeFacet { value: string; label: string; count: number }
export interface KnowledgeEntryPage { items: KnowledgeEntryListItem[]; nextCursor: string | null; roles: KnowledgeFacet[]; tasks: KnowledgeFacet[] }
export interface EntryReviewItem { entryId: string; title: string; currentVersion: number; authorName: string; submittedAt: string; round: number }
export interface EntryReviewPage { items: EntryReviewItem[]; nextCursor: string | null }
export interface EntryFieldChange { field: string; before: string; after: string }
export interface EntryTagChoices { roles: EntryTag[]; tasks: EntryTag[]; itemFamilies: EntryTag[]; claimCauses: EntryTag[] }
export interface EntryDraftRequest { title: string; problem: string; answer: string; steps: string; tags: EntryTagInput[]; recordLinks: Array<{ recordKind: string; recordId: string }>; writtenAgainst: WrittenAgainst[] }
