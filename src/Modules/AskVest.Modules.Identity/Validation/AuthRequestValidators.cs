using AskVest.Modules.Identity.Contracts;
using FluentValidation;

namespace AskVest.Modules.Identity.Validation;

public sealed class SignUpRequestValidator : AbstractValidator<SignUpRequest>
{
    public SignUpRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}

public sealed class ResendVerificationRequestValidator : AbstractValidator<ResendVerificationRequest>
{
    public ResendVerificationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Device).NotNull().SetValidator(new DeviceInfoRequestValidator());
    }
}

public sealed class DeviceInfoRequestValidator : AbstractValidator<DeviceInfoRequest?>
{
    public DeviceInfoRequestValidator()
    {
        RuleFor(x => x!.InstallationId).NotEmpty().MaximumLength(128);
        RuleFor(x => x!.Platform).NotEmpty().MaximumLength(32);
        RuleFor(x => x!.DeviceName).NotEmpty().MaximumLength(120);
        RuleFor(x => x!.AppVersion).MaximumLength(40);
    }
}

public sealed class VerifyDeviceRequestValidator : AbstractValidator<VerifyDeviceRequest>
{
    public VerifyDeviceRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotNull();
        RuleFor(x => x.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
        RuleFor(x => x.InstallationId).NotEmpty().MaximumLength(128);
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class SetPinRequestValidator : AbstractValidator<SetPinRequest>
{
    public SetPinRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Pin).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}

public sealed class ChangePinRequestValidator : AbstractValidator<ChangePinRequest>
{
    public ChangePinRequestValidator()
    {
        RuleFor(x => x.CurrentPin).NotEmpty().Length(6).Matches("^[0-9]{6}$");
        RuleFor(x => x.Pin).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}

public sealed class ResetPinRequestValidator : AbstractValidator<ResetPinRequest>
{
    public ResetPinRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Pin).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}

public sealed class StepUpRequestValidator : AbstractValidator<StepUpRequest>
{
    public StepUpRequestValidator()
    {
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Pin).NotEmpty().Length(6).Matches("^[0-9]{6}$");
        RuleFor(x => x.Binding).MaximumLength(1024);
    }
}

public sealed class AddPhoneRequestValidator : AbstractValidator<AddPhoneRequest>
{
    public AddPhoneRequestValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(32);
    }
}

public sealed class VerifyPhoneRequestValidator : AbstractValidator<VerifyPhoneRequest>
{
    public VerifyPhoneRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$");
    }
}
