using Govor.API.Common.Extensions;
using Govor.API.Common.Mapping;
using Govor.API.Filters;
using Govor.Application.Profiles;
using Govor.Application.Users.UserOnlineStatus;
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Govor.Tests.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace Govor.API.Tests.Integration.Filters;

[TestFixture]
public class HubExceptionFilterTests
{
    public sealed class TestHub : Hub
    {
        public Task<HubResult<int>> Typed() => Task.FromResult(HubResult<int>.Ok(7));
        public Task Plain() => Task.CompletedTask;
        public int Synchronous() => 7;
    }

    [TestCase(0, HubResultStatus.BadRequest)]
    [TestCase(1, HubResultStatus.Unauthorized)]
    [TestCase(2, HubResultStatus.NotFound)]
    [TestCase(3, HubResultStatus.Conflict)]
    [TestCase(4, HubResultStatus.ServerError)]
    public async Task ActiveSessionExceptionsAreMappedWithoutLeakingServerErrors(int kind, HubResultStatus expected)
    {
        using var db = new TestDatabase();
        var sid = Guid.NewGuid();
        db.Context.UserSessions.Add(new()
        {
            Id = sid, UserId = db.Alice, DeviceInfo = "test", RefreshTokenHash = "hash", CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        db.Context.SaveChanges();
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("userId", db.Alice.ToString()), new Claim("sid", sid.ToString()) }, "test")));
        using var hub = new TestHub();
        var invocation = new HubInvocationContext(caller.Object, Mock.Of<IServiceProvider>(), hub,
            typeof(TestHub).GetMethod(nameof(TestHub.Typed))!, []);
        Exception error = kind switch
        {
            0 => new ArgumentException("invalid"), 1 => new UnauthorizedAccessException("denied"),
            2 => new KeyNotFoundException("missing"), 3 => new InvalidOperationException("conflict"),
            _ => new Exception("private detail")
        };
        var result =
            (HubResult<int>)(await new HubExceptionFilter(NullLogger<HubExceptionFilter>.Instance, db.Context)
                .InvokeMethodAsync(invocation, _ => throw error))!;
        Assert.That(result.Status, Is.EqualTo(expected));
        if (kind == 4) Assert.That(result.ErrorMessage, Is.EqualTo("Internal server error"));
        caller.Verify(x => x.Abort(), Times.Never);
    }

    [TestCase(nameof(TestHub.Typed))]
    [TestCase(nameof(TestHub.Plain))]
    [TestCase(nameof(TestHub.Synchronous))]
    public async Task MissingSessionAbortsAndHandlesReturnShapes(string method)
    {
        using var db = new TestDatabase();
        using var hub = new TestHub();
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(x => x.User).Returns(new ClaimsPrincipal());
        var invocation = new HubInvocationContext(caller.Object, Mock.Of<IServiceProvider>(), hub,
            typeof(TestHub).GetMethod(method)!, []);
        var invoked = false;
        var result =
            await new HubExceptionFilter(NullLogger<HubExceptionFilter>.Instance, db.Context).InvokeMethodAsync(
                invocation, _ =>
                {
                    invoked = true;
                    return ValueTask.FromResult<object?>(7);
                });
        Assert.That(invoked, Is.False);
        caller.Verify(x => x.Abort(), Times.Once);
        if (method == nameof(TestHub.Typed))
            Assert.That(((HubResult<int>)result!).Status, Is.EqualTo(HubResultStatus.Unauthorized));
        else Assert.That(result, Is.Null);
    }
}
