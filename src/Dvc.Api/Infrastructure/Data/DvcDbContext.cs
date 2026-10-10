using Dvc.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dvc.Api.Infrastructure.Data;

public class DvcDbContext(DbContextOptions<DvcDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
}