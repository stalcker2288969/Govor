using Govor.Domain.Models.Users.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Govor.Domain.Configurations;

public class UserCryptoSessionConfiguration : IEntityTypeConfiguration<UserCryptoSession>
{
    public void Configure(EntityTypeBuilder<UserCryptoSession> builder)
    {
        builder.HasKey(ucs => ucs.Id);
        builder.Property(c => c.Generation).IsConcurrencyToken();
        builder.Property(c => c.AccountIdentityKey).HasMaxLength(200);
        builder.Property(c => c.AccountSignature).HasMaxLength(100);
        builder.Property(c => c.PreviousFingerprint).HasMaxLength(64);
        
        builder.HasOne(ucs => ucs.UserSession)
            .WithOne(us => us.CryptoSession)
            .HasForeignKey<UserCryptoSession>(ucs => ucs.UserSessionId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ucs => ucs.SignedPreKey)
            .WithOne(spk => spk.UserCryptoSession)
            .HasForeignKey<SignedPreKey>(spk => spk.UserCryptoSessionId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasMany(ucs => ucs.OneTimePreKeys)
            .WithOne(otpk => otpk.UserCryptoSession)
            .HasForeignKey(otpk => otpk.UserCryptoSessionId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasIndex(ucs => ucs.UserSessionId)
            .IsUnique();
        
        builder.Property(ucs => ucs.PublicIdentityKey)
            .IsRequired();
    }
}
