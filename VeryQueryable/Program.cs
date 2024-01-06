using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace VeryQueryable
{
    public static class Program
    {
        public static Dictionary<string, SqliteConnection> Databases = new();
        public static List<(string path, string db, string table)> StaticPaths = new();
        public static List<string> DynamicPaths = new();

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthorization();

            try
            {
                var config = builder.Configuration.GetSection("VeryQuery");
                foreach (var item in config.GetSection("DynamicPaths").Get<string[]>()!) DynamicPaths.Add(item);
                foreach (var item in config.GetSection("StaticPaths").GetChildren())
                    StaticPaths.Add(new ValueTuple<string, string, string>(item.GetValue<string>("Path")!,
                        item.GetValue<string>("Database")!,
                        item.GetValue<string>("Table")!));

                foreach (var item in config.GetSection("Databases").GetChildren())
                {
                    var connection = new SqliteConnection(item.GetValue<string>("Connection")!);
                    connection.Open();
                    Databases.TryAdd(item.GetValue<string>("Name")!, connection);
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
                context.Response.Headers?.Add("Access-Control-Allow-Origin", "*");
                context.Response.Headers?.Add("Access-Control-Allow-Methods", "*");
                context.Response.Headers?.Add("Access-Control-Allow-Headers", "*");
                context.Response.Headers?.Add("Access-Control-Allow-Credentials", "*");
                await next(context);
            });

            foreach (var i in File.ReadAllLines("db.txt"))
            {
                if (string.IsNullOrWhiteSpace(i)) continue;
                var split = i.Split(":");
                var connection = new SqliteConnection(split.Last());
                connection.Open();
                Databases.TryAdd(split.First(), connection);
            }

            foreach (var i in File.ReadAllLines("path.txt"))
            {
                if (string.IsNullOrWhiteSpace(i)) continue;
                if (i.Contains(":"))
                {
                    var split = i.Split(":");
                    StaticPaths.Add(new(split[0], split[1], split[2]));
                }
                else
                    DynamicPaths.Add(i);
            }

            foreach (var item in StaticPaths)
                app.Map(item.path, (HttpContext context) => context.DoQuery(item.db, item.table));

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

        public static string DoQuery(this HttpContext context, string db, string table)
        {
            try
            {
                context.Response.ContentType = "application/json";

                if (context.Request.Method.ToUpper() != "GET")
                    return JsonSerializer.Serialize(new
                        {error = "1", error_description = "Unsupported request mode, please GET"});
                if (!Databases.TryGetValue(db, out var conn))
                    return JsonSerializer.Serialize(new
                    {
                        error = "1",
                        error_description = "Database not found"
                    });

                var list = new List<Dictionary<string, string>>();
                var command = conn.CreateCommand();
                command.CommandText = $"SELECT * FROM {table}";

                var queryKeyList = context.Request.Query.Keys.ToList().Select(x => $"{x} = ${x}").ToList();
                if (queryKeyList.Any()) command.CommandText += " WHERE " + string.Join(" AND ", queryKeyList);

                foreach (var item in context.Request.Query)
                    command.Parameters.AddWithValue($"${item.Key}", item.Value.ToString());

                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        list.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(i => reader.GetName(i),
                            i => reader.GetValue(i).ToString())!);

                return JsonSerializer.Serialize(new
                {
                    error = "0",
                    error_description = "OK",
                    count = list.Count,
                    data = list
                });

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return JsonSerializer.Serialize(new
                {
                    error = "1",
                    error_description = e.Message
                });
            }
        }
    }
}
