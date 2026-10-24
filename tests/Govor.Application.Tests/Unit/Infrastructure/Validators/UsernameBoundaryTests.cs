using Govor.Domain.Common;
using Microsoft.Extensions.Configuration;

namespace Govor.Application.Tests.Unit.Infrastructure.Validators;

[TestFixture]
public class UsernameBoundaryTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["UsernameModeration:BlockedExact:0"] = "админ",
        ["UsernameModeration:BlockedContains:0"] = "запрет",
        ["UsernameModeration:Reserved:0"] = "логин"
    };
    private static UsernameValidator Validator() => new(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build());

    [Test]
    public void NameLengthBoundariesAndNullReturnValidationResults()
    {
        var validator = Validator();
        var longest = string.Concat(Enumerable.Repeat("Иван", 11));
        Assert.That(validator.Validate("Иван").IsSuccess, Is.True);
        Assert.That(validator.Validate(longest).IsSuccess, Is.True);
        foreach (var invalid in new[] { null, "", "   ", "Ива", longest + "а" })
        {
            var result = validator.Validate(invalid!);
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Error.Type, Is.EqualTo(ErrorType.Validation));
            Assert.That(validator.TryValidate(invalid!), Is.False);
        }
    }

    [TestCase("Абббб", true)]
    [TestCase("Аббббб", false)]
    [TestCase("Иван0", true)]
    [TestCase("Админ", false)]
    [TestCase("ЛОГИН", false)]
    [TestCase("Запрет123", false)]
    public void SyntaxRepetitionAndModerationAreApplied(string name, bool valid) =>
        Assert.That(Validator().Validate(name).IsSuccess, Is.EqualTo(valid));

    [TestCase("BlockedExact")]
    [TestCase("BlockedContains")]
    [TestCase("Reserved")]
    public void MissingModerationSectionFailsConfiguration(string section)
    {
        var settings = Settings();
        settings.Remove("UsernameModeration:" + section + ":0");
        Assert.Throws<InvalidOperationException>(() => new UsernameValidator(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }
}

