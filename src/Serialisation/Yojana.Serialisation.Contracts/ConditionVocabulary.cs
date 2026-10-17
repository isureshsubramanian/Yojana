namespace Yojana.Serialisation.Contracts;

// Condition grades a unit can carry (MOD-08). Shared by return receipts, grading,
// replacement condition rules and refurbishment release, so every writer and every
// reader agrees on the same closed list.
public static class ConditionGrades
{
    public const string New = "new";
    public const string OpenBox = "open_box";
    public const string RefurbishedA = "refurbished_a";
    public const string RefurbishedB = "refurbished_b";
    public const string RefurbishedC = "refurbished_c";
    public const string Used = "used";
    public const string Remanufactured = "remanufactured";
    public const string Defective = "defective";
    public const string Scrap = "scrap";

    public static readonly IReadOnlyList<string> All =
    [
        New, OpenBox, RefurbishedA, RefurbishedB, RefurbishedC, Used, Remanufactured, Defective, Scrap,
    ];

    public static bool IsKnown(string? grade) => grade is not null && All.Contains(grade);

    public static bool IsRefurbished(string grade) =>
        grade is RefurbishedA or RefurbishedB or RefurbishedC or Remanufactured;

    // Warranty policy rows pre-date the condition vocabulary and store the commercial A/B/C grade.
    // Keep that representation at the policy boundary; condition history remains on the explicit codes above.
    public static string WarrantyPolicyGrade(string grade) => grade switch
    {
        RefurbishedA => "A",
        RefurbishedB => "B",
        RefurbishedC => "C",
        _ => grade,
    };
}
