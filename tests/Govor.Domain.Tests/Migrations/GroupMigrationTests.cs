using Govor.Domain.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Govor.Domain.Tests.Migrations;

[TestFixture]
public class GroupMigrationTests
{
    [Test]
    public async Task LegacyCleanupPreservesBansChoosesActiveAdminAndRevokesAmbiguousLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "Users" ("Id" TEXT PRIMARY KEY);
            CREATE TABLE "ChatGroups" ("Id" TEXT PRIMARY KEY, "OwnerUserId" TEXT, "CreatedAt" TEXT);
            CREATE TABLE "GroupMemberships" ("Id" TEXT, "GroupId" TEXT, "UserId" TEXT, "IsBanned" INTEGER, "MemberSince" TEXT, "InvitationId" TEXT);
            CREATE TABLE "GroupAdmins" ("Id" TEXT, "GroupId" TEXT, "UserId" TEXT);
            CREATE TABLE "GroupInvitations" ("Id" TEXT, "InvitationCode" TEXT, "IsRevoked" INTEGER, "UsedCount" INTEGER);
            INSERT INTO "Users" VALUES ('alice'), ('bob'), ('carol');
            INSERT INTO "ChatGroups" VALUES ('group', NULL, NULL);
            INSERT INTO "GroupInvitations" VALUES ('inv-1', 'same', 0, 0), ('inv-2', 'same', 0, 0);
            INSERT INTO "GroupMemberships" VALUES
                ('a', 'group', 'alice', 0, '2026-01-02', NULL),
                ('b1', 'group', 'bob', 0, '2026-01-01', 'inv-1'),
                ('b2', 'group', 'bob', 1, '2026-01-03', 'inv-1'),
                ('c', 'group', 'carol', 0, '2026-01-01', 'inv-1'),
                ('orphan', 'group', 'missing', 0, '2026-01-01', NULL);
            INSERT INTO "GroupAdmins" VALUES
                ('a1', 'group', 'alice'), ('a2', 'group', 'alice'),
                ('b', 'group', 'bob'), ('orphan', 'group', 'missing');
            """;
        await command.ExecuteNonQueryAsync();
        var migration = new PrimitiveGroupsAndRequiredChannel();
        command.CommandText = migration.UpOperations.OfType<SqlOperation>().Single().Sql;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT COUNT(*) FROM \"GroupMemberships\"";
        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync()), Is.EqualTo(3));
        command.CommandText = "SELECT \"IsBanned\" FROM \"GroupMemberships\" WHERE \"UserId\" = 'bob'";
        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync()), Is.EqualTo(1));
        command.CommandText = "SELECT COUNT(*) FROM \"GroupAdmins\"";
        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync()), Is.EqualTo(1));
        command.CommandText = "SELECT \"OwnerUserId\" FROM \"ChatGroups\"";
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("alice"));
        command.CommandText = "SELECT COUNT(*) FROM \"GroupInvitations\" WHERE \"IsRevoked\" = 1";
        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync()), Is.EqualTo(2));
        command.CommandText = "SELECT \"UsedCount\" FROM \"GroupInvitations\" WHERE \"Id\" = 'inv-1'";
        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync()), Is.EqualTo(2));
    }
}
