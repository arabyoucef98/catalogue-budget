using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

const int MaximumUploadBytes = ProductWorkbookImporter.MaximumWorkbookBytes;
var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var connectionString = configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default est obligatoire.");
var signingKey = configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey est obligatoire.");
var issuer = configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer est obligatoire.");
var audience = configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience est obligatoire.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey doit contenir au moins 32 octets.");

builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<ProductWorkbookImporter>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy(
    "Administrator", policy => policy.RequireRole("Administrateur")));

var app = builder.Build();
await EnsureDatabaseAsync(app.Services, configuration, connectionString);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/auth/login", async (LoginRequest? request, Database db, PasswordHasher hasher, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrEmpty(request.Password))
        return Results.BadRequest(new { error = "Nom d’utilisateur et mot de passe obligatoires." });

    await using var connection = await db.OpenAsync(cancellationToken);
    var user = await connection.QuerySingleOrDefaultAsync<UserRecord>(
        "select id, username, display_name as DisplayName, role, password_hash as PasswordHash from app_users where username=@Username and is_active=true",
        new { Username = request.Username.Trim() });
    if (user is null || !hasher.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };
    var token = new JwtSecurityToken(issuer, audience, claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256));
    return Results.Ok(new
    {
        token = new JwtSecurityTokenHandler().WriteToken(token),
        user = new { user.Id, user.Username, user.DisplayName, user.Role }
    });
});

app.MapGet("/api/products", async (Database db, CancellationToken cancellationToken) =>
{
    await using var connection = await db.OpenAsync(cancellationToken);
    var products = await connection.QueryAsync<Product>(
        new CommandDefinition(
            "select id, code, designation, category, purchase_price as PurchasePrice, sale_price as SalePrice, stock_quantity as StockQuantity, low_stock_threshold as LowStockThreshold from products order by designation, code",
            cancellationToken: cancellationToken));
    return Results.Ok(products);
}).RequireAuthorization();

app.MapPost("/api/sales", async (CreateSaleRequest? request, ClaimsPrincipal principal, Database db, CancellationToken cancellationToken) =>
{
    if (request is null)
        return Results.BadRequest(new { error = "Corps de requête de vente invalide." });
    var validationError = ValidateSaleRequest(request);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });
    if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var cashierId))
        return Results.Unauthorized();

    var requestHash = ComputeSaleRequestHash(request);
    await using var connection = await db.OpenAsync(cancellationToken);
    var existing = await FindSaleByIdempotencyKeyAsync(connection, request.IdempotencyKey);
    if (existing is not null)
        return IdempotentSaleResponse(existing, requestHash);

    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    try
    {
        var saleId = Guid.NewGuid();
        var grossSubtotal = 0m;
        var totalDiscount = 0m;
        var total = 0m;
        var saleLines = new List<SaleLineToSave>(request.Lines.Count);

        foreach (var line in request.Lines.OrderBy(line => line.Code, StringComparer.Ordinal))
        {
            var product = await connection.QuerySingleOrDefaultAsync<Product>(
                new CommandDefinition(
                    "select id, code, designation, category, purchase_price as PurchasePrice, sale_price as SalePrice, stock_quantity as StockQuantity, low_stock_threshold as LowStockThreshold from products where code=@Code for update",
                    new { line.Code }, transaction, cancellationToken: cancellationToken));
            if (product is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Results.NotFound(new { error = $"Article inconnu : {line.Code}" });
            }
            if (line.UnitPrice != product.SalePrice)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Results.Conflict(new { error = $"Le prix de l’article {line.Code} a changé. Actualisez le catalogue." });
            }
            if (product.StockQuantity < line.Quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Results.Conflict(new { error = $"Stock insuffisant pour {line.Code}." });
            }
            if (product.SalePrice > 0 && line.Quantity > 9_999_999_999_999_999.99m / product.SalePrice)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Results.BadRequest(new { error = $"Le montant de la ligne {line.Code} dépasse la limite autorisée." });
            }

            var lineGross = decimal.Round(line.Quantity * product.SalePrice, 2, MidpointRounding.AwayFromZero);
            if (line.Discount > lineGross || line.LineTotal != lineGross - line.Discount)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Results.BadRequest(new { error = $"Montant de ligne invalide pour {line.Code}." });
            }

            grossSubtotal += lineGross;
            totalDiscount += line.Discount;
            total += line.LineTotal;
            saleLines.Add(new SaleLineToSave(product, line, lineGross));
        }

        if (request.Subtotal != grossSubtotal || request.Discount != totalDiscount || request.Total != total)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.BadRequest(new { error = "Les montants du ticket ne correspondent pas aux articles et aux prix du catalogue." });
        }
        if (request.AmountReceived < total)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.BadRequest(new { error = "Le montant reçu est insuffisant." });
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "insert into sales(id,cashier_id,payment_method,subtotal,discount,total,amount_received,change_due,idempotency_key,request_hash) values(@saleId,@cashierId,@PaymentMethod,@Subtotal,@Discount,@Total,@AmountReceived,@ChangeDue,@IdempotencyKey,@RequestHash)",
            new
            {
                saleId,
                cashierId,
                request.PaymentMethod,
                Subtotal = grossSubtotal,
                Discount = totalDiscount,
                Total = total,
                request.AmountReceived,
                ChangeDue = request.AmountReceived - total,
                request.IdempotencyKey,
                RequestHash = requestHash
            }, transaction, cancellationToken: cancellationToken));

        foreach (var entry in saleLines)
        {
            var product = entry.Product;
            var line = entry.Request;
            await connection.ExecuteAsync(new CommandDefinition(
                "insert into sale_lines(sale_id,product_id,code,designation,quantity,unit_price,discount,line_total) values(@saleId,@ProductId,@Code,@Designation,@Quantity,@UnitPrice,@Discount,@LineTotal)",
                new
                {
                    saleId,
                    ProductId = product.Id,
                    product.Code,
                    product.Designation,
                    line.Quantity,
                    UnitPrice = product.SalePrice,
                    line.Discount,
                    LineTotal = entry.GrossTotal - line.Discount
                }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "update products set stock_quantity=stock_quantity-@Quantity,updated_at=now() where id=@ProductId",
                new { line.Quantity, ProductId = product.Id }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "insert into stock_movements(product_id,sale_id,movement_type,quantity_out,note,created_by) values(@ProductId,@saleId,'Vente',@Quantity,'Vente au comptoir',@cashierId)",
                new { ProductId = product.Id, saleId, line.Quantity, cashierId }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "insert into audit_events(actor_id,action,entity_type,entity_id,details) values(@cashierId,'SaleCreated','Sale',@saleId,jsonb_build_object('total',@Total))",
            new { cashierId, saleId, Total = total }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        var ticketNumber = await connection.QuerySingleAsync<long>(
            new CommandDefinition("select ticket_number from sales where id=@saleId", new { saleId }, cancellationToken: cancellationToken));
        return Results.Created($"/api/sales/{saleId}", new SaleResponse(saleId, ticketNumber, total));
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        await transaction.RollbackAsync(cancellationToken);
        if (ex.ConstraintName != "sales_idempotency_key_key")
            throw;
        var duplicate = await FindSaleByIdempotencyKeyAsync(connection, request.IdempotencyKey);
        return duplicate is null
            ? Results.Conflict(new { error = "Cette vente ne peut pas être confirmée. Réessayez avec le même ticket." })
            : IdempotentSaleResponse(duplicate, requestHash);
    }
    catch
    {
        await transaction.RollbackAsync(cancellationToken);
        throw;
    }
});

var importLimits = new object[]
{
    new RequestSizeLimitAttribute(MaximumUploadBytes),
    new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes }
};
app.MapPost("/api/admin/products/import/preview", async (
    IFormFile? file, ProductWorkbookImporter importer, CancellationToken cancellationToken) =>
{
    var fileError = ValidateWorkbookFile(file);
    if (fileError is not null)
        return Results.BadRequest(new { error = fileError });

    try
    {
        await using var stream = new MemoryStream();
        await file!.CopyToAsync(stream, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        stream.Position = 0;
        var preview = importer.Read(stream);
        return Results.Ok(new ProductImportPreviewResponse(hash, preview.Items.Count, preview.IsValid, preview.Items, preview.Issues));
    }
    catch (ProductImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { error = $"Lecture du classeur impossible : {ex.Message}" });
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
    {
        return Results.BadRequest(new { error = $"Classeur XLSM invalide : {ex.Message}" });
    }
}).RequireAuthorization("Administrator").DisableAntiforgery().WithMetadata(importLimits);

app.MapPost("/api/admin/products/import/apply", async (
    IFormFile? file, [FromForm] string? previewHash, ProductWorkbookImporter importer, Database db,
    ClaimsPrincipal principal, CancellationToken cancellationToken) =>
{
    var fileError = ValidateWorkbookFile(file);
    if (fileError is not null)
        return Results.BadRequest(new { error = fileError });
    if (!IsSha256(previewHash))
        return Results.BadRequest(new { error = "Le hash de prévisualisation est obligatoire." });
    if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
        return Results.Unauthorized();

    try
    {
        await using var stream = new MemoryStream();
        await file!.CopyToAsync(stream, cancellationToken);
        var fileHash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(previewHash!), Convert.FromHexString(fileHash)))
            return Results.Conflict(new { error = "Le fichier a changé depuis sa prévisualisation. Prévisualisez-le à nouveau avant l’import." });

        stream.Position = 0;
        var preview = importer.Read(stream);
        if (!preview.IsValid)
            return Results.BadRequest(new { error = "Le classeur comporte des erreurs et n’a pas été importé.", preview.Issues });

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var codes = preview.Items.Select(item => item.Code).ToArray();
            var existingProducts = (await connection.QueryAsync<ExistingProduct>(
                new CommandDefinition(
                    "select id, code, stock_quantity as StockQuantity from products where code = any(@Codes) order by code for update",
                    new { Codes = codes }, transaction, cancellationToken: cancellationToken)))
                .ToDictionary(product => product.Code, StringComparer.OrdinalIgnoreCase);
            var inserted = 0;
            var updated = 0;
            var stockAdjustments = 0;

            foreach (var product in preview.Items.OrderBy(item => item.Code, StringComparer.Ordinal))
            {
                Guid productId;
                decimal stockDelta;
                if (existingProducts.TryGetValue(product.Code, out var existing))
                {
                    productId = existing.Id;
                    stockDelta = product.StockQuantity - existing.StockQuantity;
                    await connection.ExecuteAsync(new CommandDefinition(
                        "update products set designation=@Designation,category=@Category,purchase_price=@PurchasePrice,sale_price=@SalePrice,stock_quantity=@StockQuantity,low_stock_threshold=@LowStockThreshold,updated_at=now() where id=@Id",
                        new { product.Designation, product.Category, product.PurchasePrice, product.SalePrice, product.StockQuantity, product.LowStockThreshold, Id = productId },
                        transaction, cancellationToken: cancellationToken));
                    updated++;
                }
                else
                {
                    productId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                        "insert into products(code,designation,category,purchase_price,sale_price,stock_quantity,low_stock_threshold) values(@Code,@Designation,@Category,@PurchasePrice,@SalePrice,@StockQuantity,@LowStockThreshold) returning id",
                        product, transaction, cancellationToken: cancellationToken));
                    stockDelta = product.StockQuantity;
                    inserted++;
                }

                if (stockDelta == 0)
                    continue;
                await connection.ExecuteAsync(new CommandDefinition(
                    "insert into stock_movements(product_id,movement_type,quantity_in,quantity_out,note,created_by) values(@ProductId,'Import',@QuantityIn,@QuantityOut,@Note,@ActorId)",
                    new
                    {
                        ProductId = productId,
                        QuantityIn = Math.Max(stockDelta, 0),
                        QuantityOut = Math.Max(-stockDelta, 0),
                        Note = $"Import XLSM {fileHash[..12]}",
                        ActorId = actorId
                    }, transaction, cancellationToken: cancellationToken));
                stockAdjustments++;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "insert into audit_events(actor_id,action,entity_type,details) values(@ActorId,'ProductsImported','ProductImport',jsonb_build_object('sha256',@Hash,'count',@Count,'inserted',@Inserted,'updated',@Updated,'stock_adjustments',@StockAdjustments))",
                new { ActorId = actorId, Hash = fileHash, Count = preview.Items.Count, Inserted = inserted, Updated = updated, StockAdjustments = stockAdjustments },
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new ProductImportApplyResponse(preview.Items.Count, inserted, updated, stockAdjustments, fileHash));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
    catch (ProductImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { error = $"Lecture du classeur impossible : {ex.Message}" });
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
    {
        return Results.BadRequest(new { error = $"Classeur XLSM invalide : {ex.Message}" });
    }
}).RequireAuthorization("Administrator").DisableAntiforgery().WithMetadata(importLimits);

app.MapGet("/api/audit", async (Database db, CancellationToken cancellationToken) =>
{
    await using var connection = await db.OpenAsync(cancellationToken);
    var events = await connection.QueryAsync(new CommandDefinition(
        "select id, action, entity_type as EntityType, entity_id as EntityId, details, created_at as CreatedAt from audit_events order by created_at desc limit 500",
        cancellationToken: cancellationToken));
    return Results.Ok(events);
}).RequireAuthorization("Administrator");

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services, IConfiguration configuration, string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("alter table sales add column if not exists request_hash varchar(64)");
    var password = configuration["Seed:AdminPassword"];
    if (string.IsNullOrWhiteSpace(password))
        return;

    var hasher = services.GetRequiredService<PasswordHasher>();
    await connection.ExecuteAsync(
        "insert into app_users(username,display_name,role,password_hash) values('admin','Administrateur','Administrateur',@PasswordHash) on conflict (username) do nothing",
        new { PasswordHash = hasher.Hash(password) });
}

static string? ValidateSaleRequest(CreateSaleRequest request)
{
    const decimal maximumPrice = 9_999_999_999_999_999.99m;
    const decimal maximumQuantity = 999_999_999_999_999.999m;
    if (request.IdempotencyKey == Guid.Empty)
        return "La clé d’idempotence de la vente est obligatoire.";
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
        return "Le ticket doit contenir de 1 à 500 lignes.";
    if (request.PaymentMethod is not ("Espèces" or "Carte" or "Chèque"))
        return "Mode de paiement invalide.";
    if (request.Subtotal < 0 || request.Subtotal > maximumPrice
        || request.Discount < 0 || request.Discount > maximumPrice
        || request.Total < 0 || request.Total > maximumPrice
        || request.AmountReceived < 0 || request.AmountReceived > maximumPrice)
        return "Les montants ne peuvent pas être négatifs.";
    if (HasMoreThanDecimals(request.Subtotal, 2) || HasMoreThanDecimals(request.Discount, 2)
        || HasMoreThanDecimals(request.Total, 2) || HasMoreThanDecimals(request.AmountReceived, 2))
        return "Les montants doivent être arrondis à deux décimales.";

    foreach (var line in request.Lines)
    {
        if (string.IsNullOrWhiteSpace(line.Code) || line.Code.Length > 80)
            return "Chaque ligne doit contenir un code article valide.";
        if (line.Quantity <= 0 || line.Quantity > maximumQuantity || HasMoreThanDecimals(line.Quantity, 3))
            return $"La quantité de {line.Code} doit être positive avec au plus trois décimales.";
        if (line.UnitPrice < 0 || line.UnitPrice > maximumPrice || HasMoreThanDecimals(line.UnitPrice, 2)
            || line.Discount < 0 || line.Discount > maximumPrice || HasMoreThanDecimals(line.Discount, 2)
            || line.LineTotal < 0 || line.LineTotal > maximumPrice || HasMoreThanDecimals(line.LineTotal, 2))
            return $"Les montants de {line.Code} sont invalides.";
    }
    if (request.Lines.Select(line => line.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Lines.Count)
        return "Chaque article ne peut apparaître qu’une seule fois dans le ticket.";
    return null;
}

static bool HasMoreThanDecimals(decimal value, int decimals) => value != decimal.Round(value, decimals);

static string ComputeSaleRequestHash(CreateSaleRequest request)
{
    var canonicalRequest = JsonSerializer.SerializeToUtf8Bytes(new
    {
        request.PaymentMethod,
        request.Subtotal,
        request.Discount,
        request.Total,
        request.AmountReceived,
        request.Lines
    });
    return Convert.ToHexString(SHA256.HashData(canonicalRequest));
}

static async Task<ExistingSale?> FindSaleByIdempotencyKeyAsync(NpgsqlConnection connection, Guid idempotencyKey)
{
    return await connection.QuerySingleOrDefaultAsync<ExistingSale>(
        "select id, ticket_number as TicketNumber, total, request_hash as RequestHash from sales where idempotency_key=@IdempotencyKey",
        new { IdempotencyKey = idempotencyKey });
}

static IResult IdempotentSaleResponse(ExistingSale sale, string requestHash)
{
    if (!string.Equals(sale.RequestHash, requestHash, StringComparison.Ordinal))
        return Results.Conflict(new { error = "Cette clé d’idempotence a déjà été utilisée pour une autre vente." });
    return Results.Ok(new SaleResponse(sale.Id, sale.TicketNumber, sale.Total));
}

static string? ValidateWorkbookFile(IFormFile? file)
{
    if (file is null || file.Length == 0)
        return "Sélectionnez un classeur XLSM non vide.";
    if (!string.Equals(Path.GetExtension(file.FileName), ".xlsm", StringComparison.OrdinalIgnoreCase))
        return "Seuls les classeurs XLSM sont acceptés.";
    if (file.Length > MaximumUploadBytes)
        return $"Le classeur dépasse la limite de {MaximumUploadBytes / (1024 * 1024)} Mo.";
    return null;
}

static bool IsSha256(string? value)
{
    if (value is null || value.Length != 64)
        return false;
    try
    {
        return Convert.FromHexString(value).Length == 32;
    }
    catch (FormatException)
    {
        return false;
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed class SaleLineRequest
{
    public string Code { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Discount { get; init; }
    public decimal LineTotal { get; init; }
}
public sealed class CreateSaleRequest
{
    public string PaymentMethod { get; init; } = string.Empty;
    public decimal Subtotal { get; init; }
    public decimal Discount { get; init; }
    public decimal Total { get; init; }
    public decimal AmountReceived { get; init; }
    public Guid IdempotencyKey { get; init; }
    public List<SaleLineRequest> Lines { get; init; } = [];
}
public sealed record Product(Guid Id, string Code, string Designation, string? Category, decimal PurchasePrice, decimal SalePrice, decimal StockQuantity, decimal LowStockThreshold);
public sealed record UserRecord(Guid Id, string Username, string DisplayName, string Role, string PasswordHash);
public sealed record SaleResponse(Guid Id, long TicketNumber, decimal Total);
public sealed record ProductImportPreviewResponse(string Sha256, int Count, bool IsValid, IReadOnlyList<ProductImportRow> Items, IReadOnlyList<ProductImportIssue> Issues);
public sealed record ProductImportApplyResponse(int Count, int Inserted, int Updated, int StockAdjustments, string Sha256);
public sealed record ExistingProduct(Guid Id, string Code, decimal StockQuantity);
public sealed record ExistingSale(Guid Id, long TicketNumber, decimal Total, string? RequestHash);
public sealed record SaleLineToSave(Product Product, SaleLineRequest Request, decimal GrossTotal);

public sealed class Database(string connectionString)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha512"
                || !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 1_000_000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length is < 16 or > 64 || expected.Length != 32)
                return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
