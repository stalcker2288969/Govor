using System.ComponentModel.DataAnnotations;

namespace Govor.Domain.Models.Users;

public class User
{
    public Guid Id {get; set;}
    public string Username {get; set;} 
    public string Description {get; set;} 
    public string PasswordHash {get; set;}
    public string? SrpSalt { get; set; }
    public string? SrpIdentity { get; set; }
    public string? SrpVerifier { get; set; }
    public long CredentialVersion { get; set; }
    public Guid IconId {get; set;} 
    public DateOnly CreatedOn {get; set;}
    public DateTime WasOnline {get; set;}
    public Guid InviteId {get; set;}
    public Invitation? Invite { get; set; }
    public List<Friendship> SentFriendRequests { get; set; } = new();
    public List<Friendship> ReceivedFriendRequests { get; set; } = new();

    public override bool Equals(object? obj)
    {
        var user = obj as User;

        return Id == user.Id;
    }
}
