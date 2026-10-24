using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Security;

namespace Yojana.Platform.Tests;

/// <summary>ADR-0008 password rules, checked without a database. All people and companies are fictional.</summary>
public sealed class PasswordCheckTests
{
    private static readonly PasswordIdentity Person =
        new("asha.verma@example.invalid", "Asha Verma", ["Northwind Compute Labs"]);

    private static readonly PasswordRules NoClasses = new(12, false, false, false, false);

    private static List<string> Codes(string password, PasswordRules? rules = null) =>
        PasswordCheck.Evaluate(password, rules ?? PasswordRules.Default, Person, 128).Reasons
            .Select(reason => reason.Code).ToList();

    [Fact]
    public void A_long_mixed_password_passes_the_default_rules()
    {
        Assert.Empty(Codes("Brass-Kettle 47 Monsoon"));
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Company_minimum_length_is_between_eight_and_sixty_four(int length, bool valid)
    {
        Assert.Equal(valid, length is >= PasswordRules.ShortestMinimum and <= PasswordRules.LongestMinimum);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Length_counts_against_the_company_minimum(int length, bool tooShort)
    {
        var password = "Tq7!wPz9rLx2Vb#4".Substring(0, length);
        Assert.Equal(tooShort, Codes(password, new PasswordRules(8, false, false, false, false))
            .Contains("password_too_short"));
    }

    [Fact]
    public void The_installation_maximum_still_applies()
    {
        var reasons = PasswordCheck.Evaluate(new string('k', 64) + "Tq7!wPz9rLx2Vb#4" + new string('m', 64),
            NoClasses, Person, 128).Reasons.Select(reason => reason.Code);
        Assert.Contains("password_too_long", reasons);
    }

    [Theory]
    [InlineData("brass kettle 47 monsoon", "password_needs_uppercase")]
    [InlineData("BRASS KETTLE 47 MONSOON", "password_needs_lowercase")]
    [InlineData("Brass Kettle Monsoon!", "password_needs_digit")]
    [InlineData("BrassKettle47Monsoon", "password_needs_symbol")]
    public void Each_class_rule_refuses_when_on(string password, string code)
    {
        Assert.Equal([code], Codes(password));
    }

    [Theory]
    [InlineData("brass kettle 47 monsoon")]
    [InlineData("BRASS KETTLE 47 MONSOON")]
    [InlineData("Brass Kettle Monsoon!")]
    [InlineData("BrassKettle47Monsoon")]
    public void Class_rules_switched_off_refuse_nothing(string password)
    {
        Assert.Empty(Codes(password, NoClasses));
    }

    [Fact]
    public void Every_unmet_rule_is_returned_together()
    {
        Assert.Equal(["password_too_short", "password_needs_uppercase", "password_needs_digit",
            "password_needs_symbol", "password_too_common"], Codes("kettle"));
    }

    [Theory]
    [InlineData("Ünïcode Kettle 47!")]
    [InlineData("செம்மொழி தமிழ் 2026!")]
    public void Letters_outside_english_count_for_the_letter_rules(string password)
    {
        Assert.DoesNotContain(Codes(password), code => code is "password_needs_uppercase" or "password_needs_lowercase");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password1!")]
    [InlineData("Password2026!!")]
    [InlineData("iloveyou12345")]
    public void Common_passwords_are_refused_even_with_decoration(string password)
    {
        Assert.Contains("password_too_common", Codes(password, NoClasses with { MinimumLength = 8 }));
    }

    [Theory]
    [InlineData("asha.verma@example.invalid")]
    [InlineData("Asha.Verma2026!")]
    [InlineData("Verma123456!")]
    [InlineData("Northwind Compute Labs 1")]
    [InlineData("Yojana@2026!")]
    [InlineData("Qwerty-Brass-47")]
    [InlineData("Kettle-987654-x")]
    [InlineData("Zabcdefg-Kettle")]
    [InlineData("aaaaaaaaAb1!")]
    public void Names_keyboard_runs_and_repeats_are_refused(string password)
    {
        Assert.Contains("password_too_predictable", Codes(password, NoClasses with { MinimumLength = 8 }));
    }

    [Fact]
    public void Short_name_parts_and_all_digit_passwords_are_not_called_predictable_on_their_own()
    {
        var person = Person with { DisplayName = "Li Wu" };
        Assert.Empty(PasswordCheck.Evaluate("Li-Brass-Kettle-47", NoClasses, person, 128).Reasons);
        Assert.Empty(PasswordCheck.Evaluate("839201746583", NoClasses, Person, 128).Reasons);
    }

    [Fact]
    public void Refusal_messages_never_echo_the_password()
    {
        const string password = "Password1!";
        Assert.All(PasswordCheck.Evaluate(password, PasswordRules.Default, Person, 128).Reasons,
            reason => Assert.DoesNotContain(password, reason.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_strictest_rules_win_across_companies()
    {
        var lax = new PasswordRules(8, false, true, false, false);
        var strict = new PasswordRules(16, true, false, false, true);
        Assert.Equal(new PasswordRules(16, true, true, false, true), lax.StricterOf(strict));
    }

    [Fact]
    public void The_common_password_list_is_loaded_whole()
    {
        // Distinct after lower-casing: the copy has 99,995 lines, some of them case variants of one password.
        Assert.InRange(CommonPasswords.Count, 95_000, 99_995);
    }
}
