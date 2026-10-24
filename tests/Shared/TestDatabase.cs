using Govor.Domain;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Npgsql;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Govor.Tests.Shared;

public sealed class TestDatabase : IDisposable
{
    public DbConnection Connection { get; }
    private readonly string? _schema;
    private bool _disposed;
    public GovorDbContext Context { get; } = null!;
    public Guid Alice { get; } = Guid.NewGuid();
    public Guid Bob { get; } = Guid.NewGuid();
    public Guid Outsider { get; } = Guid.NewGuid();
    public Guid ChatId { get; } = Guid.NewGuid();

    public TestDatabase()
    {
        var postgres = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(postgres))
            Connection = new SqliteConnection("Data Source=:memory:");
        else
        {
            var settings = new NpgsqlConnectionStringBuilder(postgres);
            if (settings.Database?.EndsWith("_tests", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("PostgreSQL tests require a dedicated database ending in _tests.");
            _schema = "test_" + Guid.NewGuid().ToString("N");
            settings.SearchPath = _schema;
            // Each fixture has a unique search path. Pooling would retain one pool
            // per schema and exhaust PostgreSQL's connection limit across the suite.
            settings.Pooling = false;
            Connection = new NpgsqlConnection(settings.ConnectionString);
        }
        Connection.Open();
        if (_schema is not null)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA {_schema}";
            command.ExecuteNonQuery();
        }
        Context = CreateContext();
        if (_schema is null)
            Context.Database.EnsureCreated();
        else
            Context.Database.Migrate();
        var invite = new Invitation
        {
            Id = Guid.NewGuid(), Code = "test", Description = "test",
            EndDate = DateTime.UtcNow.AddDays(1), MaxParticipants = 100
        };
        Context.Invitations.Add(invite);
        foreach (var id in new[] { Alice, Bob, Outsider })
            Context.Users.Add(new User
            {
                Id = id, Username = id.ToString(), PasswordHash = "test", Description = "",
                InviteId = invite.Id, WasOnline = DateTime.UtcNow
            });
        Context.PrivateChats.Add(new PrivateChat { Id = ChatId, UserAId = Alice, UserBId = Bob });
        Context.SaveChanges();
    }

    public void ConfigureOptions(DbContextOptionsBuilder options)
    {
        if (_schema is null)
            options.UseSqlite(Connection);
        else
            options.UseNpgsql(Connection);
    }

    public GovorDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<GovorDbContext>();
        ConfigureOptions(options);
        options.AddInterceptors(interceptors);
        return new GovorDbContext(options.Options);
    }

    public Message AddMessage(Guid? sender = null, Guid? chat = null,
        RecipientType type = RecipientType.User, DateTime? sentAt = null, Guid? id = null)
    {
        var message = new Message
        {
            Id = id ?? Guid.NewGuid(), SenderId = sender ?? Alice,
            RecipientId = chat ?? ChatId, RecipientType = type,
            SentAt = sentAt ?? DateTime.UtcNow, EncryptedContent = "hello"
        };
        Context.Messages.Add(message);
        Context.SaveChanges();
        return message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Context?.Dispose();
            if (_schema is not null && Connection.State == System.Data.ConnectionState.Open)
            {
                using var command = Connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
                command.ExecuteNonQuery();
            }
        }
        finally { Connection.Dispose(); }
    }
}
