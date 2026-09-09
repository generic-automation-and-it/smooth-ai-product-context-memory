using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class InitiativeConfiguration : IEntityTypeConfiguration<Initiative>
{
    public void Configure(EntityTypeBuilder<Initiative> builder)
    {
        builder.ToTable("initiative");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(i => i.Name).HasColumnName("name").IsRequired().HasMaxLength(200);

        builder.Property(i => i.Description).HasColumnName("description").IsRequired();

        builder.Property(i => i.Status).HasColumnName("status").IsRequired().HasMaxLength(32);

        builder.HasIndex(i => i.Name).IsUnique();

        builder.HasData(new Initiative
        {
            Id = 1,
            Name = "to-be-decided",
            Description = "Default initiative for groups not yet assigned.",
            Status = Initiative.InitiativeStatus.Active,
        });
    }
}
