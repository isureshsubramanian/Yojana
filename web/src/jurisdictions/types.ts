import * as z from "zod/mini";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();
export const ruleKindSchema = z.object({ kind: text, valueType: text, unit: nullableText, description: text });
export type RuleKind = z.infer<typeof ruleKindSchema>;
export const regionGroupSchema = z.object({ regionGroupId: text, code: text, name: text, members: z.array(text), version: text, createdAt: text, createdBy: text, lastModifiedAt: text, lastModifiedBy: text });
export type RegionGroup = z.infer<typeof regionGroupSchema>;
export const jurisdictionRuleSchema = z.object({
  ruleId: text, rootRuleId: text, supersedesRuleId: nullableText, version: count, kind: text, scope: text,
  countryCode: nullableText, subdivisionCode: nullableText, regionGroupId: nullableText, regionGroupName: nullableText,
  buyerClass: text, itemClass: nullableText, priceMin: z.nullable(count), priceMax: z.nullable(count), currency: nullableText,
  dateTested: text, effectiveFrom: text, effectiveTo: nullableText, value: z.unknown(), sourceCitation: nullableText,
  sourceNote: nullableText, confirmedByCounsel: z.boolean(), counselConfirmedOn: nullableText, createdBy: text,
  approvedBy: text, createdAt: text, versionToken: text, current: z.boolean(),
});
export type JurisdictionRule = z.infer<typeof jurisdictionRuleSchema>;
export const rulePackSchema = z.object({ rulePackId: text, code: text, name: text, warning: text, ruleCount: count, imported: z.boolean(), importedAt: nullableText, importedBy: nullableText });
export type RulePack = z.infer<typeof rulePackSchema>;
const regionGroupPreviewSchema = z.object({ code: text, name: text, members: z.array(text) });
const rulePackRulePreviewSchema = z.object({ kind: text, scope: text, scopeLabel: text, buyerClass: text, dateTested: text, effectiveFrom: text, value: z.unknown(), sourceCitation: nullableText, sourceNote: nullableText });
export const packPreviewSchema = z.object({ rulePackId: text, name: text, warning: text, regionGroups: z.array(regionGroupPreviewSchema), rules: z.array(rulePackRulePreviewSchema) });
export type PackPreview = z.infer<typeof packPreviewSchema>;
export const catalogueSchema = z.object({ kinds: z.array(ruleKindSchema), regionGroups: z.array(regionGroupSchema), rules: z.array(jurisdictionRuleSchema), rulePacks: z.array(rulePackSchema) });
export type Catalogue = z.infer<typeof catalogueSchema>;
export const ruleContextSchema = z.object({ country: text, subdivision: nullableText, buyerClass: text, itemClass: nullableText, price: z.nullable(count), currency: nullableText, contractDate: nullableText, purchaseDate: nullableText, deliveryDate: nullableText, firstSaleDate: nullableText });
export type RuleContext = z.infer<typeof ruleContextSchema>;
export const ruleResolutionSchema = z.object({ ruleId: text, kind: text, value: z.unknown(), scope: text, scopeLabel: text, buyerClass: text, itemClass: nullableText, dateTested: text, testedDate: text, precedence: count, effectiveFrom: text, effectiveTo: nullableText, sourceCitation: nullableText, sourceNote: nullableText });
export type RuleResolution = z.infer<typeof ruleResolutionSchema>;
