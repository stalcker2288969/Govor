using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Govor.Domain.Models.Users.Crypto;
using Govor.Domain.Configurations;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;

namespace Govor.Domain;

public class GovorDbContext(DbContextOptions<GovorDbContext> options) : DbContext(options)
{
    public virtual DbSet<User> Users { get; set; }
    public virtual DbSet<UserSession> UserSessions { get; set; }
    public virtual DbSet<UserPushToken>  UserPushTokens { get; set; }
    public virtual DbSet<UserCryptoSession> UserCryptoSessions { get; set; }
    public virtual DbSet<E2eeRecoveryVault> E2eeRecoveryVaults { get; set; }
    public DbSet<E2eeIdentityChange> E2eeIdentityChanges { get; set; }
    public virtual DbSet<SignedPreKey> SignedPreKeys { get; set; }
    public virtual DbSet<OneTimePreKey> OneTimePreKeys { get; set; }
    public virtual DbSet<Friendship> Friendships { get; set; }
    public virtual DbSet<PrivateChat> PrivateChats { get; set; }
    public virtual DbSet<Admin> Admins { get; set; }
    
    public virtual DbSet<Invitation> Invitations { get; set; }
    
    public virtual DbSet<Message> Messages { get; set; }
    public virtual DbSet<ReactionPack> ReactionPacks { get; set; }
    public virtual DbSet<ReactionItem> ReactionItems { get; set; }
    public virtual DbSet<UserReactionPack> UserReactionPacks { get; set; }
    public virtual DbSet<ChannelAllowedReaction> ChannelAllowedReactions { get; set; }
    public virtual DbSet<ChatPushNotification> ChatPushNotifications { get; set; }
    public virtual DbSet<MessageView> MessageViews { get; set; }
    public virtual DbSet<MessageReaction> MessageReactions { get; set; }
    public virtual DbSet<MediaAttachments> MediaAttachments { get; set; }
    public virtual DbSet<MediaFile> MediaFiles { get; set; }
   
    public virtual DbSet<ChatGroup> ChatGroups { get; set; }
    public virtual DbSet<GroupInvitation> GroupInvitations { get; set; }
    public virtual DbSet<GroupMembership> GroupMemberships { get; set; }
    public virtual DbSet<GroupAdmins> GroupAdmins { get; set; }
    public virtual DbSet<ServerCommunitySettings> ServerCommunitySettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<E2eeIdentityChange>(b =>
        {
            b.HasKey(c => new { c.UserId, c.Epoch });
            b.Property(c => c.PreviousIdentityKey).HasMaxLength(200);
            b.Property(c => c.IdentityKey).HasMaxLength(200);
            b.Property(c => c.PreviousDeviceKeyJson).HasMaxLength(10000);
            b.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.ApplyConfiguration(new E2eeRecoveryVaultConfiguration());
        modelBuilder.ApplyConfiguration(new UserSessionConfiguration());
        modelBuilder.ApplyConfiguration(new FriendshipConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new InvitationConfiguration());
        modelBuilder.ApplyConfiguration(new AdminConfiguration());
        modelBuilder.ApplyConfiguration(new MessagesConfiguration());
        modelBuilder.ApplyConfiguration(new ChatPushNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new MessageReactionConfiguration());
        modelBuilder.ApplyConfiguration(new ReactionPackConfiguration());
        modelBuilder.ApplyConfiguration(new ReactionItemConfiguration());
        modelBuilder.ApplyConfiguration(new UserReactionPackConfiguration());
        modelBuilder.ApplyConfiguration(new ChannelAllowedReactionConfiguration());
        modelBuilder.ApplyConfiguration(new MediaAttachmentsConfiguration());
        modelBuilder.ApplyConfiguration(new MessageViewConfiguration());
        modelBuilder.ApplyConfiguration(new MediaFileConfiguration());
        modelBuilder.ApplyConfiguration(new ChatGroupConfigurator());
        modelBuilder.ApplyConfiguration(new GroupInvitationConfiguration());
        modelBuilder.ApplyConfiguration(new GroupMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new GroupAdminsConfiguration());
        modelBuilder.ApplyConfiguration(new ServerCommunitySettingsConfiguration());

        modelBuilder.ApplyConfiguration(new OneTimePreKeyConfiguration());
        modelBuilder.ApplyConfiguration(new UserCryptoSessionConfiguration());
        modelBuilder.ApplyConfiguration(new SignedPreKeyConfiguration());
        modelBuilder.ApplyConfiguration(new PrivateChatsConfiguration());
        modelBuilder.ApplyConfiguration(new UserPushTokenConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
