using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace VeryQueryable
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseHttpsRedirection();
            app.UseAuthorization();

            var databases = new Dictionary<string, SqliteConnection>();

            var connection = new SqliteConnection("Data Source=test.db");
            connection.Open();
            databases.Add("test", connection);

            app.MapGet("/{db}/{table}", (HttpContext context) =>
            {
                try
                {
                    var list = new List<Dictionary<string, string>>();
                    var table = context.GetRouteValue("table")?.ToString() ?? "default";
                    var db = context.GetRouteValue("db")?.ToString() ?? "default";

                    if (databases.TryGetValue(db, out var conn))
                    {
                        var command = conn.CreateCommand();
                        command.CommandText = $"select * from '{table}'";
                        command.Parameters.AddWithValue("$table", table);
                        //command.Parameters.AddWithValue("$id", id);

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
                    else
                    {
                        return JsonSerializer.Serialize(new
                        {
                            error = "1",
                            error_description = "Database not found"
                        });
                    }
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
            });

            app.Run();
        }
    }
}
