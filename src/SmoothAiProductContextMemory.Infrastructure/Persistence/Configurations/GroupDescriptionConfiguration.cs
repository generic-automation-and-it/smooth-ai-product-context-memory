using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

public sealed class GroupDescriptionConfiguration : IEntityTypeConfiguration<GroupDescription>
{
    public void Configure(EntityTypeBuilder<GroupDescription> builder)
    {
        builder.ToTable("group_description");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(d => d.GroupId).HasColumnName("group_id");

        builder.Property(d => d.Version).HasColumnName("version").IsRequired();

        builder.Property(d => d.Name).HasColumnName("name").IsRequired().HasMaxLength(200);

        builder.Property(d => d.Body).HasColumnName("body").IsRequired();

        builder.Property(d => d.CreatedOn).HasColumnName("created_on").HasColumnType("timestamptz").HasDefaultValueSql("now()");

        builder.HasIndex(d => new { d.GroupId, d.Version }).IsUnique();

        builder.HasOne(d => d.Group)
            .WithMany(g => g.Descriptions)
            .HasForeignKey(d => d.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
