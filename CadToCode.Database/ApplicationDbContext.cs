using CadToCode.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Database;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DrawingComponent> DrawingComponents => Set<DrawingComponent>();
    public DbSet<CadLibraryItem> CadLibraryItems => Set<CadLibraryItem>();
    public DbSet<DesignSession> DesignSessions => Set<DesignSession>();
    public DbSet<ZooGeneration> ZooGenerations => Set<ZooGeneration>();
    public DbSet<ZooGenerationFile> ZooGenerationFiles => Set<ZooGenerationFile>();

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

        modelBuilder.Entity<ZooGeneration>(entity =>
        {
            entity.HasIndex(g => g.Status);
            entity.HasIndex(g => g.CreatedAt);
        });

        modelBuilder.Entity<ZooGenerationFile>(entity =>
        {
            entity.HasIndex(f => new { f.GenerationId, f.FileName }).IsUnique();

            entity.HasOne(f => f.Generation)
                .WithMany(g => g.Files)
                .HasForeignKey(f => f.GenerationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}