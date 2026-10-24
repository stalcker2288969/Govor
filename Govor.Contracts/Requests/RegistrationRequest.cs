using System.ComponentModel.DataAnnotations;
using Govor.Domain.Common.Constants;

namespace Govor.Contracts.Requests;

public record RegistrationRequest : IValidatableObject
{
    [Required]
    [StringLength(UserConstants.MAX_LENGHT_OF_NAME,
        MinimumLength = UserConstants.MIN_LENGHT_OF_NAME, 
        ErrorMessage = "Username must be between 4 and 44 characters.")]
    public string Name { get; init; }
    [MinLength(8)]
    public string? Password { get; init; }
    public string? SrpSalt { get; init; }
    public string? SrpIdentity { get; init; }
    public string? SrpVerifier { get; init; }
    [MinLength(InvitationConstants.MIN_INVITATION_LENGTH)]
    public string InviteLink { get; init; }
    public string DeviceInfo { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var proofRegistration = SrpSalt is not null || SrpIdentity is not null || SrpVerifier is not null;
        if (proofRegistration)
        {
            if (Password is not null || SrpSalt is null || SrpIdentity is null || SrpVerifier is null)
                yield return new ValidationResult("Provide complete SRP credentials without a plaintext password.");
        }
        else if (string.IsNullOrWhiteSpace(Password))
            yield return new ValidationResult("Password is required.", [nameof(Password)]);
    }
}
