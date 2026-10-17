using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Govor.API.Common.Extensions;
using Govor.API.Hubs;
using Govor.Application.Authentication.JWT;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;

var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--migrate-only").ToArray());

var configuration = builder.Configuration;
var services = builder.Services;

builder.AddLogger();// Serilog

builder.Configuration.AddJsonFile("configs/ban_usernames.json", optional: false, reloadOnChange: true);

if (!builder.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(nameof(GovorDbContext))))
        throw new InvalidOperationException("Set ConnectionStrings__GovorDbContext in the application runtime environment.");

    if (!migrateOnly)
    {
        if (Encoding.UTF8.GetByteCount(configuration["JwtAccessOption:SecretKey"] ?? "") < 32)
            throw new InvalidOperationException("Set JwtAccessOption__SecretKey to a private key of at least 32 bytes.");
        if (string.IsNullOrWhiteSpace(configuration["EncryptionOption:Secret"]))
            throw new InvalidOperationException("Set EncryptionOption__Secret in the application runtime environment.");
    }
}


if (!migrateOnly && configuration.GetValue("Firebase:Enabled", true))
{
    var credentialsJson = configuration["Firebase:CredentialsJson"];
    var credentialsPath = configuration["Firebase:CredentialsPath"]
        ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
        ?? "secrets/firebase-adminsdk.json";
    var credential = !string.IsNullOrWhiteSpace(credentialsJson)
        ? CredentialFactory.FromJson<ServiceAccountCredential>(credentialsJson).ToGoogleCredential()
        : CredentialFactory.FromFile<ServiceAccountCredential>(
            Path.GetFullPath(credentialsPath, builder.Environment.ContentRootPath)).ToGoogleCredential();
    FirebaseApp.Create(new AppOptions()
    {
        Credential = credential
    });
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length > 0)
            policy.WithOrigins(origins);
        policy
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.Configure<JwtAccessOption>(configuration.GetSection(nameof(JwtAccessOption)));

// Add services
builder.Services.AddSignalRConf();// signalR

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["JwtAccessOption:SecretKey"]!))
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<GovorDbContext>();
                if (!await db.HasActiveSessionAsync(context.Principal))
                    context.Fail("The access session is invalid or revoked.");
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(); 

builder.Services.AddControllers();

// Init DI
builder.Services.AddServices();
builder.Services.AddOptionsConfiguration(configuration);

builder.Services.AddGovorDbContext(configuration); // GovorDbContext init

builder.Services.AddEndpointsApiExplorer();

services.AddSwaggerGen(options =>
{
    const string schemeId = "Bearer";
    
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Govor API", Version = "v1" });
    
    options.AddSecurityDefinition(schemeId, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        In = ParameterLocation.Header,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT Authorization header using the Bearer scheme. Example: 'Bearer {token}'"
    });
    
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>(0)
    });
});


//builder.Services.AddOpenApi();

if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["urls"]) &&
    string.IsNullOrWhiteSpace(configuration["HTTP_PORTS"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

var app = builder.Build();

if (migrateOnly)
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<GovorDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        app.Logger.LogInformation("Applying {Count} pending database migrations: {Migrations}",
            pending.Length, string.Join(", ", pending));
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database migrations completed.");
    }
    await app.DisposeAsync();
    return;
}

/*if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}*/

app.UseSwagger();
app.UseSwaggerUI();

//app.UseHttpsRedirection();

app.UseRouting();
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/server/ping",
    () => Results.Ok());

app.MapGet("/server/ready", async (GovorDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(cancellationToken) ||
            (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok();
    }
    catch (Exception) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapHub<ChatsHub>("/hubs/chats");
app.MapHub<FriendsHub>("/hubs/friends");
app.MapHub<ProfileHub>("/hubs/profiles");
app.MapHub<PresenceHub>("/hubs/presence");

app.MapSwagger()
    .RequireAuthorization();

app.Map("/", () => "Not for browsers");

app.Run();

public partial class Program { }
