using Govor.API.Common.Extensions;
using Govor.Application.PushNotifications;
using Govor.Application.PushNotifications.Providers;
using Govor.Application.Storage;
using Govor.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Govor.API.Tests.Unit.Common;

[TestFixture]
public class ConfigurationTests
{
    [TestCase(false)] [TestCase(true)]
    public void DatabaseConfigurationUsesPostgresAndHonorsDetailedErrors(bool detailed)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:GovorDbContext","Host=localhost;Database=configuration_tests;Username=tests"},{"Database:EnableDetailedErrors",detailed.ToString()}}).Build();
        var services=new ServiceCollection();services.AddGovorDbContext(config);
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var db=scope.ServiceProvider.GetRequiredService<GovorDbContext>();
        Assert.That(db.Database.ProviderName,Is.EqualTo("Npgsql.EntityFrameworkCore.PostgreSQL"));
        Assert.That(db.Database.CreateExecutionStrategy().RetriesOnFailure,Is.True);
        var options=scope.ServiceProvider.GetRequiredService<DbContextOptions<GovorDbContext>>();
        Assert.That(options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()!.DetailedErrorsEnabled,Is.EqualTo(detailed));
    }
    [Test]
    public void DisabledFirebaseAndLocalStorageResolveThroughProductionRegistration()
    {
        var services=new ServiceCollection();services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Firebase:Enabled","false"}}).Build());
        var environment=new Mock<IWebHostEnvironment>();environment.SetupGet(x=>x.ContentRootPath).Returns(Path.GetTempPath());services.AddSingleton(environment.Object);
        services.AddServices();using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<IStorageService>(),Is.TypeOf<LocalStorageService>());
        Assert.That(scope.ServiceProvider.GetRequiredService<IPushNotificationProvider>(),Is.TypeOf<NullPushProvider>());
    }
}
