using AutoFixture;
using Microsoft.Extensions.Configuration;

[TestFixture]
public class JwtTokenHasherTests
{
    private IJwtTokenHasher _jwtTokenHasher = null!;
    private Fixture _fixture = null!;
    private const string TestSecret = "SuperSecretPepper123!";

    [SetUp]
    public void StartUp()
    {
        _fixture = new Fixture();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "EncryptionOption:Secret", TestSecret }
            })
            .Build();

        _jwtTokenHasher = new JwtTokenHasher(config);
    }

    [Test]
    public void Given_Token_When_Hash_Then_Verify_Should_Return_True()
    {
        // Arrange
        string token = _fixture.Create<string>();

        // Act
        string hash = _jwtTokenHasher.HashToken(token);
        var result = _jwtTokenHasher.VerifyToken(token, hash);

        // Assert
        Assert.That(hash, Is.Not.EqualTo(token));
        Assert.That(result, Is.True);
    }

    [Test]
    public void Given_Invalid_Token_When_Verify_Should_Return_False()
    {
        // Arrange
        string token = _fixture.Create<string>();
        string notToken = _fixture.Create<string>();

        // Act
        string hash = _jwtTokenHasher.HashToken(token);
        var result = _jwtTokenHasher.VerifyToken(notToken, hash);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void Given_Same_Token_When_Hash_Multiple_Times_Should_Be_Equal()
    {
        // Arrange
        string token = _fixture.Create<string>();

        // Act
        string hash1 = _jwtTokenHasher.HashToken(token);
        string hash2 = _jwtTokenHasher.HashToken(token);

        // Assert
        Assert.That(hash1, Is.EqualTo(hash2));
    }

    [Test]
    public void Given_Different_Tokens_When_Hash_Should_Be_Different()
    {
        // Arrange
        string token1 = _fixture.Create<string>();
        string token2 = _fixture.Create<string>();

        // Act
        string hash1 = _jwtTokenHasher.HashToken(token1);
        string hash2 = _jwtTokenHasher.HashToken(token2);

        // Assert
        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    [Test]
    public void Given_Null_Secret_In_Config_Should_Use_Default_Secret()
    {
        // Arrange
        var emptyConfig = new ConfigurationBuilder().Build();
        // Act & Assert 
        Assert.Throws<InvalidOperationException>(() => new JwtTokenHasher(emptyConfig));
    }
}
