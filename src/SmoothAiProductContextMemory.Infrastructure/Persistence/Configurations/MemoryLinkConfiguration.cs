using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class MemoryLinkConfiguration : IEntityTypeConfiguration<MemoryLink>
{
    public void Configure(EntityTypeBuilder<MemoryLink> builder)
    {
        builder.ToTable("memory_link");

        builder.HasKey(l => new { l.SourceMemoryId, l.TargetMemoryId, l.Relation });

        builder.Property(l => l.SourceMemoryId).HasColumnName("source_memory_id");

        builder.Property(l => l.TargetMemoryId).HasColumnName("target_memory_id");

        builder.Property(l => l.Relation).HasColumnName("relation").IsRequired().HasMaxLength(32);

        builder.Property(l => l.Reason).HasColumnName("reason").IsRequired();

        builder.HasIndex(l => l.TargetMemoryId);

        builder.HasOne(l => l.SourceMemory)
            .WithMany(m => m.LinksFrom)
            .HasForeignKey(l => l.SourceMemoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.TargetMemory)
            .WithMany(m => m.LinksTo)
            .HasForeignKey(l => l.TargetMemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
