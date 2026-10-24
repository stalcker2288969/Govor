using Govor.API.Filters;
using Govor.Contracts.Responses.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class HubSessionTests
{
    [Test]
    public async Task InvalidSessionReturnsAHubResultAndAbortsWithoutRunningMethod()
    {
        using var db = new TestDatabase();
        var caller = new Mock<HubCallerContext>();
        var hub = new StubHub();
        var invocation = new HubInvocationContext(caller.Object, Mock.Of<IServiceProvider>(), hub,
            typeof(StubHub).GetMethod(nameof(StubHub.Echo))!, Array.Empty<object?>());
        var filter = new HubExceptionFilter(NullLogger<HubExceptionFilter>.Instance, db.Context);
        var invoked = false;
        var response = await filter.InvokeMethodAsync(invocation, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(HubResult<string>.Ok("unexpected"));
        });
        Assert.That(invoked, Is.False);
        Assert.That(response, Is.TypeOf<HubResult<string>>());
        Assert.That(((HubResult<string>)response!).Status, Is.EqualTo(HubResultStatus.Unauthorized));
        caller.Verify(c => c.Abort(), Times.Once);
    }

    private sealed class StubHub : Hub
    {
        public Task<HubResult<string>> Echo() => Task.FromResult(HubResult<string>.Ok("ok"));
    }
}
