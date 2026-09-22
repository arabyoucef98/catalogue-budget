using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var connectionString = configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default est obligatoire.");
var signingKey = configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey est obligatoire.");

builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton(new PasswordHasher());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = configuration["Jwt:Issuer"],
            ValidateAudience = true, ValidAudience = configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("Administrator", policy => policy.RequireRole("Administrateur")));
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
await EnsureDatabaseAsync(app.Services, configuration, connectionString);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/auth/login", async (LoginRequest request, Database db, PasswordHasher hasher, IConfiguration config) =>
{
    await using var connection = await db.OpenAsync();
    var user = await connection.QuerySingleOrDefaultAsync<UserRecord>(
        "select id, username, display_name as DisplayName, role, password_hash as PasswordHash from app_users where username=@Username and is_active=true",
        new { request.Username });
    if (user is null || !hasher.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
    var token = new JwtSecurityToken(config["Jwt:Issuer"], config["Jwt:Audience"], claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)), SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), user = new { user.Id, user.Username, user.DisplayName, user.Role } });
});

app.MapGet("/api/products", async (Database db, ClaimsPrincipal principal) =>
{
    await using var connection = await db.OpenAsync();
    var products = await connection.QueryAsync<Product>(
        "select id, code, designation, category, purchase_price as PurchasePrice, sale_price as SalePrice, stock_quantity as StockQuantity, low_stock_threshold as LowStockThreshold from products order by designation");
    return Results.Ok(products);
}).RequireAuthorization();

app.MapPost("/api/sales", async (CreateSaleRequest request, Database db, ClaimsPrincipal principal) =>
{
    if (request.Lines is null || request.Lines.Count == 0)
        return Results.BadRequest(new { error = "Le ticket doit contenir au moins une ligne." });
    if (request.PaymentMethod.Equals("Espèces", StringComparison.OrdinalIgnoreCase) && request.AmountReceived < request.Total)
        return Results.BadRequest(new { error = "Le montant reçu est insuffisant." });

    if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var cashierId))
        return Results.Unauthorized();
    await using var connection = await db.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
    try
    {
        var saleId = Guid.NewGuid();
        var inserted = await connection.ExecuteAsync(
            "insert into sales(id,cashier_id,payment_method,subtotal,discount,total,amount_received,change_due,idempotency_key) values(@saleId,@cashierId,@PaymentMethod,@Subtotal,@Discount,@Total,@AmountReceived,@ChangeDue,@IdempotencyKey)",
            new { saleId, cashierId, request.PaymentMethod, request.Subtotal, request.Discount, request.Total, ChangeDue = request.AmountReceived - request.Total, request.IdempotencyKey }, transaction);
        if (inserted != 1) throw new InvalidOperationException("Échec de création du ticket.");
        foreach (var line in request.Lines)
        {
            var product = await connection.QuerySingleOrDefaultAsync<Product>(
                "select id, code, designation, sale_price as SalePrice, stock_quantity as StockQuantity from products where code=@Code for update",
                new { line.Code }, transaction);
            if (product is null) throw new InvalidOperationException($"Article inconnu : {line.Code}");
            if (line.Quantity <= 0 || product.StockQuantity < line.Quantity)
                throw new InvalidOperationException($"Stock insuffisant pour {line.Code}.");
            await connection.ExecuteAsync(
                "insert into sale_lines(sale_id,product_id,code,designation,quantity,unit_price,discount,line_total) values(@saleId,@ProductId,@Code,@Designation,@Quantity,@UnitPrice,@Discount,@LineTotal)",
                new { saleId, ProductId = product.Id, product.Code, product.Designation, line.Quantity, UnitPrice = line.UnitPrice, line.Discount, line.LineTotal }, transaction);
            await connection.ExecuteAsync("update products set stock_quantity=stock_quantity-@Quantity,updated_at=now() where id=@ProductId",
                new { line.Quantity, ProductId = product.Id }, transaction);
            await connection.ExecuteAsync(
                "insert into stock_movements(product_id,sale_id,movement_type,quantity_out,note,created_by) values(@ProductId,@saleId,'Vente',@Quantity,'Vente au comptoir',@cashierId)",
                new { ProductId = product.Id, saleId, line.Quantity, cashierId }, transaction);
        }
        await connection.ExecuteAsync("insert into audit_events(actor_id,action,entity_type,entity_id,details) values(@cashierId,'SaleCreated','Sale',@saleId,jsonb_build_object('total',@Total))",
            new { cashierId, saleId, request.Total }, transaction);
        await transaction.CommitAsync();
        var ticket = await connection.QuerySingleAsync<long>("select ticket_number from sales where id=@saleId", new { saleId });
        return Results.Created($"/api/sales/{saleId}", new { id = saleId, ticketNumber = ticket, request.Total });
    }
    catch (PostgresException ex) when (ex.SqlState == "23505")
    {
        await transaction.RollbackAsync();
        return Results.Conflict(new { error = "Cette vente a déjà été enregistrée.", detail = ex.ConstraintName });
    }
    catch (InvalidOperationException ex)
    {
        await transaction.RollbackAsync();
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/audit", async (Database db, ClaimsPrincipal principal) =>
{
    await using var connection = await db.OpenAsync();
    var events = await connection.QueryAsync("select id, action, entity_type as EntityType, entity_id as EntityId, details, created_at as CreatedAt from audit_events order by created_at desc limit 500");
    return Results.Ok(events);
}).RequireAuthorization("Administrator");

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services, IConfiguration configuration, string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var password = configuration["Seed:AdminPassword"];
    if (string.IsNullOrWhiteSpace(password))
        return;
    var exists = await connection.ExecuteScalarAsync<bool>("select exists(select 1 from app_users where username='admin')");
    if (exists)
        return;
    var hasher = services.GetRequiredService<PasswordHasher>();
    await connection.ExecuteAsync(
        "insert into app_users(username,display_name,role,password_hash) values('admin','Administrateur','Administrateur',@PasswordHash)",
        new { PasswordHash = hasher.Hash(password) });
}

public sealed record LoginRequest(string Username, string Password);
public sealed record SaleLineRequest(string Code, decimal Quantity, decimal UnitPrice, decimal Discount, decimal LineTotal);
public sealed record CreateSaleRequest(string PaymentMethod, decimal Subtotal, decimal Discount, decimal Total, decimal AmountReceived, Guid IdempotencyKey, List<SaleLineRequest> Lines);
public sealed record Product(Guid Id, string Code, string Designation, string? Category, decimal PurchasePrice, decimal SalePrice, decimal StockQuantity, decimal LowStockThreshold);
public sealed record UserRecord(Guid Id, string Username, string DisplayName, string Role, string PasswordHash);

public sealed class Database(string connectionString)
{
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}

public sealed class PasswordHasher
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA512, 32);
        return $"pbkdf2-sha512$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations)) return false;
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
