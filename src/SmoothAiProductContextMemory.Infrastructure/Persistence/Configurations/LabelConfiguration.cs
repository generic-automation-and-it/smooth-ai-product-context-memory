using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    /// <summary>The initial controlled facet vocabulary, derived from what actually clustered in trials.</summary>
    public static readonly string[] SeedFacets =
    [
        "positioning", "architecture", "storage", "domain-model", "write-path",
        "retrieval", "security", "governance", "prior-art", "process",
    ];

    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.ToTable("label");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(l => l.Name).HasColumnName("name").IsRequired().HasMaxLength(100);

        builder.Property(l => l.Status).HasColumnName("status").IsRequired().HasMaxLength(32);

        builder.HasIndex(l => l.Name).IsUnique();

        for (int i = 0; i < SeedFacets.Length; i++)
        {
            long id = i + 2;
            builder.HasData(new Label
            {
                Id = id,
                Name = SeedFacets[i],
                Status = Label.LabelStatus.Active,
            });
        }
    }
}
