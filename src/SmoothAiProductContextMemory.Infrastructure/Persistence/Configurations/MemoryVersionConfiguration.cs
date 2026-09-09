using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class MemoryVersionConfiguration : IEntityTypeConfiguration<MemoryVersion>
{
    public void Configure(EntityTypeBuilder<MemoryVersion> builder)
    {
        builder.ToTable("memory_version");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(v => v.MemoryId).HasColumnName("memory_id");

        builder.Property(v => v.Version).HasColumnName("version").IsRequired();

        builder.Property(v => v.IsCurrent).HasColumnName("is_current").IsRequired();

        builder.Property(v => v.Statement).HasColumnName("statement").IsRequired();

        builder.Property(v => v.ContentSummary).HasColumnName("content_summary").IsRequired();

        builder.Property(v => v.BlobAddress).HasColumnName("blob_address").HasMaxLength(500);

        builder.Property(v => v.Kind).HasColumnName("kind").IsRequired().HasMaxLength(64);

        builder.Property(v => v.Confidence).HasColumnName("confidence").HasColumnType("smallint");

        builder.Property(v => v.Status).HasColumnName("status").IsRequired().HasMaxLength(32);

        builder.Property(v => v.Sources)
            .HasColumnName("sources")
            .HasColumnType("jsonb")
            .HasConversion(JsonbConverter.ForSources());

        builder.Property(v => v.ValidFrom).HasColumnName("valid_from").HasColumnType("timestamptz");

        builder.Property(v => v.ValidUntil).HasColumnName("valid_until").HasColumnType("timestamptz");

        builder.Property(v => v.CreatedOn).HasColumnName("created_on").HasColumnType("timestamptz").HasDefaultValueSql("now()");

        builder.HasIndex(v => new { v.MemoryId, v.Version }).IsUnique();

        builder.HasIndex(v => v.MemoryId).IsUnique().HasFilter("\"is_current\"");

        builder.HasIndex(v => v.Kind);

        builder.HasIndex(v => v.Status);

        builder.HasOne(v => v.Memory)
            .WithMany(m => m.Versions)
            .HasForeignKey(v => v.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
