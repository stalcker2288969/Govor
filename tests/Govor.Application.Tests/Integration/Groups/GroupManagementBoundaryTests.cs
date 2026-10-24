using Govor.Application.Groups;
using Govor.Application.Infrastructure.Common;
using Govor.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Govor.Application.Tests.Integration.Groups;

[TestFixture]
public class GroupManagementBoundaryTests
{
    private TestDatabase _db = null!;
    private GroupManagementService _service = null!;
    private Guid _group;
    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        _service = new(_db.Context, Mock.Of<INowDateTimeProvider>(c => c.Now == DateTime.UtcNow.Date));
        _group = (await _service.CreateAsync(_db.Alice, "group", "", false, false)).Value.Group.Id;
        await _service.JoinPublicAsync(_db.Bob, _group);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [TestCase("name")]
    [TestCase("description")]
    [TestCase("null-description")]
    public async Task InvalidGroupProfilesCannotCreateOrUpdate(string kind)
    {
        var name = kind == "name" ? new string('x', 101) : "group";
        var description = kind == "description" ? new string('x', 501) : kind == "null-description" ? null : "";
        Assert.That((await _service.CreateAsync(_db.Alice, name, description!, false, false)).IsFailure, Is.True);
        Assert.That((await _service.UpdateAsync(_db.Alice, _group, name, description!, false)).IsFailure, Is.True);
        Assert.That(await _db.Context.ChatGroups.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UnknownActorCannotCreateOrJoinGroups()
    {
        Assert.That((await _service.CreateAsync(Guid.NewGuid(), "name", "", false, false)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.JoinPublicAsync(Guid.NewGuid(), _group)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.UpdateAsync(_db.Alice, Guid.NewGuid(), "name", "", false)).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }

    [Test]
    public async Task MembershipAndAdministrationGuardsProtectOwnerAndOutsiders()
    {
        Assert.That((await _service.MembersAsync(_db.Outsider, _group, false, 0, 20)).IsFailure, Is.True);
        Assert.That((await _service.MembersAsync(_db.Bob, _group, true, 0, 20)).IsFailure, Is.True);
        Assert.That((await _service.LeaveAsync(_db.Outsider, _group)).IsFailure, Is.True);
        Assert.That((await _service.LeaveAsync(_db.Alice, _group)).IsFailure, Is.True);
        Assert.That((await _service.SetAdministratorAsync(_db.Alice, _group, _db.Alice, true)).IsFailure, Is.True);
        Assert.That((await _service.SetAdministratorAsync(_db.Alice, _group, _db.Outsider, true)).IsFailure, Is.True);
        Assert.That((await _service.TransferOwnershipAsync(_db.Alice, _group, _db.Outsider)).IsFailure, Is.True);
        Assert.That((await _service.TransferOwnershipAsync(_db.Alice, _group, _db.Alice)).IsFailure, Is.True);
        Assert.That((await _service.SetBanAsync(_db.Alice, _group, _db.Outsider, true)).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }

    [TestCase(0, 1, "")]
    [TestCase(366, 1, "")]
    [TestCase(1, -1, "")]
    [TestCase(1, 1000001, "")]
    [TestCase(1, 1, null)]
    public async Task InvalidInvitationLimitsAreRejected(int days, int limit, string? description) =>
        Assert.That((await _service.CreateInvitationAsync(_db.Alice, _group, days, limit, description!)).Error.Type, Is.EqualTo(ErrorType.Validation));

    [Test]
    public async Task MissingAndUnauthorizedInvitationsCannotBeViewedOrRevoked()
    {
        Assert.That((await _service.PreviewInvitationAsync(_db.Bob, "unknown")).IsFailure, Is.True);
        Assert.That((await _service.JoinByInvitationAsync(_db.Bob, "unknown")).IsFailure, Is.True);
        Assert.That((await _service.InvitationsAsync(_db.Bob, _group)).IsFailure, Is.True);
        Assert.That((await _service.RevokeInvitationAsync(_db.Bob, _group, Guid.NewGuid())).IsFailure, Is.True);
        Assert.That((await _service.RevokeInvitationAsync(_db.Alice, _group, Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.SetRequiredChannelAsync(_db.Alice, Guid.Empty, true)).IsFailure, Is.True);
    }
}

