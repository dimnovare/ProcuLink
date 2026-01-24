using ProcuLink.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Configure CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("http://localhost:8080", "http://localhost:8081", "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// Configure file-based repositories
var dataRoot = Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSingleton<IOrderRepository>(new FileOrderRepository(Path.Combine(dataRoot, "orders")));
builder.Services.AddSingleton<ISupplierProfileRepository>(new FileSupplierProfileRepository(Path.Combine(dataRoot, "suppliers")));
builder.Services.AddSingleton<IOutboundRepository>(new FileOutboundRepository(Path.Combine(dataRoot, "outbound")));
builder.Services.AddSingleton<IItemMappingRepository>(new FileItemMappingRepository(Path.Combine(dataRoot, "mappings")));

// Add HttpClient for webhook delivery
builder.Services.AddHttpClient();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ProcuLink API",
        Version = "v1",
        Description = "Purchase Order processing API"
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

app.UseAuthorization();

app.MapControllers();

app.Run();
