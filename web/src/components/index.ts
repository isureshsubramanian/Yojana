// Shared components (MOD-02, P0-T11). Screens import from here; nothing here knows a business rule.
export { ApprovalInbox } from "./ApprovalInbox";
export { ActionDialog } from "./ActionDialog";
export { DataGrid, type GridColumn } from "./DataGrid";
export { FindBar } from "./FindBar";
export { FormActions } from "./FormActions";
export { KpiStrip } from "./KpiStrip";
export { Pager } from "./Pager";
export { PresenceAdvisory, PresenceChips, useDocumentPresence } from "./Presence";
export type { PresenceMode, PresenceUser } from "./Presence";
export { ScanInput } from "./ScanInput";
export { Shortcut, useModEnter } from "./Shortcut";
export { SelectionPreview } from "./SelectionPreview";
export { StatusBadge } from "./StatusBadge";
export { TimelineRail } from "./TimelineRail";
export { SearchPalette } from "./search/SearchPalette";
export { openSearch, closeSearch } from "./search/searchStore";
export type * from "./types";
