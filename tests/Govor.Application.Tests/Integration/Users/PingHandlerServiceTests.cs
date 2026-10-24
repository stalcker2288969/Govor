using Govor.Application.PingHandler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Govor.Application.Tests.Users;

[TestFixture]
public class PingHandlerServiceTests
{
    [Test]
    public async Task PingUpdatesLastSeenAndThrottlesRepeatedRequests()
    {
        using var db = new TestDatabase();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new PingHandlerService(db.Context, cache);
        var old = DateTime.UtcNow.AddHours(-1);
        await db.Context.Users.Where(u => u.Id == db.Alice).ExecuteUpdateAsync(s => s.SetProperty(u => u.WasOnline, old));
        var before = DateTime.UtcNow;
        await service.Ping(db.Alice);
        var updated = await db.Context.Users.AsNoTracking().SingleAsync(u => u.Id == db.Alice);
        Assert.That(updated.WasOnline, Is.GreaterThanOrEqualTo(before));
        Assert.That(cache.TryGetValue($"LastPing_{db.Alice}", out DateTime _), Is.True);
        await db.Context.Users.Where(u => u.Id == db.Alice).ExecuteUpdateAsync(s => s.SetProperty(u => u.WasOnline, old));
        var storedBeforeThrottledPing = (await db.Context.Users.AsNoTracking().SingleAsync(u => u.Id == db.Alice)).WasOnline;
        await service.Ping(db.Alice);
        Assert.That((await db.Context.Users.AsNoTracking().SingleAsync(u => u.Id == db.Alice)).WasOnline, Is.EqualTo(storedBeforeThrottledPing));
    }
}
