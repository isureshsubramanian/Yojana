namespace Yojana.Serialisation.Contracts;

public sealed record ItemSerialisationSettingsRow(Guid ItemId, string ItemCode, IReadOnlyList<string> ModelNumbers);

public sealed record UpdateItemModelNumbers(IReadOnlyList<string>? ModelNumbers);
