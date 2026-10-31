using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;

namespace Govor.Server.Tests;

public partial class ReadApiTests
{
    [Test]
    public async Task BenefitsGrantTagEditAndRevocationBroadcastToEveryConnectedProfileClient()
    {
        Authorize(userId: _db.Alice);
        using var alice = await ConnectHubAsync("/hubs/profiles");
        Authorize();
        using var bob = await ConnectHubAsync("/hubs/profiles");
        Authorize(userId: _db.Outsider);
        using var outsider = await ConnectHubAsync("/hubs/profiles");
        Authorize(role: "Admin");
        var route = $"/api/admin/users/{_db.Bob}/privileges";
        Assert.That((await _client.PutAsJsonAsync(route, new { privileges = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        long previous = 0;
        foreach (var stage in new[] { "grant", "edit", "reset", "revoke" })
        {
            if (stage == "edit")
            {
                Authorize();
                Assert.That((await _client.PutAsJsonAsync("/api/profile/tag", new ProfileTagStyle { Text = "Live" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
            if (stage == "reset")
                Assert.That((await _client.DeleteAsync("/api/profile/tag")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            if (stage == "revoke")
            {
                Authorize(role: "Admin");
                Assert.That((await _client.PutAsJsonAsync(route, new { privileges = 0 })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
            long revision = 0;
            foreach (var connection in new[] { alice, bob, outsider })
            {
                var frame = await connection.ReadAsync();
                Assert.That(frame.GetProperty("target").GetString(), Is.EqualTo("UserBenefitsUpdated"));
                var payload = frame.GetProperty("arguments")[0];
                Assert.That(payload.GetProperty("userId").GetGuid(), Is.EqualTo(_db.Bob));
                var benefits = payload.GetProperty("benefits");
                revision = benefits.GetProperty("revision").GetInt64();
                Assert.That(revision, Is.GreaterThan(previous));
                Assert.That(benefits.GetProperty("isGovorPlus").GetBoolean(), Is.EqualTo(stage != "revoke"));
                if (stage == "revoke") Assert.That(benefits.GetProperty("tag").ValueKind, Is.EqualTo(JsonValueKind.Null));
                else Assert.That(benefits.GetProperty("tag").GetProperty("text").GetString(), Is.EqualTo(stage == "edit" ? "Live" : "Govor+"));
            }
            previous = revision;
        }
        Authorize();
        Assert.That((await _client.PutAsJsonAsync("/api/profile/tag", new ProfileTagStyle())).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var profile = JsonDocument.Parse(await _client.GetStringAsync("/api/profile/download/me"));
        Assert.That(profile.RootElement.GetProperty("benefits").GetProperty("revision").GetInt64(), Is.EqualTo(previous));
    }

    [Test]
    public async Task BenefitsRoutesEnforceAuthenticationAdminRoleAndValidation()
    {
        var route = $"/api/admin/users/{_db.Bob}/privileges";
        Assert.That((await _client.GetAsync($"/api/profile/{_db.Bob}/benefits")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize();
        Assert.That((await _client.PutAsJsonAsync(route, new { privileges = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PutAsJsonAsync("/api/profile/tag", new ProfileTagStyle())).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize(role: "Admin");
        Assert.That((await _client.PutAsJsonAsync(route, new { privileges = 4 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _client.PutAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/privileges", new { privileges = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PutAsJsonAsync(route, new { privileges = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Authorize();
        Assert.That((await _client.PutAsJsonAsync("/api/profile/tag", new ProfileTagStyle { TextColor = "invalid" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _client.GetAsync($"/api/profile/{Guid.NewGuid()}/benefits")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var value = await _client.GetFromJsonAsync<UserBenefitsView>($"/api/profile/{_db.Bob}/benefits");
        Assert.That(value!.Tag!.Text, Is.EqualTo("Govor+"));
    }

    [Test]
    public async Task FriendRemovalAndBlockHttpRoutesPreserveBlockOwnership()
    {
        _db.Context.Friendships.Add(new Friendship { Id = Guid.NewGuid(), RequesterId = _db.Alice,
            AddresseeId = _db.Bob, Status = FriendshipStatus.Accepted });
        await _db.Context.SaveChangesAsync();
        var route = $"/api/friends/{_db.Alice}";
        Assert.That((await _client.DeleteAsync(route)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize();
        Assert.That((await _client.DeleteAsync(route)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.Friendships.AsNoTracking().SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Rejected));
        Assert.That((await _client.PutAsync(route + "/block", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync(route)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.Friendships.AsNoTracking().SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Blocked));
        Authorize(userId: _db.Alice);
        Assert.That((await _client.DeleteAsync($"/api/friends/{_db.Bob}/block")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize();
        Assert.That((await _client.DeleteAsync(route + "/block")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.Friendships.AsNoTracking().SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Rejected));
        Assert.That((await _client.PutAsync($"/api/friends/{_db.Bob}/block", null)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _client.PutAsync($"/api/friends/{Guid.NewGuid()}/block", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ReplyLookupDoesNotExposeMessagesToNonParticipants()
    {
        var message = _db.AddMessage();
        Authorize();
        var response = await _client.GetAsync($"/api/messages/{message.Id}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(message.Id));
        Authorize(userId: _db.Outsider);
        Assert.That((await _client.GetAsync($"/api/messages/{message.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
