using System.Text.Json.Serialization;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;
using SententiamConverte.Server;

// Teamserver für gemeinsame TMs, Terminologien und Wissensdatenbanken.
//
//   SententiamConverte.Server [--daten <ordner>] [--urls http://0.0.0.0:5280]
//   SententiamConverte.Server benutzer neu <name> [admin|bearbeiter|leser]
//   SententiamConverte.Server benutzer liste | benutzer entfernen <name>
//   SententiamConverte.Server ressource neu <tm|terminologie|wissen> <name> [beschreibung]

var dataDir = ArgValue(args, "--daten") ?? Environment.GetEnvironmentVariable("SENTENTIAM_SERVER_DATA") ?? Path.Combine(AppContext.BaseDirectory, "daten");
Directory.CreateDirectory(dataDir);

if (args.Length > 0 && !args[0].StartsWith("--"))
    return ServerCli.Run(args, dataDir);

var app = ServerApp.Build(args, dataDir);
app.Run();
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

namespace SententiamConverte.Server
{
    public static class ServerCli
    {
        public static int Run(string[] args, string dataDir)
        {
            var users = new UserStore(dataDir);
            switch (args)
            {
                case ["benutzer", "neu", var name, ..]:
                    var role = args.Length > 3 ? ParseRole(args[3]) : Role.Editor;
                    Console.WriteLine($"Benutzer „{name}“ ({role}) angelegt. Zugangsschlüssel (nur jetzt sichtbar):");
                    Console.WriteLine(users.Add(name, role));
                    return 0;
                case ["benutzer", "liste"]:
                    foreach (var u in users.All) Console.WriteLine($"{u.Name}\t{u.Role}\tseit {u.Created:d}");
                    return 0;
                case ["benutzer", "entfernen", var name]:
                    Console.WriteLine(users.Remove(name) ? "Entfernt." : "Nicht gefunden.");
                    return 0;
                case ["ressource", "neu", var kind, var name, ..]:
                    using (var store = new ResourceStore(dataDir))
                        store.Create(ParseKind(kind), name, args.Length > 4 ? string.Join(' ', args[4..]) : null);
                    Console.WriteLine("Angelegt.");
                    return 0;
                default:
                    Console.WriteLine("Befehle: benutzer neu <name> [admin|bearbeiter|leser] | benutzer liste | benutzer entfernen <name> | ressource neu <tm|terminologie|wissen> <name> [beschreibung]");
                    return 1;
            }
        }

        public static Role ParseRole(string s) => s.ToLowerInvariant() switch
        {
            "admin" => Role.Admin,
            "leser" or "reader" => Role.Reader,
            _ => Role.Editor,
        };

        public static ResourceKind ParseKind(string s) => s.ToLowerInvariant() switch
        {
            "tm" => ResourceKind.Tm,
            "terminologie" or "termbase" or "tb" => ResourceKind.Termbase,
            _ => ResourceKind.Knowledge,
        };
    }

    public static class ServerApp
    {
        public static WebApplication Build(string[] args, string dataDir)
        {
            var builder = WebApplication.CreateBuilder(args);
            if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
                builder.WebHost.UseUrls("http://0.0.0.0:5280");
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.SerializerOptions.PropertyNameCaseInsensitive = true;
            });

            var users = new UserStore(dataDir);
            if (users.All.Count == 0)
            {
                var token = users.Add("admin", Role.Admin);
                Console.WriteLine("Erster Start: Benutzer „admin“ angelegt. Zugangsschlüssel (bitte sicher aufbewahren):");
                Console.WriteLine(token);
            }
            builder.Services.AddSingleton(users);
            builder.Services.AddSingleton(new ResourceStore(dataDir));
            builder.Services.AddSingleton(new ActivityLog(dataDir));

            var app = builder.Build();
            Map(app);
            return app;
        }

        private static UserAccount? User(HttpContext ctx) => ctx.Items["user"] as UserAccount;

        private static IResult? Require(HttpContext ctx, Role role) =>
            User(ctx) is { } u && u.Role >= role ? null : Results.StatusCode(403);

        public static void Map(WebApplication app)
        {
            app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }));

            // Anmeldung per Zugangsschlüssel
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api/v1") && !ctx.Request.Path.StartsWithSegments("/api/v1/health"))
                {
                    var header = ctx.Request.Headers.Authorization.ToString();
                    var user = header.StartsWith("Bearer ") ? ctx.RequestServices.GetRequiredService<UserStore>().Authenticate(header[7..].Trim()) : null;
                    if (user == null)
                    {
                        ctx.Response.StatusCode = 401;
                        return;
                    }
                    ctx.Items["user"] = user;
                }
                await next();
            });

            var api = app.MapGroup("/api/v1");

            api.MapGet("/me", (HttpContext ctx) => new WhoAmI(User(ctx)!.Name, User(ctx)!.Role.ToString()));
            api.MapGet("/resources", (ResourceStore store) => store.List());
            api.MapPost("/resources", (HttpContext ctx, ResourceInfo info, ResourceStore store, ActivityLog log) =>
            {
                if (Require(ctx, Role.Admin) is { } denied) return denied;
                if (!ResourceStore.ValidName(info.Name)) return Results.BadRequest("Ungültiger Name.");
                store.Create(info.Kind, info.Name, info.Description);
                log.Add(User(ctx)!.Name, info.Kind, info.Name, "Ressource angelegt");
                return Results.Ok();
            });
            api.MapGet("/activity", (int? max, ActivityLog log) => log.Latest(max ?? 50));

            // ---------- Translation Memories ----------
            var tm = api.MapGroup("/tm/{name}");
            tm.MapGet("/count", (string name, ResourceStore s) => Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => t.Count()));
            tm.MapPost("/search", (string name, TmSearchRequest r, ResourceStore s) =>
                Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => t.Search(r.SrcLang, r.TgtLang, r.Text, r.MinScore, Math.Clamp(r.Max, 1, 50))));
            tm.MapPost("/fragments", (string name, TmSearchRequest r, ResourceStore s) =>
                Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => t.Fragments(r.SrcLang, r.TgtLang, r.Text, Math.Clamp(r.Max, 1, 50))));
            tm.MapPost("/concordance", (string name, TmSearchRequest r, ResourceStore s) =>
                Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => t.Concordance(r.SrcLang, r.TgtLang, r.Text, r.InTarget, Math.Clamp(r.Max, 1, 200))));
            tm.MapGet("/units", (string name, string? filter, int? offset, int? limit, ResourceStore s) =>
                Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => t.Browse(filter, offset ?? 0, Math.Clamp(limit ?? 50, 1, 1000))));
            tm.MapPost("/units", (HttpContext ctx, string name, TmAddRequest r, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var user = User(ctx)!.Name;
                var result = Do(s, ResourceKind.Tm, name, (ITranslationMemory t) =>
                {
                    t.Add(r.SrcLang, r.TgtLang, r.Source, r.Target, user, r.Context, r.Created);
                    return 0;
                });
                log.Add(user, ResourceKind.Tm, name, $"{TagTextPlain(r.Source)} → {TagTextPlain(r.Target)}");
                return result;
            });
            tm.MapPost("/units/batch", (HttpContext ctx, string name, List<TmAddRequest> units, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var result = Do(s, ResourceKind.Tm, name, (ITranslationMemory t) =>
                    t.AddMany(units.Select(u => (u.SrcLang, u.TgtLang, u.Source, u.Target, u.Created))));
                log.Add(User(ctx)!.Name, ResourceKind.Tm, name, $"{units.Count} Einheiten importiert");
                return result;
            });
            tm.MapPut("/units/{id:long}", (HttpContext ctx, string name, long id, TmUpdateRequest r, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var result = Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => { t.Update(id, r.Source, r.Target); return 0; });
                log.Add(User(ctx)!.Name, ResourceKind.Tm, name, $"geändert: {TagTextPlain(r.Target)}");
                return result;
            });
            tm.MapDelete("/units/{id:long}", (HttpContext ctx, string name, long id, ResourceStore s) =>
                Require(ctx, Role.Editor) ?? Do(s, ResourceKind.Tm, name, (ITranslationMemory t) => { t.Delete(id); return 0; }));

            // ---------- Terminologie ----------
            var tb = api.MapGroup("/tb/{name}");
            tb.MapGet("/count", (string name, ResourceStore s) => Do(s, ResourceKind.Termbase, name, (ITermbase t) => t.ConceptCount()));
            tb.MapPost("/recognize", (string name, TermRecognizeRequest r, ResourceStore s) =>
                Do(s, ResourceKind.Termbase, name, (ITermbase t) => t.Recognize(r.Text, r.SrcLang, r.TgtLang)));
            tb.MapGet("/lookup", (string name, string lang, string text, int? max, ResourceStore s) =>
                Do(s, ResourceKind.Termbase, name, (ITermbase t) => t.Lookup(lang, text, Math.Clamp(max ?? 30, 1, 200))));
            tb.MapGet("/concepts", (string name, ResourceStore s) => Do(s, ResourceKind.Termbase, name, (ITermbase t) => t.AllConcepts().ToList()));
            tb.MapGet("/concepts/{id:long}", (string name, long id, ResourceStore s) => Do(s, ResourceKind.Termbase, name, (ITermbase t) => t.GetConcept(id)));
            tb.MapPost("/concepts", (HttpContext ctx, string name, ConceptAddRequest r, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var user = User(ctx)!.Name;
                var result = Do(s, ResourceKind.Termbase, name, (ITermbase t) =>
                    t.AddConcept(r.Terms.Select(x => (x.Lang, x.Text, x.Status)), r.Definition, r.Domain, r.Source ?? user));
                log.Add(user, ResourceKind.Termbase, name, string.Join(" · ", r.Terms.Select(x => $"{x.Lang}: {x.Text}")));
                return result;
            });
            tb.MapDelete("/concepts/{id:long}", (HttpContext ctx, string name, long id, ResourceStore s) =>
                Require(ctx, Role.Editor) ?? Do(s, ResourceKind.Termbase, name, (ITermbase t) => { t.DeleteConcept(id); return 0; }));

            // ---------- Wissen ----------
            var kb = api.MapGroup("/kb/{name}");
            kb.MapGet("/count", (string name, ResourceStore s) => Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) => k.Count()));
            kb.MapPost("/search", (string name, KnowledgeSearchRequest r, ResourceStore s) =>
                Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) => k.Search(r.Language, r.Query, Math.Clamp(r.Max, 1, 50))));
            kb.MapGet("/entries", (string name, string? language, string? filter, int? offset, int? limit, ResourceStore s) =>
                Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) =>
                    k.Browse(string.IsNullOrEmpty(language) ? null : language, filter, offset ?? 0, Math.Clamp(limit ?? 50, 1, 500))));
            kb.MapPost("/entries", (HttpContext ctx, string name, KnowledgeEntry e, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var user = User(ctx)!.Name;
                var entry = e with { Id = 0, Author = string.IsNullOrEmpty(e.Author) ? user : $"{e.Author} · {user}", Created = DateTime.UtcNow, Modified = DateTime.UtcNow };
                var result = Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) => k.Add(entry));
                log.Add(user, ResourceKind.Knowledge, name, $"[{entry.Language}] {entry.Title}");
                return result;
            });
            kb.MapPut("/entries/{id:long}", (HttpContext ctx, string name, long id, KnowledgeEntry e, ResourceStore s, ActivityLog log) =>
            {
                if (Require(ctx, Role.Editor) is { } denied) return denied;
                var result = Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) => { k.Update(e with { Id = id }); return 0; });
                log.Add(User(ctx)!.Name, ResourceKind.Knowledge, name, $"geändert: {e.Title}");
                return result;
            });
            kb.MapDelete("/entries/{id:long}", (HttpContext ctx, string name, long id, ResourceStore s) =>
                Require(ctx, Role.Editor) ?? Do(s, ResourceKind.Knowledge, name, (IKnowledgeBase k) => { k.Delete(id); return 0; }));
        }

        private static IResult Do<T, TResult>(ResourceStore store, ResourceKind kind, string name, Func<T, TResult> action) where T : class, ILanguageResource
        {
            try { return Results.Ok(store.With(kind, name, action)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        }

        private static string TagTextPlain(string s) => SententiamConverte.Core.Model.TagText.StripTags(s);
    }
}
