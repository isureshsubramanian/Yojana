export type QuestionStatus = "open" | "answered" | "closed_without_answer";
export interface QuestionCounts { open: number; answered: number; closedWithoutAnswer: number; total: number }
export interface QuestionListItem { questionId: string; title: string; status: QuestionStatus; askedByName: string; askedAt: string; directedToUserName: string | null; directedToRoleLabel: string | null; replyCount: number }
export interface QuestionPage { recordKind: string; recordId: string; recordName: string; items: QuestionListItem[]; nextCursor: string | null; counts: QuestionCounts }
export interface AllowedActions { canEditQuestion: boolean; canReply: boolean; canAcceptAnswer: boolean; canCloseWithoutAnswer: boolean; canReopen: boolean }
export interface QuestionReply { replyId: string; body: string; authorName: string; at: string; editedAt: string | null; revisionCount: number; canEdit: boolean }
export interface QuestionThread { questionId: string; recordKind: string; recordId: string; recordName: string; title: string; body: string; status: QuestionStatus; directedToUserName: string | null; directedToRoleLabel: string | null; askedByName: string; askedAt: string; acceptedReplyId: string | null; closedReason: string | null; closedByName: string | null; closedAt: string | null; questionRevisionCount: number; allowedActions: AllowedActions; replies: QuestionReply[] }
export interface Revision { revisionId: string; body: string; revisedByName: string; revisedAt: string }
export interface StaffChoice { userId: string; name: string; roles: string[] }
export interface RoleChoice { role: string; label: string; people: number }
export interface AddresseePage { people: { items: StaffChoice[]; nextCursor: string | null }; roles: RoleChoice[] }
export interface SearchQuestion { questionId: string; title: string; status: QuestionStatus; recordKind: string; recordId: string; recordName: string; answer: string; rank: number; askedAt: string }
export interface SearchPage { reviewedEntries: unknown[]; questions: SearchQuestion[]; nextCursor: string | null }
export interface RankedCount { key: string; label: string; count: number }
export interface MyQuestion { questionId: string; title: string; status: QuestionStatus; recordKind: string; recordId: string; recordName: string; askedByName: string; directedTo: string; askedAt: string; lastActivityAt: string; replyCount: number; waitingDays: number }
export interface MyQuestionPage { items: MyQuestion[]; nextCursor: string | null; summary: { waitingByAge: RankedCount[]; waitingByRecordKind: RankedCount[] } }
