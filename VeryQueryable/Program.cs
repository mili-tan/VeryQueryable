using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace VeryQueryable
{
    public static class Program
    {
        public static Dictionary<string, SqliteConnection> Databases = new();
        public static Dictionary<string, string> HeadersDictionary = new();
        public static List<(string path, string db, string table)> StaticPaths = new();
        public static List<string> DynamicPaths = new();
        public static bool AllowAnyQuery = true;
        public static bool AllowAnyCORS = true;

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthorization();

            try
            {
                var config = builder.Configuration.GetSection("VeryQueryable");
                if (!builder.Configuration.GetSection("VeryQueryable").Exists()) return;
                
                if (config.GetSection("DynamicPaths").Exists()) 
                    foreach (var item in config.GetSection("DynamicPaths").Get<string[]>()!) DynamicPaths.Add(item);
                if (config.GetSection("StaticPaths").Exists())
                    foreach (var item in config.GetSection("StaticPaths").GetChildren())
                        StaticPaths.Add(new ValueTuple<string, string, string>(item.GetValue<string>("Path")!,
                            item.GetValue<string>("Database")!,
                            item.GetValue<string>("Table")!));

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
                            StaticPaths.Add(new ValueTuple<string, string, string>(split[0], split[1], split[2]));
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
                if (AllowAnyCORS)
                {
                    context.Response.Headers?.Add("Access-Control-Allow-Origin", "*");
                    context.Response.Headers?.Add("Access-Control-Allow-Methods", "*");
                    context.Response.Headers?.Add("Access-Control-Allow-Headers", "*");
                    context.Response.Headers?.Add("Access-Control-Allow-Credentials", "*");
                }

                context.Response.Headers?.Add("X-Powered-By", "VeryQueryable/0.1");

                foreach (var item in HeadersDictionary)
                    context.Response.Headers.TryAdd(item.Key, item.Value);

                await next(context);
            });

            foreach (var (path, db, table) in StaticPaths)
                app.Map(path, (HttpContext context) => context.DoQuery(db, table));
            foreach (var path in DynamicPaths)
            {
                app.Map(path, (HttpContext context) =>
                {
                    var table = context.GetRouteValue("table")?.ToString() ?? "default";
                    var db = context.GetRouteValue("db")?.ToString() ?? "default";
                    return context.DoQuery(db, table);
                });
            }

            app.Run();
        }

        public static string DoQuery(this HttpContext context, string db, string table,
            string[]? requiredParameters = null, string[]? allowedParameters = null, string[]? bannedParameters = null)
        {
            try
            {
                context.Response.ContentType = "application/json";
                var querys = context.Request.Query.ToDictionary();

                if (context.Request.Method.ToUpper() != "GET")
                    return JsonSerializer.Serialize(new
                        {status = -1, description = "Unsupported request mode, please GET"});
                if (!Databases.TryGetValue(db, out var conn))
                    return JsonSerializer.Serialize(new
                    {
                        status = -1,
                        description = "Database not found"
                    });
                if (requiredParameters != null &&
                    requiredParameters.Any(i => !querys.Keys.Contains(i)))
                    return JsonSerializer.Serialize(new
                    {
                        status = 0,
                        description = "No valid query"
                    });
                if (bannedParameters != null &&
                    bannedParameters.Length != 0)
                    foreach (var item in querys.Where(item => bannedParameters.Contains(item.Key)))
                        querys.Remove(item.Key);
                if (allowedParameters != null &&
                    allowedParameters.Length != 0)
                    foreach (var item in querys.Where(item => !allowedParameters.Contains(item.Key)))
                        querys.Remove(item.Key);
                if (querys.Count == 0 && !AllowAnyQuery)
                    return JsonSerializer.Serialize(new
                    {
                        status = 0,
                        description = "No valid query"
                    });

                var list = new List<Dictionary<string, string>>();
                var command = conn.CreateCommand();
                command.CommandText = $"SELECT * FROM {table}";

                var queryKeyList = querys.Keys.ToList().Select(x => $"{x} = ${x}").ToList();
                if (queryKeyList.Any()) command.CommandText += " WHERE " + string.Join(" AND ", queryKeyList);

                foreach (var item in querys)
                    command.Parameters.AddWithValue($"${item.Key}", item.Value.ToString());

                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        list.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i),
                            i => reader.GetValue(i).ToString())!);

                return JsonSerializer.Serialize(new
                {
                    status = 1,
                    description = "OK",
                    count = list.Count,
                    data = list
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return JsonSerializer.Serialize(new
                {
                    error = 0,
                    error_description = e.Message
                });
            }
        }
    }
}
