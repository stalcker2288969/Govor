using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Govor.Application.E2ee;
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses;
using Microsoft.EntityFrameworkCore;

namespace Govor.Server.Tests;

public partial class ReadApiTests
{
    [Test]
    public async Task HealthAndSwaggerEndpointsExposeExpectedContracts()
    {
        Assert.That((await _client.GetAsync("/server/ping")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _client.GetStringAsync("/"), Is.EqualTo("Not for browsers"));
        var ready = await _client.GetAsync("/server/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(_db.Connection is Npgsql.NpgsqlConnection
            ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        var swagger = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(swagger.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var document = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
        Assert.That(document.RootElement.GetProperty("paths").TryGetProperty("/api/e2ee/device", out _), Is.True);
        Assert.That(document.RootElement.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString(), Is.EqualTo("bearer"));
    }

    [Test]
    public async Task AdminHttpRoutesRequireRoleAndReturnPersistedUserAndInvitationData()
    {
        Assert.That((await _client.GetAsync("/api/admin/Users/all")).StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(role:"Admin");
        var list=await _client.GetAsync("/api/admin/Users/all");Assert.That(list.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        using var users=JsonDocument.Parse(await list.Content.ReadAsStringAsync());Assert.That(users.RootElement.GetArrayLength(),Is.EqualTo(3));
        Assert.That((await _client.GetAsync($"/api/admin/Users/{_db.Alice}")).StatusCode,Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/admin/Users/{Guid.NewGuid()}")).StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        var before=(await _db.Context.Users.AsNoTracking().SingleAsync(x=>x.Id==_db.Alice)).PasswordHash;
        Assert.That((await _client.PostAsJsonAsync($"/api/admin/Users/user/{_db.Alice}/password",new{password="new-password-123"})).StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.Users.AsNoTracking().SingleAsync(x=>x.Id==_db.Alice)).PasswordHash,Is.Not.EqualTo(before));
        var created=await _client.PostAsJsonAsync("/api/admin/InviteUser/Invitation",new{endDate=DateTime.UtcNow.AddDays(2),maxParticipants=5,isAdmin=false,description="http invitation"});
        Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var invitation=await _db.Context.Invitations.AsNoTracking().SingleAsync(x=>x.Description=="http invitation");
        foreach(var path in new[]{"/api/admin/InviteUser","/api/admin/InviteUser/GetAllActiveInvitations",$"/api/admin/InviteUser/{invitation.Id}"})
            Assert.That((await _client.GetAsync(path)).StatusCode,Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.GetAsync($"/api/admin/InviteUser/{Guid.NewGuid()}")).StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        Authorize();
        Assert.That((await _client.PostAsJsonAsync("/api/admin/InviteUser/Invitation",new{endDate=DateTime.UtcNow.AddDays(2),maxParticipants=5})).StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ReactionPackHttpCrudAndSubscriptionsUseRealMappings()
    {
        Authorize(role:"Admin");ReactionTests.MakeAdmin(_db.Context,_db.Bob);
        var created=await _client.PostAsJsonAsync("/api/admin/reaction-packs",new{name="http pack",description="original"});
        Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.OK));var pack=(await created.Content.ReadFromJsonAsync<ReactionPackResponse>())!;
        Assert.That((await _client.PutAsJsonAsync($"/api/admin/reaction-packs/{pack.Id}",new{name="updated",description="changed",isEnabled=true})).StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var emoji=await _client.PostAsJsonAsync($"/api/admin/reaction-packs/{pack.Id}/reactions/emoji",new{name="wave",emoji="👋"});
        Assert.That(emoji.StatusCode,Is.EqualTo(HttpStatusCode.OK));var item=(await emoji.Content.ReadFromJsonAsync<ReactionItemResponse>())!;
        foreach(var path in new[]{"/api/reaction-packs",$"/api/reaction-packs/{pack.Id}",$"/api/reaction-packs/shared/{pack.ShareCode}",$"/api/reactions/{item.Id}","/api/reaction-packs/limits",$"/api/admin/reaction-packs/{pack.Id}"})
            Assert.That((await _client.GetAsync(path)).StatusCode,Is.EqualTo(HttpStatusCode.OK),path);
        Assert.That((await _client.PutAsync($"/api/reaction-packs/{pack.Id}/subscription",null)).StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync($"/api/reaction-packs/{pack.Id}/subscription")).StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync($"/api/admin/reaction-packs/{pack.Id}/reactions/{item.Id}")).StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync($"/api/admin/reaction-packs/{pack.Id}")).StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.GetAsync($"/api/reaction-packs/{pack.Id}")).StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync($"/api/admin/reaction-packs/{pack.Id}")).StatusCode,Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task E2eeHttpRegistrationIsIdempotentAndImmutableAndDirectoryChecksAccess()
    {
        Authorize();
        var privateKeys=E2eeProtocol.GenerateKeys();var key=E2eeProtocol.PublicKey(privateKeys,_db.Bob,_session);
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device",key)).StatusCode,Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device",key)).StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var replacement=E2eeProtocol.PublicKey(E2eeProtocol.GenerateKeys(),_db.Bob,_session);
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device",replacement)).StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device",key with{UserId=_db.Alice})).StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device",key with{IdentityKey="invalid"})).StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _client.GetAsync($"/api/e2ee/conversations/0/{_db.ChatId}/keys")).StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await _client.GetAsync($"/api/e2ee/conversations/0/{Guid.NewGuid()}/keys")).StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        foreach(var path in new[]{$"/api/e2ee/conversations/2/{_db.ChatId}/keys",$"/api/e2ee/conversations/0/{Guid.Empty}/keys"})
            Assert.That((await _client.GetAsync(path)).StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
        var aliceSession=new Govor.Domain.Models.Users.UserSession{UserId=_db.Alice,RefreshTokenHash="alice-e2ee",ExpiresAt=DateTime.UtcNow.AddDays(1)};
        _db.Context.UserSessions.Add(aliceSession);await _db.Context.SaveChangesAsync();
        var aliceKey=E2eeProtocol.PublicKey(E2eeProtocol.GenerateKeys(),_db.Alice,aliceSession.Id);
        await new E2eeService(_db.Context).RegisterAsync(_db.Alice,aliceSession.Id,aliceKey);
        var directoryResponse=await _client.GetAsync($"/api/e2ee/conversations/0/{_db.ChatId}/keys");
        Assert.That(directoryResponse.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var directory=(await directoryResponse.Content.ReadFromJsonAsync<ConversationKeys>())!;
        Assert.That(directory.Members,Is.EquivalentTo(new[]{_db.Alice,_db.Bob}));Assert.That(directory.Devices,Has.Length.EqualTo(2));
    }
}
