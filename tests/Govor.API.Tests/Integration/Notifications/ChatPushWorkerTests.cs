using Govor.API.Hubs.Infrastructure;
using Govor.Application.Infrastructure.Common;
using Govor.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Integration.Notifications;

[TestFixture]
public class ChatPushWorkerTests
{
    private sealed class Worker(IServiceScopeFactory factory):ChatPushWorker(factory,NullLogger<ChatPushWorker>.Instance)
    {
        public Task Run(CancellationToken token)=>ExecuteAsync(token);
    }
    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public async Task WorkerDisposesScopesAndStopsOnCancellationIncludingFailurePaths(int mode)
    {
        using var db=new TestDatabase();using var cancellation=new CancellationTokenSource();
        var factory=new Mock<IServiceScopeFactory>();var scope=new Mock<IServiceScope>();var provider=new Mock<IServiceProvider>();
        var clock=new Mock<INowDateTimeProvider>();clock.SetupGet(x=>x.Now).Returns(DateTime.UtcNow);
        var dispatcher=new ChatPushDispatcher(db.Context,Mock.Of<IChatNotificationService>(),clock.Object);
        scope.SetupGet(x=>x.ServiceProvider).Returns(provider.Object);provider.Setup(x=>x.GetService(typeof(ChatPushDispatcher))).Returns(dispatcher);
        scope.Setup(x=>x.Dispose()).Callback(()=>cancellation.Cancel());
        if(mode==0) cancellation.Cancel();
        if(mode is 1 or 2) factory.Setup(x=>x.CreateScope()).Returns(()=>{cancellation.Cancel();throw mode==1?new OperationCanceledException(cancellation.Token):new InvalidOperationException("scope failure");});
        else factory.Setup(x=>x.CreateScope()).Returns(scope.Object);
        using var worker=new Worker(factory.Object);
        await worker.Run(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        factory.Verify(x=>x.CreateScope(),mode==0?Times.Never():Times.Once());
        scope.Verify(x=>x.Dispose(),mode==3?Times.Once():Times.Never());
    }
}

