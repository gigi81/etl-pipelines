using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database;

/// <summary>
/// The one seam <c>dotnet ef dbcontext scaffold</c> leaves for hand customization without ever
/// touching the generated <c>ServerDbContext.cs</c>: re-running the scaffold command overwrites
/// that file wholesale (see this project's README.md for the exact command), but never this one,
/// since scaffolding never produces it.
/// </summary>
/// <remarks>
/// One customization lives here so far, needed only because this same model also runs against a
/// second provider scaffolding never saw: see <see cref="OnModelCreatingPartial"/>. Add anything
/// else scaffolding fails to infer correctly here too - never by hand-editing
/// <c>ServerDbContext.cs</c>.
/// </remarks>
public partial class ServerDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Scaffolding correctly captured Agents.Tags' Postgres default ('{}'::text[]) from the
        // live schema, but that array-literal syntax isn't valid SQLite - and
        // EtlPipelines.Server.Database.Tests' fast suite runs this exact model against SQLite
        // (EnsureCreated(), never dbdeploy - see SqliteServerDbContext's own remarks). Every row
        // those tests insert sets Tags itself, so dropping the DB-side default for that one
        // provider costs nothing there while leaving the real Postgres default untouched.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.Entity<Agent>().Property(agent => agent.Tags).HasDefaultValueSql(null);
        }
    }
}
