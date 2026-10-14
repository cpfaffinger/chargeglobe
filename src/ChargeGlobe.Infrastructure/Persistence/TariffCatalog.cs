using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>EMPs and tariffs in <c>emp</c> and <c>tariff</c>; changes are recorded in the audit log.</summary>
public sealed class TariffCatalog(AppDbContext db, AuditLog audit) : ITariffCatalog
{
    public async Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken) =>
        await db.Emps.AsNoTracking()
            .OrderBy(emp => emp.Name)
            .ThenBy(emp => emp.Key)
            .Select(emp => new EmpView(emp.Key, emp.Name, emp.Website, db.Tariffs.Count(tariff => tariff.EmpId == emp.Id)))
            .ToListAsync(cancellationToken);

    public async Task<(EmpView Emp, bool Created)> PutEmpAsync(string key, EmpInput input, Actor actor, CancellationToken cancellationToken)
    {
        var emp = await db.Emps.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        var created = emp is null;
        if (emp is null)
        {
            emp = Emp.Create(key, input.Name, input.Website);
            db.Emps.Add(emp);
        }
        else
        {
            emp.Update(input.Name, input.Website);
        }

        if (db.ChangeTracker.HasChanges())
        {
            audit.Record(created ? "emp.create" : "emp.update", $"emp:{key}", actor);
            await db.SaveChangesAsync(cancellationToken);
        }

        var tariffs = await db.Tariffs.CountAsync(tariff => tariff.EmpId == emp.Id, cancellationToken);
        return (new EmpView(emp.Key, emp.Name, emp.Website, tariffs), created);
    }

    public async Task<EmpDeleteOutcome> DeleteEmpAsync(string key, Actor actor, CancellationToken cancellationToken)
    {
        var emp = await db.Emps.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (emp is null)
        {
            return EmpDeleteOutcome.NotFound;
        }

        if (await db.Tariffs.AnyAsync(tariff => tariff.EmpId == emp.Id, cancellationToken))
        {
            return EmpDeleteOutcome.HasTariffs;
        }

        db.Emps.Remove(emp);
        audit.Record("emp.delete", $"emp:{key}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return EmpDeleteOutcome.Deleted;
    }

    public async Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, CancellationToken cancellationToken)
    {
        var rows = await db.Tariffs.AsNoTracking()
            .Join(db.Emps, tariff => tariff.EmpId, item => item.Id, (tariff, item) => new { Tariff = tariff, item.Key })
            .Where(row => emp == null || row.Key == emp)
            .OrderBy(row => row.Key)
            .ThenBy(row => row.Tariff.Name)
            .ThenBy(row => row.Tariff.ValidFrom)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => View(row.Tariff, row.Key))];
    }

    public async Task<TariffResult> PutTariffAsync(TariffView tariff, Actor actor, CancellationToken cancellationToken)
    {
        var emp = await db.Emps.AsNoTracking().FirstOrDefaultAsync(item => item.Key == tariff.Emp, cancellationToken);
        if (emp is null)
        {
            return new TariffResult(TariffOutcome.UnknownEmp, null);
        }

        List<Guid> operators = [.. tariff.OperatorIds.Distinct()];
        if (await db.Operators.CountAsync(item => operators.Contains(item.Id), cancellationToken) != operators.Count)
        {
            return new TariffResult(TariffOutcome.UnknownOperator, null);
        }

        var values = new Tariff.Values(
            tariff.Name, tariff.Currency, Microseconds(tariff.ValidFrom), tariff.ValidTo is { } to ? Microseconds(to) : null,
            tariff.PricePerKwhAc, tariff.PricePerKwhDc, tariff.PricePerMinute, tariff.SessionFee, tariff.BlockingFeePerMinute,
            tariff.BlockingAfterMinutes, tariff.MonthlyFee, tariff.RoamingOnly, operators);
        var stored = await db.Tariffs.FirstOrDefaultAsync(item => item.Id == tariff.Id, cancellationToken);
        var created = stored is null;
        if (stored is null)
        {
            stored = Tariff.Create(tariff.Id, emp.Id, values);
            db.Tariffs.Add(stored);
        }
        else
        {
            stored.Update(emp.Id, values);
        }

        if (db.ChangeTracker.HasChanges())
        {
            audit.Record(created ? "tariff.create" : "tariff.update", $"tariff:{tariff.Id}", actor);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new TariffResult(created ? TariffOutcome.Created : TariffOutcome.Updated, View(stored, emp.Key));
    }

    public async Task<bool> DeleteTariffAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var tariff = await db.Tariffs.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (tariff is null)
        {
            return false;
        }

        db.Tariffs.Remove(tariff);
        audit.Record("tariff.delete", $"tariff:{id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static TariffView View(Tariff tariff, string emp) =>
        new(tariff.Id, emp, tariff.Name, tariff.Currency, tariff.ValidFrom, tariff.ValidTo, tariff.PricePerKwhAc, tariff.PricePerKwhDc,
            tariff.PricePerMinute, tariff.SessionFee, tariff.BlockingFeePerMinute, tariff.BlockingAfterMinutes, tariff.MonthlyFee,
            tariff.RoamingOnly, tariff.OperatorIds);

    /// <summary>Postgres keeps microseconds: an equal second PUT finds no change, and the answer equals a later read.</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));
}
