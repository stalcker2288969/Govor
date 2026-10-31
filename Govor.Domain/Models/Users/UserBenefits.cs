namespace Govor.Domain.Models.Users;

[Flags]
public enum UserPrivileges : long
{
    None = 0,
    GovorPlus = 1,
    CustomProfileTag = 2
}

public sealed class UserBenefits
{
    public Guid UserId { get; set; }
    public UserPrivileges Privileges { get; set; }
    public long Revision { get; set; }
    public string? TagJson { get; set; }
}

/// <summary>Portable appearance values; clients render these as data, never as CSS or HTML.</summary>
public sealed record ProfileTagStyle
{
    public string Text { get; init; } = "Govor+";
    public string TextColor { get; init; } = "#FFFFFF";
    public string? TextGradientEndColor { get; init; }
    public string BackgroundColor { get; init; } = "#2455DD";
    public string? GradientEndColor { get; init; }
    public int GradientAngle { get; init; } = 90;
    public string BorderColor { get; init; } = "#6688FF";
    public string? BorderGradientEndColor { get; init; }
    public double BorderWidth { get; init; } = 1;
    public string ShadowColor { get; init; } = "#00000080";
    public double ShadowBlur { get; init; } = 4;
    public double ShadowOffsetX { get; init; }
    public double ShadowOffsetY { get; init; } = 1;
    public string FontFamily { get; init; } = "Inter";
    public int FontWeight { get; init; } = 600;
    public int FontSize { get; init; } = 12;
}

public sealed record UserBenefitsView(UserPrivileges Privileges, bool IsGovorPlus,
    bool CanCustomizeProfileTag, ProfileTagStyle? Tag, long Revision = 0);
