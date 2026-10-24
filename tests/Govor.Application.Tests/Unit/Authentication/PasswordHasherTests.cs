using AutoFixture;

namespace  Govor.Application.Tests.Services;

[TestFixture]
public class PasswordHasherTests
{
    private IPasswordHasher _passwordHasher = null!;
    private Fixture _fixture = null!;
    [SetUp]
    public void StarUp()
    {
        _fixture = new Fixture();
        _passwordHasher = new PasswordHasher();
    }
    
    [Test]
    public void Given_Password_When_Hash_Then_Hash_And_Verify_Then_Result_Should_Be_True()
    {
        // Arrange
        string password = _fixture.Create<string>();
        
        // Act 
        string hash = _passwordHasher.Hash(password);
        
        var result = _passwordHasher.Verify(password, hash);
        
        // Assert 
        Assert.That(hash, Is.Not.EqualTo(password));
        Assert.That(result, Is.True);
    }

    [Test]
    public void Given_Password_NotPassword_When_Hash_Should_Not_Be_True_Then_Result_Should_Be_False()
    {
        // Arrange 
        string password = _fixture.Create<string>();
        string notPassword = _fixture.Create<string>();
        
        // Act 
        string hash = _passwordHasher.Hash(password);
        
        var result = _passwordHasher.Verify(notPassword, hash);
        
        // Assert 
        Assert.That(result, Is.False);
    }
}
