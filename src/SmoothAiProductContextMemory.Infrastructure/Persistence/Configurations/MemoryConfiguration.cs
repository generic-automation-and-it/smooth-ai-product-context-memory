using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class MemoryConfiguration : IEntityTypeConfiguration<Memory>
{
    public void Configure(EntityTypeBuilder<Memory> builder)
    {
        builder.ToTable("memory");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(m => m.Uuid).HasColumnName("uuid").IsRequired();

        builder.Property(m => m.LineageId).HasColumnName("lineage_id").IsRequired();

        builder.Property(m => m.GroupId).HasColumnName("group_id");

        builder.Property(m => m.Name).HasColumnName("name").IsRequired().HasMaxLength(200);

        builder.Property(m => m.Description).HasColumnName("description").IsRequired();

        builder.Property(m => m.SubjectSlug).HasColumnName("subject_slug").IsRequired().HasMaxLength(200);

        builder.Property(m => m.Tags).HasColumnName("tags").HasColumnType("text[]");

        builder.Property(m => m.Facets).HasColumnName("facets").HasColumnType("text[]");

        builder.HasIndex(m => m.Uuid).IsUnique();

        builder.HasIndex(m => new { m.GroupId, m.SubjectSlug }).IsUnique();

        builder.HasIndex(m => m.Tags).HasMethod("gin");

        builder.HasIndex(m => m.Facets).HasMethod("gin");

        builder.HasIndex(m => m.LineageId);

        builder.HasMany(m => m.Versions)
            .WithOne(v => v.Memory)
            .HasForeignKey(v => v.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
