using CadToCode.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Database;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DrawingComponent> DrawingComponents => Set<DrawingComponent>();
    public DbSet<CadLibraryItem> CadLibraryItems => Set<CadLibraryItem>();
    public DbSet<DesignSession> DesignSessions => Set<DesignSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(p => p.Status);
            entity.HasIndex(p => p.CreatedAt);
            entity.HasIndex(p => p.LastOpenedAt);
        });

        modelBuilder.Entity<DrawingComponent>(entity =>
        {
            entity.HasIndex(c => new { c.ProjectId, c.SequenceIndex });

            entity.HasOne(c => c.Project)
                  .WithMany(p => p.DrawingComponents)
                  .HasForeignKey(c => c.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CadLibraryItem>(entity =>
        {
            entity.HasIndex(i => i.Category);
            entity.HasIndex(i => i.FileType);
            entity.HasIndex(i => i.SyncStatus);
            entity.HasIndex(i => i.CreatedAt);
            entity.HasIndex(i => i.VectorPointId).IsUnique();
        });

        modelBuilder.Entity<DesignSession>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.State);
            entity.HasIndex(s => s.UpdatedAt);
            entity.HasIndex(s => new { s.UserId, s.UpdatedAt });
        });
    }
}