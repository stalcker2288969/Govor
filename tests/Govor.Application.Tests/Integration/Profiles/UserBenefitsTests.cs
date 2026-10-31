using Govor.Application.Profiles;
using Govor.Application.Tests.Support;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Govor.Application.Tests.Integration.Profiles;

[TestFixture]
public sealed class UserBenefitsTests
{
    private TestDatabase _db = null!;
    private UserBenefitsService _service = null!;
    [SetUp] public void SetUp() { _db = new(); _service = new(_db.Context); }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task GrantCustomizeRevokeAndRegrantUseLiveDatabaseRights()
    {
        Assert.That((await _service.GetAsync(_db.Alice)).Tag, Is.Null);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetTagAsync(_db.Alice, new()));
        var granted = await _service.SetPrivilegesAsync(_db.Alice, UserPrivileges.GovorPlus);
        Assert.That(granted.IsGovorPlus, Is.True);
        Assert.That(granted.Tag!.Text, Is.EqualTo("Govor+"));
        var style = new ProfileTagStyle { Text = "Тест 👑", TextGradientEndColor = "#112233", GradientEndColor = "#998877",
            BorderGradientEndColor = "#ABCDEF80", ShadowBlur = 20, FontFamily = "Roboto", FontSize = 24 };
        var customized = await _service.SetTagAsync(_db.Alice, style);
        Assert.That(customized.Tag, Is.EqualTo(style));
        Assert.That(customized.Revision, Is.GreaterThan(granted.Revision));
        var revoked = await _service.SetPrivilegesAsync(_db.Alice, UserPrivileges.None);
        Assert.That(revoked.Tag, Is.Null);
        Assert.That(revoked.CanCustomizeProfileTag, Is.False);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetTagAsync(_db.Alice, style));
        var restored = await _service.SetPrivilegesAsync(_db.Alice, UserPrivileges.CustomProfileTag);
        Assert.That(restored.IsGovorPlus, Is.False);
        Assert.That(restored.Tag, Is.EqualTo(style));
        Assert.That((await _service.SetTagAsync(_db.Alice, null)).Tag, Is.Null);
        Assert.That((await _service.GetAsync(_db.Bob)).Privileges, Is.EqualTo(UserPrivileges.None));
    }

    [TestCase("text")]
    [TestCase("blank")]
    [TestCase("control")]
    [TestCase("color")]
    [TestCase("color-newline")]
    [TestCase("gradient")]
    [TestCase("text-gradient")]
    [TestCase("border-gradient")]
    [TestCase("font")]
    [TestCase("weight")]
    [TestCase("size")]
    [TestCase("border")]
    [TestCase("blur")]
    [TestCase("offset")]
    [TestCase("nan")]
    [TestCase("angle")]
    public async Task InvalidAppearanceCannotReplacePersistedTag(string reason)
    {
        await _service.SetPrivilegesAsync(_db.Alice, UserPrivileges.GovorPlus);
        var good = new ProfileTagStyle { Text = "saved" };
        await _service.SetTagAsync(_db.Alice, good);
        var bad = reason switch
        {
            "text" => good with { Text = new string('x', 33) },
            "blank" => good with { Text = " " },
            "control" => good with { Text = "a\nb" },
            "color" => good with { TextColor = "url(evil)" },
            "color-newline" => good with { TextColor = "#FFFFFF\n" },
            "gradient" => good with { GradientEndColor = "red" },
            "text-gradient" => good with { TextGradientEndColor = "#fff" },
            "border-gradient" => good with { BorderGradientEndColor = "#abc" },
            "font" => good with { FontFamily = "url(evil)" },
            "weight" => good with { FontWeight = 650 },
            "size" => good with { FontSize = 99 },
            "border" => good with { BorderWidth = -1 },
            "blur" => good with { ShadowBlur = 21 },
            "offset" => good with { ShadowOffsetX = -11 },
            "nan" => good with { ShadowOffsetY = double.NaN },
            _ => good with { GradientAngle = 361 }
        };
        Assert.ThrowsAsync<ArgumentException>(() => _service.SetTagAsync(_db.Alice, bad));
        Assert.That((await _service.GetAsync(_db.Alice)).Tag, Is.EqualTo(good));
    }

    [Test]
    public async Task UnknownRightsAndUsersAreRejectedAndDeletingUserCascadesBenefits()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.SetPrivilegesAsync(_db.Alice, (UserPrivileges)4));
        Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SetPrivilegesAsync(Guid.NewGuid(), UserPrivileges.GovorPlus));
        Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetAsync(Guid.NewGuid()));
        await _service.SetPrivilegesAsync(_db.Outsider, UserPrivileges.GovorPlus);
        await _db.Context.Users.Where(u => u.Id == _db.Outsider).ExecuteDeleteAsync();
        Assert.That(await _db.Context.UserBenefits.AnyAsync(), Is.False);
    }

    [Test]
    public async Task ConcurrentFirstGrantReturnsConflictWithoutOverwritingWinner()
    {
        using var competing = _db.CreateContext(new BeforeSave(async () =>
        {
            _db.Context.UserBenefits.Add(new UserBenefits { UserId = _db.Alice, Privileges = UserPrivileges.CustomProfileTag, Revision = 1 });
            await _db.Context.SaveChangesAsync();
        }));
        Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new UserBenefitsService(competing).SetPrivilegesAsync(_db.Alice, UserPrivileges.GovorPlus));
        Assert.That((await _service.GetAsync(_db.Alice)).Privileges, Is.EqualTo(UserPrivileges.CustomProfileTag));
        Assert.That(competing.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task FailedInitialGrantDoesNotCreatePrivileges()
    {
        using var failed = _db.CreateContext(new FailDatabaseCommand(sql => sql.Contains("INSERT INTO \"UserBenefits\"", StringComparison.Ordinal)));
        Assert.ThrowsAsync<DbUpdateException>(() => new UserBenefitsService(failed).SetPrivilegesAsync(_db.Alice, UserPrivileges.GovorPlus));
        Assert.That((await _service.GetAsync(_db.Alice)).Privileges, Is.EqualTo(UserPrivileges.None));
        Assert.That(failed.ChangeTracker.Entries(), Is.Empty);
    }

    private sealed class BeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await action();
            return result;
        }
    }
}
