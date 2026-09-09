using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class MemoryGroupConfiguration : IEntityTypeConfiguration<MemoryGroup>
{
    public void Configure(EntityTypeBuilder<MemoryGroup> builder)
    {
        builder.ToTable("memory_group");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(g => g.Uuid).HasColumnName("uuid").IsRequired();

        builder.Property(g => g.ScopeDimension).HasColumnName("scope_dimension").IsRequired().HasMaxLength(32);

        builder.Property(g => g.ScopeIdentifier).HasColumnName("scope_identifier").HasMaxLength(200);

        builder.Property(g => g.Repo).HasColumnName("repo").HasMaxLength(200);

        builder.Property(g => g.RepoUrl).HasColumnName("repo_url").HasMaxLength(500);

        builder.Property(g => g.Tickets)
            .HasColumnName("tickets")
            .HasColumnType("jsonb")
            .HasConversion(JsonbConverter.ForTickets());

        builder.Property(g => g.CreatedOn).HasColumnName("created_on").HasColumnType("timestamptz").HasDefaultValueSql("now()");

        builder.HasIndex(g => g.Uuid).IsUnique();

        builder.HasIndex(g => g.Tickets).HasMethod("gin");

        builder.Property(g => g.InitiativeId).HasColumnName("initiative_id");

        builder.HasOne(g => g.Initiative)
            .WithMany()
            .HasForeignKey(g => g.InitiativeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(g => g.Descriptions)
            .WithOne(d => d.Group)
            .HasForeignKey(d => d.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.Memories)
            .WithOne(m => m.Group)
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
