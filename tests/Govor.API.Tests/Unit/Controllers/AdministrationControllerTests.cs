using Govor.API.Controllers.AdminStuff;
using Govor.Application.Infrastructure.AdminsStuff;
using Govor.Contracts.Requests;
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses.Admins;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class AdministrationControllerTests
{
    private Mock<IInvitationGenerator> _generator = null!;
    private Mock<IInvitationGetter> _getter = null!;
    private InviteUserController _invites = null!;
    private readonly DateTime _end = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [TearDown]
    public void TearDown() => _invites.Dispose();

    [SetUp]
    public void Setup()
    {
        _generator = new(); _getter = new();
        _invites = Create("admin-secret");
    }

    private InviteUserController Create(string? key) => new(_generator.Object, _getter.Object,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InvitationAdmin:Key"] = key }).Build(),
        NullLogger<InviteUserController>.Instance);

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvitationForwardsAllFields(bool withKey)
    {
        _generator.Setup(g => g.GenerateInvitationCode(_end, 12, true, "description")).ReturnsAsync("code");
        var response = withKey
            ? await _invites.InvitationWithKey(new() { AdminKey = "admin-secret", EndDate = _end, MaxParticipants = 12, IsAdmin = true, Description = "description" })
            : await _invites.Invitation(new() { EndDate = _end, MaxParticipants = 12, IsAdmin = true, Description = "description" });
        Assert.That(((OkObjectResult)response).Value, Is.EqualTo("code"));
        _generator.Verify(g => g.GenerateInvitationCode(_end, 12, true, "description"), Times.Once);
    }

    [TestCase(null, "admin-secret", 503)]
    [TestCase(" ", "admin-secret", 503)]
    [TestCase("admin-secret", "", 401)]
    [TestCase("admin-secret", "ADMIN-SECRET", 401)]
    public async Task InvalidAdminKeyNeverGeneratesInvitation(string? configured, string supplied, int status)
    {
        var result = (ObjectResult)await Create(configured).InvitationWithKey(new() { AdminKey = supplied });
        Assert.That(result.StatusCode, Is.EqualTo(status));
        _generator.VerifyNoOtherCalls();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GeneratorExceptionProducesBadRequest(bool withKey)
    {
        _generator.Setup(g => g.GenerateInvitationCode(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("failure"));
        var result = withKey ? await _invites.InvitationWithKey(new() { AdminKey = "admin-secret" }) : await _invites.Invitation(new());
        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvitationListsMapFieldsAndFilterActive(bool activeOnly)
    {
        var active = new Invitation { Id = Guid.NewGuid(), Code = "code", Description = "desc", IsAdmin = true, MaxParticipants = 7, DateCreated = _end.AddDays(-1), EndDate = _end, Users = [new User()] };
        var inactive = new Invitation { Id = Guid.NewGuid(), IsActive = false };
        _getter.Setup(g => g.GetAllAsync()).ReturnsAsync([active, inactive]);
        var result = activeOnly ? await _invites.GetAllActiveInvitations() : await _invites.GetAllInvitations();
        var dtos = (List<InvitationResponses>)((OkObjectResult)result).Value!;
        Assert.That(dtos, Has.Count.EqualTo(activeOnly ? 1 : 2));
        var dto = dtos.Single(x => x.Id == active.Id);
        Assert.Multiple(() => {
            Assert.That(dto.Code, Is.EqualTo(active.Code)); Assert.That(dto.Description, Is.EqualTo(active.Description));
            Assert.That(dto.ParticipantCount, Is.EqualTo(1)); Assert.That(dto.IsAdmin, Is.True);
            Assert.That(dto.MaxParticipants, Is.EqualTo(7)); Assert.That(dto.CreatedAt, Is.EqualTo(active.DateCreated)); Assert.That(dto.EndAt, Is.EqualTo(_end));
        });
        _getter.Setup(g => g.FindByIdAsync(active.Id)).ReturnsAsync(active);
        Assert.That(((InvitationResponses)((OkObjectResult)await _invites.GetInvitationById(active.Id)).Value!).Id, Is.EqualTo(active.Id));
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)]
    public async Task InvitationReadExceptionsProduceBadRequest(int operation)
    {
        _getter.Setup(g => g.GetAllAsync()).ThrowsAsync(new Exception("failure"));
        _getter.Setup(g => g.FindByIdAsync(It.IsAny<Guid>())).ThrowsAsync(new Exception("failure"));
        var result = operation switch { 0 => await _invites.GetAllInvitations(), 1 => await _invites.GetAllActiveInvitations(), _ => await _invites.GetInvitationById(Guid.NewGuid()) };
        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task MissingInvitationReturnsNotFound()
    {
        _getter.Setup(g => g.FindByIdAsync(It.IsAny<Guid>())).ReturnsAsync(Result.Failure<Invitation>(Error.NotFound("missing", "missing")));
        Assert.That(await _invites.GetInvitationById(Guid.NewGuid()), Is.TypeOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UserDtosHandleMissingAndAdminInvitations()
    {
        var users = new Mock<IUsersAdministration>();
        var a = new User { Id = Guid.NewGuid(), Username = "a", Description = "desc", Invite = new Invitation { IsAdmin = true } };
        var b = new User { Id = Guid.NewGuid(), Username = "b" };
        users.Setup(u => u.GetAllUsersAsync()).ReturnsAsync([a,b]);
        users.Setup(u => u.GetUserById(a.Id)).ReturnsAsync(a);
        var controller = new UsersController(NullLogger<UsersController>.Instance, users.Object);
        var dtos = (List<UserResponse>)((OkObjectResult)await controller.AllUsers()).Value!;
        Assert.That(dtos.Select(x => x.IsAdmin), Is.EqualTo(new[] { true, false }));
        Assert.That(((UserResponse)((OkObjectResult)await controller.GetUserById(a.Id)).Value!).Username, Is.EqualTo("a"));
        Assert.That(await controller.GetUserById(b.Id), Is.TypeOf<NotFoundResult>());
        Assert.That(await controller.SetNewPassword(b.Id, new() { Password = "password" }), Is.TypeOf<NotFoundResult>());
        Assert.That(await controller.SetNewPassword(a.Id, new() { Password = "password" }), Is.TypeOf<NoContentResult>());
        users.Verify(u => u.SetPasswordAsync(a.Id, "password"), Times.Once);
        users.Verify(u => u.SetPasswordAsync(b.Id, It.IsAny<string>()), Times.Never);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)]
    public async Task UserAdministrationErrorsReturn500(int operation)
    {
        var users = new Mock<IUsersAdministration>();
        users.Setup(u => u.GetAllUsersAsync()).ThrowsAsync(new Exception("failure"));
        users.Setup(u => u.GetUserById(It.IsAny<Guid>())).ThrowsAsync(new Exception("failure"));
        var controller = new UsersController(NullLogger<UsersController>.Instance, users.Object);
        var result = operation switch { 0 => await controller.AllUsers(), 1 => await controller.GetUserById(Guid.NewGuid()), _ => await controller.SetNewPassword(Guid.NewGuid(), new() { Password = "password" }) };
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
    }
}
