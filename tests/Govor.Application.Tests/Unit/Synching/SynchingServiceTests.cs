using Govor.Application.Synching;

namespace Govor.Application.Tests.Services;

[TestFixture]
public class SynchingServiceTests
{
    private readonly SynchingService _sut; // System Under Test

    public SynchingServiceTests()
    {
        _sut = new SynchingService();
    }

    [Test]
    public void NormalizeNewlines_ShouldReturnEmptyString_WhenInputIsEmpty()
    {
        // Act
        var result = _sut.NormalizeNewlines(string.Empty);

        // Assert
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void NormalizeNewlines_ShouldReturnNull_WhenInputIsNull()
    {
        // Arrange
        string input = null;

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void NormalizeNewlines_ShouldNotChangeUnixStyleNewlines_WhenInputIsLF()
    {
        // Arrange
        const string input = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.EqualTo(input));
    }

    [Test]
    public void NormalizeNewlines_ShouldConvertWindowsStyleNewlines_WhenInputIsCRLF()
    {
        // Arrange
        const string input = "Line 1\r\nLine 2\r\nLine 3";
        const string expected = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeNewlines_ShouldConvertMacStyleNewlines_WhenInputIsCR()
    {
        // Arrange
        const string input = "Line 1\rLine 2\rLine 3";
        const string expected = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeNewlines_ShouldHandleMixedNewlines_WhenInputIsMixed()
    {
        // Arrange
        const string input = "Line 1\r\nLine 2\rLine 3\nLine 4";
        const string expected = "Line 1\nLine 2\nLine 3\nLine 4";

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeNewlines_ShouldHandleTextWithoutNewlines()
    {
        // Arrange
        const string input = "This is a single line of text.";

        // Act
        var result = _sut.NormalizeNewlines(input);

        // Assert
        Assert.That(result, Is.EqualTo(input));
    }
}
