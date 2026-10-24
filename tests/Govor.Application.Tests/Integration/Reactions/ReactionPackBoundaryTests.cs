using Govor.Application.Infrastructure.Common;
using Govor.Application.Reactions;
using Govor.Application.Tests.Support;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Reactions;

[TestFixture]
public class ReactionPackBoundaryTests
{
    private TestDatabase _db = null!;
    private ReactionPackService _service = null!;
    private Mock<IReactionMediaProcessor> _processor = null!;
    private Mock<IStorageService> _storage = null!;
    private readonly DateTime _now = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
    private ReactionPackService Service(GovorDbContext db) => new(db, Mock.Of<INowDateTimeProvider>(c => c.Now == _now),
        _processor.Object, _storage.Object, NullLogger<ReactionPackService>.Instance);
    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        var invite = new Invitation { Id = Guid.NewGuid(), Code = "admin", Description = "", IsAdmin = true };
        _db.Context.Invitations.Add(invite);
        (await _db.Context.Users.SingleAsync(u => u.Id == _db.Alice)).InviteId = invite.Id;
        await _db.Context.SaveChangesAsync();
        _processor = new Mock<IReactionMediaProcessor>(MockBehavior.Strict);
        _storage = new Mock<IStorageService>(MockBehavior.Strict);
        _service = Service(_db.Context);
    }
    [TearDown] public void TearDown() => _db.Dispose();
    private async Task<ReactionPack> Pack() => (await _service.CreateAsync(_db.Alice, "Custom", "")).Value;

    [Test]
    public async Task UpdateDisableAndHistoryPreserveDefinitions()
    {
        var pack = await Pack();
        var item = (await _service.AddEmojiAsync(_db.Alice, pack.Id, "like", "👍")).Value;
        var updated = await _service.UpdateAsync(_db.Alice, pack.Id, " Renamed ", " details ", true);
        Assert.That(updated.Value.Name, Is.EqualTo("Renamed"));
        Assert.That(updated.Value.Description, Is.EqualTo("details"));
        Assert.That((await _service.DisableReactionAsync(_db.Alice, pack.Id, item.Id)).IsSuccess, Is.True);
        Assert.That((await _service.GetAsync(pack.Id)).Value.Reactions, Is.Empty);
        Assert.That((await _service.GetAsync(pack.Id, includeDisabled: true)).Value.Reactions.Single().Id, Is.EqualTo(item.Id));
        Assert.That((await _service.GetReactionAsync(item.Id)).Value.IsEnabled, Is.False);
        Assert.That((await _service.UpdateAsync(_db.Alice, pack.Id, "Renamed", "", false)).IsSuccess, Is.True);
        Assert.That((await _service.GetAsync(pack.Id)).IsFailure, Is.True);
        Assert.That((await _service.GetAsync(pack.Id, true)).IsSuccess, Is.True);
        Assert.That((await _service.GetSharedAsync(pack.ShareCode)).IsFailure, Is.True);
        Assert.That((await _service.SubscribeAsync(_db.Bob, pack.Id, true)).IsFailure, Is.True);
        Assert.That((await _service.ListAsync(_db.Bob, includeDisabled: true)).Any(p => p.Id == pack.Id), Is.True);
    }

    [Test]
    public async Task DefaultPackCannotBeChangedDisabledOrUninstalled()
    {
        var pack = await _db.Context.ReactionPacks.SingleAsync(p => p.IsDefault);
        var item = await _db.Context.ReactionItems.FirstAsync(r => r.PackId == pack.Id);
        Assert.That((await _service.UpdateAsync(_db.Alice, pack.Id, "default", "", true)).IsFailure, Is.True);
        Assert.That((await _service.DisableAsync(_db.Alice, pack.Id)).IsFailure, Is.True);
        Assert.That((await _service.DisableReactionAsync(_db.Alice, pack.Id, item.Id)).IsFailure, Is.True);
        Assert.That((await _service.SubscribeAsync(_db.Bob, pack.Id, false)).IsFailure, Is.True);
        Assert.That((await _service.SubscribeAsync(_db.Bob, pack.Id, true)).IsSuccess, Is.True);
    }

    [Test]
    public async Task MissingPackOrItemReturnsFailureForEveryMutation()
    {
        var id = Guid.NewGuid();
        Assert.That((await _service.GetAsync(id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.GetReactionAsync(id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.GetSharedAsync("missing")).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.UpdateAsync(_db.Alice, id, "name", "", true)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.DisableAsync(_db.Alice, id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.DisableReactionAsync(_db.Alice, id, id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.AddEmojiAsync(_db.Alice, id, "name", "👍")).IsFailure, Is.True);
    }

    [Test]
    public async Task NonAdminCannotMutatePacksOrUploadFiles()
    {
        var pack = await Pack();
        Assert.That((await _service.CreateAsync(_db.Bob, "name", "")).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.UpdateAsync(_db.Bob, pack.Id, "name", "", true)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.DisableAsync(_db.Bob, pack.Id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.DisableReactionAsync(_db.Bob, pack.Id, Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.AddEmojiAsync(_db.Bob, pack.Id, "name", "👍")).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.AddMediaAsync(_db.Bob, pack.Id, "name", new byte[] { 1 })).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        _processor.VerifyNoOtherCalls();
        _storage.VerifyNoOtherCalls();
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("letters")]
    [TestCase("👍👍")]
    public async Task InvalidEmojiDoesNotCreateReaction(string emoji)
    {
        var pack = await Pack();
        Assert.That((await _service.AddEmojiAsync(_db.Alice, pack.Id, "name", emoji)).IsFailure, Is.True);
        Assert.That(await _db.Context.ReactionItems.CountAsync(r => r.PackId == pack.Id), Is.Zero);
    }

    [TestCase("name")]
    [TestCase("description")]
    [TestCase("reaction")]
    public async Task OversizedTextIsRejected(string field)
    {
        var pack = await Pack();
        if (field == "name") Assert.That((await _service.CreateAsync(_db.Alice, new string('x', 101), "")).IsFailure, Is.True);
        if (field == "description") Assert.That((await _service.UpdateAsync(_db.Alice, pack.Id, "name", new string('x', 501), true)).IsFailure, Is.True);
        if (field == "reaction") Assert.That((await _service.AddEmojiAsync(_db.Alice, pack.Id, "", "👍")).IsFailure, Is.True);
    }

    [Test]
    public async Task DisabledEntriesStillCountTowardPackLimit()
    {
        var pack = await Pack();
        _db.Context.ReactionItems.AddRange(Enumerable.Range(0, 100).Select(i => new ReactionItem {
            Id = Guid.NewGuid(), PackId = pack.Id, Name = "item-" + i, Code = ":test_" + i + ":", Emoji = "👍", IsEnabled = false }));
        await _db.Context.SaveChangesAsync();
        Assert.That((await _service.AddEmojiAsync(_db.Alice, pack.Id, "overflow", "👍")).IsFailure, Is.True);
    }

    [Test]
    public async Task ProcessorFailureIsReturnedBeforeStorageWrite()
    {
        var pack = await Pack();
        var error = Error.Validation("Media.Invalid", "bad image");
        _processor.Setup(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ProcessedReactionMedia>(error));
        Assert.That((await _service.AddMediaAsync(_db.Alice, pack.Id, "name", new byte[] { 1 })).Error, Is.SameAs(error));
        _storage.VerifyNoOtherCalls();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedDatabaseWriteRollsBackMediaAndAttemptsStorageCleanup(bool cleanupFails)
    {
        var pack = await Pack();
        _processor.Setup(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessedReactionMedia(new byte[] { 1 }, ReactionKind.Image, "image/png", ".png", 10, 10, 0));
        _storage.Setup(s => s.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>())).ReturnsAsync("failed.png");
        if (cleanupFails) _storage.Setup(s => s.RemoveAsync("failed.png")).ThrowsAsync(new IOException("cleanup failure"));
        else _storage.Setup(s => s.RemoveAsync("failed.png")).Returns(Task.CompletedTask);
        using var failed = _db.CreateContext(new FailDatabaseCommand(sql => sql.Contains("INSERT INTO \"MediaFiles\"", StringComparison.Ordinal)));
        Assert.ThrowsAsync<DbUpdateException>(() => Service(failed).AddMediaAsync(_db.Alice, pack.Id, "name", new byte[] { 1 }));
        _storage.Verify(s => s.RemoveAsync("failed.png"), Times.Once);
        Assert.That(await _db.Context.ReactionItems.CountAsync(r => r.PackId == pack.Id), Is.Zero);
        Assert.That(await _db.Context.MediaFiles.CountAsync(), Is.Zero);
        Assert.That(failed.ChangeTracker.Entries(), Is.Empty);
    }
}

