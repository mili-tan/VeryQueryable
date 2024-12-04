using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kenzo;
using System.Net;

#pragma warning disable ASP0019

namespace VeryQueryable
{
    public static class Program
    {
        public static Dictionary<string, SqliteConnection> Databases = new();
        public static Dictionary<string, string> HeadersDictionary = new();
        public static List<StaticPathEntity> StaticPaths = new();
        public static List<string> DynamicPaths = new();
        public static bool AllowAnyQuery = true;
        public static bool AllowAnyCORS = true;
        public static bool ShowLog = false;
        public static bool AllowShowExceptionMessage = true;

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthorization();

            try
            {
                var config = builder.Configuration.GetSection("VeryQueryable");
                if (!builder.Configuration.GetSection("VeryQueryable").Exists()) return;

                if (config.GetSection("DynamicPaths").Exists())
                    foreach (var item in config.GetSection("DynamicPaths").Get<string[]>()!)
                        DynamicPaths.Add(item);
                if (config.GetSection("StaticPaths").Exists())
                    foreach (var item in config.GetSection("StaticPaths").GetChildren())
                        StaticPaths.Add(item.Get<StaticPathEntity>());

                if (config.GetSection("Databases").Exists())
                    foreach (var item in config.GetSection("Databases").GetChildren())
                    {
                        var connection = new SqliteConnection(item.GetValue<string>("Connection")!);
                        connection.Open();
                        Databases.TryAdd(item.GetValue<string>("Name")!, connection);
                    }

                if (config.GetSection("AllowAnyQuery").Exists())
                    AllowAnyQuery = config.GetValue<bool>("AllowAnyQuery");
                if (config.GetSection("AllowAnyCORS").Exists())
                    AllowAnyCORS = config.GetValue<bool>("AllowAnyCORS");
                if (config.GetSection("AllowShowExceptionMessage").Exists())
                    AllowShowExceptionMessage = config.GetValue<bool>("AllowShowExceptionMessage");
                if (config.GetSection("ShowLog").Exists())
                    ShowLog = config.GetValue<bool>("ShowLog");

                if (config.GetSection("Herders").Exists())
                    foreach (var item in config.GetSection("Herders").GetChildren())
                        HeadersDictionary.Add(item.Key, item.Value ?? "");

                if (File.Exists("db.txt"))
                    foreach (var i in File.ReadAllLines("db.txt"))
                    {
                        if (string.IsNullOrWhiteSpace(i)) continue;
                        var split = i.Split(":");
                        var connection = new SqliteConnection(split.Last());
                        connection.Open();
                        Databases.TryAdd(split.First(), connection);
                    }

                if (File.Exists("path.txt"))
                    foreach (var i in File.ReadAllLines("path.txt"))
                    {
                        if (string.IsNullOrWhiteSpace(i)) continue;
                        if (i.Contains(":"))
                        {
                            var split = i.Split(":");
                            StaticPaths.Add(new StaticPathEntity()
                                {Database = split[0], Table = split[1], Path = split[2]});
                        }
                        else DynamicPaths.Add(i);
                    }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            var app = builder.Build();
            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = RealIP.Get(context);

                if (AllowAnyCORS)
                {
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    context.Response.Headers.Add("Access-Control-Allow-Methods", "*");
                    context.Response.Headers.Add("Access-Control-Allow-Headers", "*");
                    context.Response.Headers.Add("Access-Control-Allow-Credentials", "*");
                }

                context.Response.Headers.Add("X-Powered-By", "VeryQueryable/0.1");

                foreach (var item in HeadersDictionary)
                    context.Response.Headers.TryAdd(item.Key, item.Value);

                if (ShowLog)
                    Console.WriteLine(
                        $"{context.Connection.RemoteIpAddress}|{context.Request.Headers.UserAgent}:{WebUtility.UrlDecode(context.Request.QueryString.ToString())}");

                await next(context);
            });

            foreach (var item in StaticPaths)
            {
                Console.WriteLine("StaticPaths:" + item.Path);
                app.Map(item.Path, (HttpContext context) => context.DoQuery(item));
            }

            foreach (var path in DynamicPaths)
            {
                Console.WriteLine("DynamicPaths:" + path);
                app.Map(path, (HttpContext context) =>
                {
                    var table = context.GetRouteValue("table")?.ToString() ?? "default";
                    var db = context.GetRouteValue("db")?.ToString() ?? "default";
                    return context.DoQuery(db, table);
                });
            }

            app.Run();
        }

        public static string DoQuery(this HttpContext context, StaticPathEntity entity)
        {
            context.Response.ContentType = "application/json";
            var querys = context.Request.Query.ToDictionary();
            var keys = entity.KeyNames ?? new KeyNameEntity();
            var codes = entity.StatusCodes ?? new StatusCodesEntity();

            var limit = 0;
            var offset = 0;

            try
            {
                if (entity.Pageable ?? false)
                {
                    if (querys.TryGetValue("limit", out var limitValue))
                    {
                        limit = int.Parse(limitValue);
                        querys.Remove("limit");
                    }
                    if (querys.TryGetValue("offset", out var offsetValue))
                    {
                        offset = int.Parse(offsetValue);
                        querys.Remove("offset");
                    }
                }

                if (entity.RequiredHerders != null && entity.RequiredHerders.Any() && entity.RequiredHerders.Any(x =>
                        !context.Request.Headers.Keys.Contains(x.Key) ||
                        context.Request.Headers[x.Key].ToString() != x.Value))
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InternalInvalid},
                        {keys.Description, "Missing required request headers"}
                    });

                if (context.Request.Method.ToUpper() != "GET")
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InternalInvalid},
                        {keys.Description, "Unsupported request mode, please GET"}
                    });
                if (!Databases.TryGetValue(entity.Database, out var conn))
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InternalInvalid},
                        {keys.Description, "Database not found"}
                    });
                if (entity.RequiredQuerys != null && entity.RequiredQuerys.Any(i => !querys.Keys.Contains(i)))
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InputInvalid},
                        {keys.Description, "Required query is missing"}
                    });
                if (entity.BannedQuerys != null && entity.BannedQuerys.Length != 0)
                    foreach (var item in querys.Where(item => entity.BannedQuerys.Contains(item.Key)))
                        querys.Remove(item.Key);
                if (entity.AllowedQuerys != null && entity.AllowedQuerys.Length != 0)
                    foreach (var item in querys.Where(item => !entity.AllowedQuerys.Contains(item.Key)))
                        querys.Remove(item.Key);
                if (querys.Count == 0 && !AllowAnyQuery)
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InputInvalid},
                        {keys.Description, "No valid query"}
                    });
                if (querys.Any(item => !IsValidInput(item.Value.ToString()) || !IsValidInput(item.Key)))
                {
                    return JsonSerializer.Serialize(new JsonObject()
                    {
                        {keys.Status, codes.InputInvalid},
                        {keys.Description, "Invalid or failure query value"}
                    });
                }

                var list = new List<Dictionary<string, string>>();
                var command = conn.CreateCommand();
                command.CommandText = $"SELECT * FROM '{entity.Table}'";

                var queryKeyList = querys.Keys.ToList().Select(x => $"{x} = ${x}").ToList();
                if (queryKeyList.Any()) command.CommandText += " WHERE " + string.Join(" AND ", queryKeyList);
                if (limit != 0 && (entity.Pageable ?? false))
                    command.CommandText +=
                        $" LIMIT {((entity.Takes.HasValue && limit > entity.Takes.Value) ? entity.Takes.Value : limit)} OFFSET {offset}";
                else if (entity.Takes.HasValue) command.CommandText += " LIMIT " + entity.Takes.Value;

                foreach (var item in querys)
                    command.Parameters.AddWithValue($"${item.Key}", item.Value.ToString());

                //Console.WriteLine(command.CommandText);

                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        list.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i),
                            i => reader.GetValue(i).ToString())!);

                if (entity.Takes.HasValue) list = list.Take(entity.Takes.Value).ToList();

                if (entity.BannedResults != null && entity.BannedResults.Length != 0)
                    foreach (var item in list)
                    {
                        foreach (var banned in entity.BannedResults) item.Remove(banned);
                    }

                if (entity.AllowedResults != null && entity.AllowedResults.Length != 0)
                    foreach (var item in list)
                    {
                        foreach (var key in item.Keys.Where(key => !entity.AllowedResults.Contains(key)))
                            item.Remove(key);
                    }

                return JsonSerializer.Serialize(new JsonObject()
                {
                    {keys.Status, codes.OK},
                    {keys.Description, "OK"},
                    {keys.Count, list.Count},
                    {
                        keys.Result,
                        (entity.NotArray ?? false)
                            ? JsonNode.Parse(JsonSerializer.Serialize(list.First()))
                            : JsonNode.Parse(JsonSerializer.Serialize(list))
                    }
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return JsonSerializer.Serialize(new JsonObject()
                {
                    {keys.Status, codes.Error},
                    {keys.Description, AllowShowExceptionMessage ? e.Message : "Internal database error"}
                });
            }
        }

        public static string DoQuery(this HttpContext context, string db, string table)
        {
            return DoQuery(context, new StaticPathEntity() {Database = db, Table = table});
        }

        static bool IsValidInput(string input)
        {
            return input.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ');
        }
    }

    public class StaticPathEntity
    {
        public string Database { get; set; }
        public string Table { get; set; }
        public string Path { get; set; }
        public int? Takes { get; set; }
        public bool? NotArray { get; set; }
        public bool? Pageable { get; set; }

        public KeyNameEntity? KeyNames { get; set; }
        public StatusCodesEntity? StatusCodes { get; set; }
        public Dictionary<string, string>? RequiredHerders { get; set; }
        public string[]? RequiredQuerys { get; set; }
        public string[]? AllowedQuerys { get; set; }
        public string[]? BannedQuerys { get; set; }
        public string[]? BannedResults { get; set; }
        public string[]? AllowedResults { get; set; }
    }

    public class KeyNameEntity
    {
        public string Status { get; set; } = "status";
        public string Description { get; set; } = "description";
        public string Count { get; set; } = "count";
        public string Result { get; set; } = "data";
    }

    public class StatusCodesEntity
    {
        public int OK { get; set; } = 1;
        public int Error { get; set; } = 0;
        public int InputInvalid { get; set; } = 0;
        public int InternalInvalid { get; set; } = -1;
    }
}
