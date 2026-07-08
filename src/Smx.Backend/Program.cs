using System.Text.Json.Serialization;
using Smx.Backend.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
// IRecordStore is registered in Task 13 (Cosmos) and overridden in tests.

var app = builder.Build();
app.MapProjectEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
