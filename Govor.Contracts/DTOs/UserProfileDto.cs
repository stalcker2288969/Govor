namespace Govor.Contracts.DTOs;

public class UserProfileDto
{
    public Guid Id { get; set; }
    public Govor.Domain.Models.Users.UserBenefitsView Benefits { get; set; } = new(0, false, false, null);
    public string Username { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? IconId { get; set; }
    public bool IsOnline { get; set; }
}
