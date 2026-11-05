namespace AskVest.Modules.Identity.Messaging;

public static class MessageTemplates
{
    public static EmailMessage EmailVerification(string to, string code)
        => new(to, "Verify your AskVest email",
            $"Your AskVest verification code is {code}.\n\n" +
            "Enter this code in the app to verify your email address. It expires in 10 minutes.\n\n" +
            "If you did not create an AskVest account, ignore this message - no account will be activated without the code.");

    public static EmailMessage DeviceVerification(string to, string code)
        => new(to, "Verify your new AskVest device",
            $"Your AskVest device verification code is {code}.\n\n" +
            "Enter this code to finish signing in on the new device. It expires in 10 minutes.\n\n" +
            "If you did not try to sign in, change your password immediately and review your active sessions.");

    public static EmailMessage PasswordReset(string to, string code)
        => new(to, "Reset your AskVest password",
            $"Your AskVest password reset code is {code}.\n\n" +
            "Enter this code in the app to choose a new password. It expires in 10 minutes and can be used once.\n\n" +
            "If you did not request a password reset, ignore this message - your password will not change.");

    public static EmailMessage PinReset(string to, string code)
        => new(to, "Reset your AskVest PIN",
            $"Your AskVest PIN reset code is {code}.\n\n" +
            "Enter this code in the app to set a new transaction PIN. It expires in 10 minutes and can be used once.\n\n" +
            "If you did not request a PIN reset, ignore this message and review your security history.");

    public static SmsMessage PhoneVerification(string to, string code)
        => new(to, $"Your AskVest verification code is {code}. It expires in 10 minutes. If you did not request this, ignore this message.");

    public static EmailMessage NewDeviceSignIn(string to, string deviceSummary)
        => new(to, "New sign-in to your AskVest account",
            $"A sign-in to your AskVest account was completed from a new device: {deviceSummary}.\n\n" +
            "If you made this sign-in, no action is needed. If you did not, change your password immediately, revoke the session from your account settings, and contact support.");

    public static EmailMessage PasswordChanged(string to)
        => new(to, "Your AskVest password was changed",
            "The password on your AskVest account was just changed.\n\n" +
            "If you made this change, no action is needed. If you did not, reset your password immediately and review your active sessions.");

    public static EmailMessage PasswordResetCompleted(string to)
        => new(to, "Your AskVest password was reset",
            "The password on your AskVest account was reset using a reset code.\n\n" +
            "If you requested this, no action is needed. If you did not, contact support immediately and review your security history.");

    public static EmailMessage PinChanged(string to)
        => new(to, "Your AskVest PIN was changed",
            "The transaction PIN on your AskVest account was just changed.\n\n" +
            "If you made this change, no action is needed. If you did not, contact support immediately.");

    public static EmailMessage RefreshReuseDetected(string to)
        => new(to, "Security alert: your AskVest session was signed out",
            "We detected that a sign-in token that had already been replaced was used again, which can indicate token theft. " +
            "As a precaution, the affected session was revoked.\n\n" +
            "If you signed in on more than one device at once, simply sign in again. " +
            "If you did not recognise this activity, change your password immediately and review your security history.");

    public static EmailMessage DeletionRequested(string to, int coolingOffDays)
        => new(to, "Your AskVest account deletion was scheduled",
            $"A request to delete your AskVest account was received. Your account will be anonymized in {coolingOffDays} days unless you cancel.\n\n" +
            "If you requested this, no action is needed. If you did not, cancel the deletion from your account settings immediately and change your password.");

    public static EmailMessage SignUpAttemptOnExistingAccount(string to)
        => new(to, "Someone tried to register your email on AskVest",
            "A sign-up attempt was made with your email address, but an AskVest account already exists for it. " +
            "No changes were made to your account.\n\n" +
            "If you attempted this sign-up, sign in instead. If you did not, make sure your password is strong and review your security history.");
}
