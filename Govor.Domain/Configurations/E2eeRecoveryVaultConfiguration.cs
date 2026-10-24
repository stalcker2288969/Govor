using Govor.Domain.Models.Users;
using Govor.Domain.Models.Users.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Govor.Domain.Configurations;

public class E2eeRecoveryVaultConfiguration : IEntityTypeConfiguration<E2eeRecoveryVault>
{
    public void Configure(EntityTypeBuilder<E2eeRecoveryVault> builder)
    {
        builder.HasKey(v => v.UserId);
        var b = builder;
        b.Property(v => v.Revision).IsConcurrencyToken();
        b.Property(v => v.IdentityKey).HasMaxLength(200);
        b.Property(v => v.Signature).HasMaxLength(100);
        b.Property(v => v.Ciphertext).HasMaxLength(4_000_000);
        b.Property(v => v.PasswordCiphertext).HasMaxLength(4096);
        b.Property(v => v.PasswordSignature).HasMaxLength(100);
        b.HasOne<User>().WithOne().HasForeignKey<E2eeRecoveryVault>(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
