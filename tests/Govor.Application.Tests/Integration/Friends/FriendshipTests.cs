using Govor.Application.Friends;
using Govor.Application.PrivateUserChats;
using Govor.Application.Exceptions.VerifyFriendship;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Friends;

[TestFixture]
public class FriendshipTests
{
    private TestDatabase _db = null!;
    private FriendRequestCommandService _commands = null!;
    private Mock<IUserPrivateChatsCreator> _chats = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _chats = new Mock<IUserPrivateChatsCreator>();
        _commands = new FriendRequestCommandService(_db.Context, _chats.Object);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task RequestCanBeAcceptedOnlyByAddresseeAndCreatesChat()
    {
        var sent = await _commands.SendAsync(_db.Alice, _db.Bob);
        Assert.That(sent.Value.Status, Is.EqualTo(FriendshipStatus.Pending));
        Assert.That((await _commands.AcceptAsync(sent.Value.Id, _db.Outsider)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _commands.AcceptAsync(sent.Value.Id, _db.Bob)).Value.Status, Is.EqualTo(FriendshipStatus.Accepted));
        Assert.That((await _commands.AcceptAsync(sent.Value.Id, _db.Bob)).IsFailure, Is.True);
        _chats.Verify(c => c.CreateAsync(_db.Bob, _db.Alice), Times.Once);
        var verifier = new VerifyFriendship(_db.Context, NullLogger<VerifyFriendship>.Instance);
        Assert.That(await verifier.TryVerifyAsync(_db.Alice, _db.Bob), Is.True);
        Assert.That(await verifier.TryVerifyAsync(_db.Bob, _db.Alice), Is.True);
        Assert.DoesNotThrowAsync(() => verifier.VerifyAsync(_db.Alice, _db.Bob));
    }

    [Test]
    public async Task DuplicateAndReversePendingRequestsReturnConflict()
    {
        await _commands.SendAsync(_db.Alice, _db.Bob);
        Assert.That((await _commands.SendAsync(_db.Alice, _db.Bob)).Error.Type, Is.EqualTo(ErrorType.Conflict));
        Assert.That((await _commands.SendAsync(_db.Bob, _db.Alice)).Error.Type, Is.EqualTo(ErrorType.Conflict));
        Assert.That(await _db.Context.Friendships.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task SelfRequestDoesNotPersist()
    {
        Assert.That((await _commands.SendAsync(_db.Alice, _db.Alice)).IsFailure, Is.True);
        Assert.That(await _db.Context.Friendships.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RejectedRequestCanBeResentInOtherDirection()
    {
        var sent = (await _commands.SendAsync(_db.Alice, _db.Bob)).Value;
        Assert.That((await _commands.RejectAsync(sent.Id, _db.Alice)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _commands.RejectAsync(sent.Id, _db.Bob)).Value.Status, Is.EqualTo(FriendshipStatus.Rejected));
        var resent = (await _commands.SendAsync(_db.Bob, _db.Alice)).Value;
        Assert.That(resent.Id, Is.EqualTo(sent.Id));
        Assert.That(resent.AddresseeId, Is.EqualTo(_db.Alice));
        Assert.That(resent.Status, Is.EqualTo(FriendshipStatus.Pending));
        _chats.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MissingRequestsReturnNotFound()
    {
        Assert.That((await _commands.AcceptAsync(Guid.NewGuid(), _db.Bob)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _commands.RejectAsync(Guid.NewGuid(), _db.Bob)).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }

    [TestCase(FriendshipStatus.Accepted)]
    [TestCase(FriendshipStatus.Blocked)]
    public async Task EstablishedOrBlockedFriendshipCannotBeRequestedOrRejected(FriendshipStatus status)
    {
        var sent = (await _commands.SendAsync(_db.Alice, _db.Bob)).Value;
        sent.Status = status;
        await _db.Context.SaveChangesAsync();
        Assert.That((await _commands.SendAsync(_db.Bob, _db.Alice)).Error.Type, Is.EqualTo(ErrorType.Conflict));
        Assert.That((await _commands.RejectAsync(sent.Id, _db.Bob)).Error.Type, Is.EqualTo(ErrorType.Conflict));
    }

    [Test]
    public async Task QueriesFilterStatusAndDirection()
    {
        var sent = (await _commands.SendAsync(_db.Alice, _db.Bob)).Value;
        var query = new FriendRequestQueryService(_db.Context);
        var friends = new FriendshipService(_db.Context);
        Assert.That((await query.GetIncomingAsync(_db.Bob)).Select(f => f.Id), Is.EqualTo(new[] { sent.Id }));
        Assert.That(await query.GetIncomingAsync(_db.Alice), Is.Empty);
        Assert.That((await friends.GetPotentialFriendsAsync(_db.Alice)).Select(u => u.Id), Is.EqualTo(new[] { _db.Bob }));
        Assert.That(await friends.GetFriendsAsync(_db.Alice), Is.Empty);
        await _commands.AcceptAsync(sent.Id, _db.Bob);
        Assert.That(await query.GetIncomingAsync(_db.Bob), Is.Empty);
        Assert.That(await query.GetResponsesAsync(_db.Alice), Is.Empty);
        Assert.That((await friends.GetFriendsAsync(_db.Alice)).Select(u => u.Id), Is.EqualTo(new[] { _db.Bob }));
        Assert.That((await friends.GetFriendsAsync(_db.Bob)).Select(u => u.Id), Is.EqualTo(new[] { _db.Alice }));
    }

    [Test]
    public async Task SearchExcludesCurrentUserAndEmptyQuery()
    {
        var service = new FriendshipService(_db.Context);
        Assert.That(await service.SearchUsersAsync(" ", _db.Alice), Is.Empty);
        Assert.That((await service.SearchUsersAsync("-", _db.Alice)).Select(u => u.Id),
            Is.EquivalentTo(new[] { _db.Bob, _db.Outsider }));
    }

    [Test]
    public async Task VerifierRejectsMissingOrPendingFriendshipAndEmptyIds()
    {
        var verifier = new VerifyFriendship(_db.Context, NullLogger<VerifyFriendship>.Instance);
        await _commands.SendAsync(_db.Alice, _db.Bob);
        Assert.That(await verifier.TryVerifyAsync(_db.Alice, _db.Bob), Is.False);
        Assert.That(await verifier.TryVerifyAsync(Guid.Empty, _db.Bob), Is.False);
        Assert.ThrowsAsync<FriendshipException>(() => verifier.VerifyAsync(_db.Alice, _db.Bob));
        Assert.ThrowsAsync<ArgumentException>(() => verifier.VerifyAsync(Guid.Empty, _db.Bob));
    }
}
