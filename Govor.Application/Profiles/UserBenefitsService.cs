using System.Text.Json;
using System.Text.RegularExpressions;
using Govor.Domain;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Profiles;

/// <summary>Privileges are read from the database on every operation so revocation takes effect immediately.</summary>
public sealed class UserBenefitsService(GovorDbContext db)
{
    private const UserPrivileges Known = UserPrivileges.GovorPlus | UserPrivileges.CustomProfileTag;
    private static readonly string[] SupportedFonts = ["Inter", "Roboto", "Arial", "Noto Sans", "serif", "sans-serif", "monospace"];

    public async Task<UserBenefitsView> GetAsync(Guid userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId)) throw new KeyNotFoundException("User not found.");
        var row = await db.UserBenefits.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId);
        return View(row);
    }

    // Call only from an administrator-authorized boundary. No self-service grant endpoint exists.
    public async Task<UserBenefitsView> SetPrivilegesAsync(Guid userId, UserPrivileges privileges)
    {
        if ((privileges & ~Known) != 0) throw new ArgumentException("Unknown privilege bits.");
        if (!await db.Users.AnyAsync(u => u.Id == userId)) throw new KeyNotFoundException("User not found.");
        var count = await db.UserBenefits.Where(x => x.UserId == userId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.Privileges, privileges).SetProperty(x => x.Revision, x => x.Revision + 1));
        if (count == 0)
        {
            db.UserBenefits.Add(new UserBenefits { UserId = userId, Privileges = privileges, Revision = 1 });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // Concurrent first grants compete on the user PK; don't silently overwrite the winner.
                if (await db.UserBenefits.AnyAsync(x => x.UserId == userId))
                    throw new DbUpdateConcurrencyException("Privileges were created concurrently.");
                throw;
            }
        }
        return await GetAsync(userId);
    }

    public async Task<UserBenefitsView> SetTagAsync(Guid userId, ProfileTagStyle? tag)
    {
        if (tag is not null) Validate(tag);
        // Conditional UPDATE makes revocation and customization serialize at the row in the database.
        var json = tag is null ? null : JsonSerializer.Serialize(tag);
        var count = await db.UserBenefits.Where(x => x.UserId == userId && (x.Privileges & Known) != 0)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TagJson, json).SetProperty(x => x.Revision, x => x.Revision + 1));
        if (count == 0) throw new UnauthorizedAccessException("CustomProfileTag privilege or Govor+ is required.");
        return await GetAsync(userId);
    }

    private static UserBenefitsView View(UserBenefits? row)
    {
        var privileges = row?.Privileges ?? UserPrivileges.None;
        var plus = privileges.HasFlag(UserPrivileges.GovorPlus);
        var custom = (privileges & Known) != 0;
        var tag = custom && row?.TagJson is { } json ? JsonSerializer.Deserialize<ProfileTagStyle>(json) :
            plus ? new ProfileTagStyle() : null;
        return new(privileges, plus, custom, tag, row?.Revision ?? 0);
    }

    private static void Validate(ProfileTagStyle tag)
    {
        static bool Color(string? value) => value is not null && Regex.IsMatch(value, @"\A#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?\z", RegexOptions.CultureInvariant);
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if (string.IsNullOrWhiteSpace(tag.Text) || tag.Text.Length > 32 || tag.Text.Any(char.IsControl) ||
            !Color(tag.TextColor) || !Color(tag.BackgroundColor) || !Color(tag.BorderColor) || !Color(tag.ShadowColor) ||
            (tag.GradientEndColor is not null && !Color(tag.GradientEndColor)) || tag.GradientAngle is < 0 or > 360 ||
            (tag.TextGradientEndColor is not null && !Color(tag.TextGradientEndColor)) ||
            (tag.BorderGradientEndColor is not null && !Color(tag.BorderGradientEndColor)) ||
            !Range(tag.BorderWidth, 0, 4) || !Range(tag.ShadowBlur, 0, 20) ||
            !Range(tag.ShadowOffsetX, -10, 10) || !Range(tag.ShadowOffsetY, -10, 10) ||
            !SupportedFonts.Contains(tag.FontFamily, StringComparer.Ordinal) ||
            tag.FontSize is < 10 or > 24 || tag.FontWeight is < 100 or > 900 || tag.FontWeight % 100 != 0)
            throw new ArgumentException("Invalid tag text, color, font or appearance range.");
    }
}
