using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Govor.Domain.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(u => u.SrpSalt).HasMaxLength(64);
        builder.Property(u => u.SrpIdentity).HasMaxLength(64);
        builder.Property(u => u.SrpVerifier).HasMaxLength(1024);
        builder.Property(u => u.CredentialVersion).IsConcurrencyToken();
        
        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.CreatedOn);
        builder.HasIndex(u => u.WasOnline);
        
        builder.HasOne(u => u.Invite)
            .WithMany(i => i.Users)
            .HasForeignKey(u => u.InviteId);
        
        builder.Property(u => u.Username)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(u => u.Username).IsUnique();

        builder.Property(u => u.Description)
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(u => u.PasswordHash)
            .IsRequired()
            .HasMaxLength(128);
    }
}
