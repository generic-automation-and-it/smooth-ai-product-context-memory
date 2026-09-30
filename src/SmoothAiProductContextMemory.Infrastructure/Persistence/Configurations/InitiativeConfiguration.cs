using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class InitiativeConfiguration : IEntityTypeConfiguration<Initiative>
{
    /// <summary>
    /// The seeded default initiative's name. Named because the restore target's emptiness check has to
    /// tell "a row the migrations created" from "a row an operator created", and an inlined literal
    /// here and again in that check would be two places to keep in step.
    /// </summary>
    public const string DefaultInitiativeName = "to-be-decided";

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
            Name = DefaultInitiativeName,
            Description = "Default initiative for groups not yet assigned.",
            Status = Initiative.InitiativeStatus.Active,
        });
    }
}
