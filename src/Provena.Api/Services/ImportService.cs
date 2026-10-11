using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;

namespace Provena.Api.Services;

public sealed class ImportService(
    AppDbContext db,
    CatalogService catalog,
    ShipmentService shipments,
    FileValidationService files
)
{
    public async Task<ImportResult> Import(string kind, IFormFile file, CancellationToken ct)
    {
        if (files.Validate(file) != ".csv")
            throw new ApiException(400, "A CSV file is required.");
        string[] expected = kind switch
        {
            "suppliers" => ["externalReference", "name", "email", "contactPerson", "countryCode"],
            "products" => ["externalReference", "name", "cnCode", "commodityType", "description"],
            "shipments" =>
            [
                "referenceNumber",
                "supplierExternalReference",
                "productExternalReference",
                "quantity",
                "unit",
                "originCountryCode",
            ],
            _ => throw ApiException.NotFound(),
        };
        var rows = new List<string[]>();
        using var reader = new StreamReader(file.OpenReadStream());
        using var csv = new CsvReader(
            reader,
            new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim,
            }
        );
        try
        {
            if (!await csv.ReadAsync())
                throw new ApiException(400, "CSV is empty.");
            csv.ReadHeader();
            if (!(csv.HeaderRecord ?? []).SequenceEqual(expected))
                throw new ApiException(400, $"Expected CSV columns: {string.Join(',', expected)}");
            while (await csv.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 10000)
                    throw new ApiException(400, "Maximum 10,000 data rows per import.");
                rows.Add((csv.Parser.Record ?? []).ToArray());
            }
        }
        catch (CsvHelperException)
        {
            throw new ApiException(
                400,
                "Malformed CSV. Use the documented UTF-8 comma-separated format."
            );
        }
        var errors = new List<ImportError>();
        var imported = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            try
            {
                if (row.Length != expected.Length)
                    throw new ApiException(400, "Unexpected number of columns.");
                string? Value(int index) =>
                    string.IsNullOrWhiteSpace(row[index]) ? null : row[index];
                switch (kind)
                {
                    case "suppliers":
                        await catalog.SaveSupplier(
                            null,
                            new(row[1], Value(0), Value(2), Value(3), Value(4), null),
                            ct
                        );
                        break;
                    case "products":
                        if (
                            !Enum.TryParse<CommodityType>(row[3], false, out var commodity)
                            || !Enum.IsDefined(commodity)
                        )
                            throw new ApiException(400, "Invalid commodityType.");
                        await catalog.SaveProduct(
                            null,
                            new(row[1], Value(0), row[2], commodity, Value(4)),
                            ct
                        );
                        break;
                    case "shipments":
                        if (
                            !decimal.TryParse(
                                row[3],
                                NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture,
                                out var quantity
                            )
                            || !Enum.TryParse<QuantityUnit>(row[4], false, out var unit)
                            || !Enum.IsDefined(unit)
                        )
                            throw new ApiException(400, "Invalid quantity or unit.");
                        var supplier =
                            await db
                                .Suppliers.Where(x => x.ExternalReference == row[1])
                                .Select(x => (Guid?)x.Id)
                                .SingleOrDefaultAsync(ct)
                            ?? throw new ApiException(
                                400,
                                "Supplier external reference not found."
                            );
                        var product =
                            await db
                                .Products.Where(x => x.ExternalReference == row[2])
                                .Select(x => (Guid?)x.Id)
                                .SingleOrDefaultAsync(ct)
                            ?? throw new ApiException(400, "Product external reference not found.");
                        await shipments.Save(
                            null,
                            new(
                                row[0],
                                supplier,
                                product,
                                quantity,
                                unit,
                                Value(5),
                                null,
                                null,
                                null
                            ),
                            ct
                        );
                        break;
                }
                imported++;
            }
            catch (Exception e)
                when (e is ApiException or FluentValidation.ValidationException or DbUpdateException
                )
            {
                errors.Add(
                    new(
                        i + 2,
                        e is DbUpdateException ? "Duplicate or conflicting record." : e.Message
                    )
                );
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return new(rows.Count, imported, errors.Count, errors);
    }
}
