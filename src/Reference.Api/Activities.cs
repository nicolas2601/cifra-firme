using Microsoft.EntityFrameworkCore;
using static Reference.Api.Access;
using static Reference.Api.Administration;

namespace Reference.Api;

internal static class Activities
{
    public static void MapActivities(this WebApplication app)
    {
        var group = app.MapGroup("/api/activities").RequireAuthorization();
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("", (ActivityInput input, AppDb db, HttpContext http, CancellationToken ct) => Save(null, input, db, http, ct));
        group.MapPut("/{id:guid}", (Guid id, ActivityInput input, AppDb db, HttpContext http, CancellationToken ct) => Save(id, input, db, http, ct));
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> List(AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Any(Permission.ActivityManage | Permission.ActivityReadOwn)) return Results.Forbid();
        var rows = Visible(db, actor);
        return Results.Ok(await rows.OrderBy(x => x.Status).ThenBy(x => x.Title).ThenBy(x => x.Id).ToListAsync(ct));
    }

    private static async Task<IResult> Get(Guid id, AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Any(Permission.ActivityManage | Permission.ActivityReadOwn)) return Results.Forbid();
        var item = await Visible(db, actor).SingleOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static IQueryable<Activity> Visible(AppDb db, Person actor) => actor.Has(Permission.ActivityManage)
        ? db.Activities.AsNoTracking()
        : db.Activities.AsNoTracking().Where(x => x.AssigneeId == actor.Id);

    private static async Task<IResult> Save(Guid? id, ActivityInput input, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.ActivityManage)) return Results.Forbid();
        var title = CleanText(input.Title, 160);
        if (title is null || input.AssigneeId is null || input.Status is null || !Enum.IsDefined(input.Status.Value))
            return Bad("Título, responsable o estado inválido.");
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.ActivityManage)) return Results.Forbid();
        if (!await db.People.AnyAsync(x => x.Id == input.AssigneeId && x.Active, ct)) return Bad("El responsable no está activo.");
        var activity = id is null ? new Activity() : await db.Activities.FindAsync([id], ct);
        if (activity is null) return Results.NotFound();
        activity.Title = title;
        activity.AssigneeId = input.AssigneeId.Value;
        activity.Status = input.Status.Value;
        if (id is null) db.Activities.Add(activity);
        await db.CommitAsync(tx, actor.Id, id is null ? "activity.created" : "activity.changed", activity.Id.ToString(), false, ct);
        return id is null ? Results.Created($"/api/activities/{activity.Id}", activity) : Results.Ok(activity);
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.ActivityManage)) return Results.Forbid();
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.ActivityManage)) return Results.Forbid();
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null) return Results.NotFound();
        db.Activities.Remove(activity);
        await db.CommitAsync(tx, actor.Id, "activity.deleted", id.ToString(), false, ct);
        return Results.NoContent();
    }

    private sealed record ActivityInput(string? Title, Guid? AssigneeId, TaskState? Status);
}
