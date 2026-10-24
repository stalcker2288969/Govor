using Govor.Application.Authentication.Exceptions;
using Govor.Application.Infrastructure.Validators;
using Microsoft.Extensions.Configuration;

namespace Govor.Application.Tests.Infrastructure.Validators;

[TestFixture]
public class UsernameValidatorTests
{
    private UsernameValidator _validator = null!;
    [SetUp]
    public void SetUp()
    {
        var configData = new Dictionary<string, string?>
        {
            ["UsernameModeration:BlockedExact:0"] = "админ",
            ["UsernameModeration:BlockedExact:1"] = "модератор",

            ["UsernameModeration:BlockedContains:0"] = "гитлер",
            ["UsernameModeration:BlockedContains:1"] = "наци",

            ["UsernameModeration:Reserved:0"] = "логин",
            ["UsernameModeration:Reserved:1"] = "регистрация"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _validator = new UsernameValidator(config);
    }

    [TestCase("Иван")]
    [TestCase("Алексей")]
    [TestCase("Ёжик")]
    [TestCase("Иван123")] // содержит цифры
    public void Validate_ValidUsernames_ReturnsSuccess(string username)
    {
        Assert.That(_validator.Validate(username).IsSuccess, Is.True);
    }

    [TestCase("Ivan")] // не кириллица
    [TestCase("123Иван")] // начинается не с буквы
    [TestCase("!@#$")] // спецсимволы
    [TestCase("")] // пусто
    [TestCase("И")] // меньше минимума
    [TestCase("ИванИванИванИванИванИванИванИванИванИванИванИванИван")] // больше максимума (44 символа)
    public void Validate_InvalidUsernames_ReturnsFailure(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Ааааааа")] 
    [TestCase("Бbbbb")] 
    public void Validate_RepeatingCharacters_ReturnsFailure(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Админ")]
    [TestCase("Модератор")]
    public void Validate_BlockedExact_ReturnsFailure(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Гитлер123")]
    [TestCase("Нацист")]
    public void Validate_BlockedContains_ReturnsFailure(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Логин")]
    [TestCase("Регистрация")]
    public void Validate_ReservedNames_ReturnsFailure(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Г1тлер")]
    public void Validate_Normalization_ShouldDetectBlockedWord(string username)
    {
        var result = _validator.Validate(username);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Type, Is.EqualTo(Govor.Domain.Common.ErrorType.Validation));
    }

    [TestCase("Иван", ExpectedResult = true)]
    [TestCase("1234", ExpectedResult = false)]
    [TestCase("Админ", ExpectedResult = false)]
    [TestCase("Гитлер123", ExpectedResult = false)]
    public bool TryValidate_ShouldReturnExpectedResult(string username)
    {
        return _validator.TryValidate(username);
    }
}
