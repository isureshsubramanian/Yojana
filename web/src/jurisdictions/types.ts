export interface RuleKind { kind: string; valueType: string; unit: string | null; description: string }
export interface RegionGroup { regionGroupId: string; code: string; name: string; members: string[]; version: string; createdAt: string; createdBy: string; lastModifiedAt: string; lastModifiedBy: string }
export interface JurisdictionRule {
  ruleId: string; rootRuleId: string; supersedesRuleId: string | null; version: number; kind: string; scope: string;
  countryCode: string | null; subdivisionCode: string | null; regionGroupId: string | null; regionGroupName: string | null;
  buyerClass: string; itemClass: string | null; priceMin: number | null; priceMax: number | null; currency: string | null;
  dateTested: string; effectiveFrom: string; effectiveTo: string | null; value: unknown; sourceCitation: string | null;
  sourceNote: string | null; confirmedByCounsel: boolean; counselConfirmedOn: string | null; createdBy: string;
  approvedBy: string; createdAt: string; versionToken: string; current: boolean;
}
export interface RulePack { rulePackId: string; code: string; name: string; warning: string; ruleCount: number; imported: boolean; importedAt: string | null; importedBy: string | null }
export interface PackPreview { rulePackId: string; name: string; warning: string; regionGroups: { code: string; name: string; members: string[] }[]; rules: { kind: string; scope: string; scopeLabel: string; buyerClass: string; dateTested: string; effectiveFrom: string; value: unknown; sourceCitation: string | null; sourceNote: string | null }[] }
export interface Catalogue { kinds: RuleKind[]; regionGroups: RegionGroup[]; rules: JurisdictionRule[]; rulePacks: RulePack[] }
export interface RuleContext { country: string; subdivision: string | null; buyerClass: string; itemClass: string | null; price: number | null; currency: string | null; contractDate: string | null; purchaseDate: string | null; deliveryDate: string | null; firstSaleDate: string | null }
export interface RuleResolution { ruleId: string; kind: string; value: unknown; scope: string; scopeLabel: string; buyerClass: string; itemClass: string | null; dateTested: string; testedDate: string; precedence: number; effectiveFrom: string; effectiveTo: string | null; sourceCitation: string | null; sourceNote: string | null }

