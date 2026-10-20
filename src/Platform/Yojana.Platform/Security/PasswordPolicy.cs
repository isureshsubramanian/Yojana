using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

public sealed class PasswordPolicy(SignInOptions options) : IPasswordPolicy
{
    public PasswordPolicyResult Validate(string password, string email, string displayName)
    {
        var reasons = new List<PasswordPolicyReason>();
        if (password.Length < options.MinimumPasswordLength)
            reasons.Add(new("password_too_short", $"Use at least {options.MinimumPasswordLength} characters."));
        if (password.Length > options.MaximumPasswordLength)
            reasons.Add(new("password_too_long", $"Use no more than {options.MaximumPasswordLength} characters."));
        if (string.Equals(password, email.Trim(), StringComparison.OrdinalIgnoreCase))
            reasons.Add(new("password_matches_email", "The password must not be the e-mail address."));
        if (string.Equals(password, displayName.Trim(), StringComparison.OrdinalIgnoreCase))
            reasons.Add(new("password_matches_display_name", "The password must not be the display name."));
        return new PasswordPolicyResult(reasons.Count == 0, reasons);
    }
}
