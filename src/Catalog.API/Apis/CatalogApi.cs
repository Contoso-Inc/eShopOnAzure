﻿using Microsoft.AspNetCore.Http.HttpResults;
using Pgvector.EntityFrameworkCore;

namespace eShop.Catalog.API;

public static class CatalogApi
{
    public static IEndpointRouteBuilder MapCatalogApi(this IEndpointRouteBuilder app)
    {
        // Routes for querying catalog items.
        app.MapGet("/items", GetAllCatalogItems);
        app.MapGet("/items/by", GetItemsByIds);
        app.MapGet("/items/{id:int}", GetItemById);
        app.MapGet("/items/by/{name:minlength(1)}", GetItemsByName);
        app.MapGet("/items/{catalogItemId:int}/pic", GetItemPictureById);

        // Routes for resolving catalog items using AI.
        app.MapGet("/items/withsemanticrelevance/{text:minlength(1)}", GetItemsBySemanticRelevance);

        // Routes for resolving catalog items by type and brand.
        app.MapGet("/items/type/{typeId}/brand/{brandId?}", GetItemsByBrandAndTypeId);
        app.MapGet("/items/type/all/brand/{brandId:int?}", GetItemsByBrandId);
        app.MapGet("/catalogtypes", async (CatalogContext context) => await context.CatalogTypes.OrderBy(x => x.Type).ToListAsync());
        app.MapGet("/catalogbrands", async (CatalogContext context) => await context.CatalogBrands.OrderBy(x => x.Brand).ToListAsync());

        // Routes for modifying catalog items.
        app.MapPut("/items", UpdateItem);
        app.MapPost("/items", CreateItem);
        app.MapDelete("/items/{id:int}", DeleteItemById);

        return app;
    }


    public static Results<Ok<PaginatedItems<CatalogItem>>, BadRequest<string>> GetAllItems(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var totalItems = services.Context.CatalogItems
            .LongCount();

        var itemsOnPage = services.Context.CatalogItems
                .OrderBy(c => c.Name)
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToList();

        return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
    }


    public static Ok<List<CatalogItem>> GetItemsByIds(
        [AsParameters] CatalogServices services,
        int[] ids)
    {
        var items = services.Context.CatalogItems.Where(item => ids.Contains(item.Id)).ToList();
        return TypedResults.Ok(items);
    }

    public static Task<Results<Ok<CatalogItem>, NotFound, BadRequest<string>>> GetItemById(
        [AsParameters] CatalogServices services,
        int id)
    {
        if (id <= 0)
        {
            return Task.FromResult<Results<Ok<CatalogItem>, NotFound, BadRequest<string>>>(TypedResults.BadRequest("Id is not valid."));
        }

        var items = services.Context.CatalogItems.Include(ci => ci.CatalogBrand).ToList();

        var item = items.SingleOrDefault(ci => ci.Id == id);

        if (item == null)
        {
            return Task.FromResult<Results<Ok<CatalogItem>, NotFound, BadRequest<string>>>(TypedResults.NotFound());
        }

        return Task.FromResult<Results<Ok<CatalogItem>, NotFound, BadRequest<string>>>(TypedResults.Ok(item));
    }

    public static Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByName(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        string name)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var allItems = services.Context.CatalogItems
            .Where(c => c.Name.StartsWith(name))
            .ToList();

        var totalItems = allItems.Count;

        var itemsOnPage = allItems
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage)));
    }

    public static Task<Results<NotFound, PhysicalFileHttpResult>> GetItemPictureById(CatalogContext context, IWebHostEnvironment environment, int catalogItemId)
    {
        var allItems = context.CatalogItems.ToList();
        var item = allItems.FirstOrDefault(i => i.Id == catalogItemId);

        if (item is null)
        {
            return Task.FromResult<Results<NotFound, PhysicalFileHttpResult>>(TypedResults.NotFound());
        }

        var path = GetFullPath(environment.ContentRootPath, item.PictureFileName);

        string imageFileExtension = Path.GetExtension(item.PictureFileName);
        string mimetype = GetImageMimeTypeFromImageFileExtension(imageFileExtension);
        DateTime lastModified = File.GetLastWriteTimeUtc(path);

        return Task.FromResult<Results<NotFound, PhysicalFileHttpResult>>(TypedResults.PhysicalFile(path, mimetype, lastModified: lastModified));
    }

    public static async Task<Results<BadRequest<string>, RedirectToRouteHttpResult, Ok<PaginatedItems<CatalogItem>>>> GetItemsBySemanticRelevance(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        string text)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        if (!services.CatalogAI.IsEnabled)
        {
            return await GetItemsByName(paginationRequest, services, text);
        }

        // Create an embedding for the input search
        var vector = await services.CatalogAI.GetEmbeddingAsync(text);

        // Get the total number of items
        var totalItems = await services.Context.CatalogItems
            .LongCountAsync();

        // Get the next page of items, ordered by most similar (smallest distance) to the input search
        List<CatalogItem> itemsOnPage;
        if (services.Logger.IsEnabled(LogLevel.Debug))
        {
            var itemsWithDistance = await services.Context.CatalogItems
                .Select(c => new { Item = c, Distance = c.Embedding.CosineDistance(vector) })
                .OrderBy(c => c.Distance)
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToListAsync();

            services.Logger.LogDebug("Results from {text}: {results}", text, string.Join(", ", itemsWithDistance.Select(i => $"{i.Item.Name} => {i.Distance}")));

            itemsOnPage = itemsWithDistance.Select(i => i.Item).ToList();
        }
        else
        {
            itemsOnPage = await services.Context.CatalogItems
                .OrderBy(c => c.Embedding.CosineDistance(vector))
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToListAsync();
        }

        return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
    }

    public static Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByBrandAndTypeId(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        int typeId,
        int? brandId)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var root = (IQueryable<CatalogItem>)services.Context.CatalogItems;
        root = root.Where(c => c.CatalogTypeId == typeId);
        if (brandId is not null)
        {
            root = root.Where(c => c.CatalogBrandId == brandId);
        }

        var allItems = root.ToList();

        var totalItems = allItems.Count;

        var itemsOnPage = allItems
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage)));
    }

    public static Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByBrandId(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        int? brandId)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var root = (IQueryable<CatalogItem>)services.Context.CatalogItems;

        if (brandId is not null)
        {
            root = root.Where(ci => ci.CatalogBrandId == brandId);
        }

        var allItems = root.ToList();

        var totalItems = allItems.Count;

        var itemsOnPage = allItems
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage)));
    }

    public static async Task<Results<Created, NotFound<string>>> UpdateItem(
        [AsParameters] CatalogServices services,
        CatalogItem productToUpdate)
    {
        var catalogItem = await services.Context.CatalogItems.SingleOrDefaultAsync(i => i.Id == productToUpdate.Id);

        if (catalogItem == null)
        {
            return TypedResults.NotFound($"Item with id {productToUpdate.Id} not found.");
        }

        // Update current product
        var catalogEntry = services.Context.Entry(catalogItem);
        catalogEntry.CurrentValues.SetValues(productToUpdate);

        catalogItem.Embedding = await services.CatalogAI.GetEmbeddingAsync(catalogItem);

        var priceEntry = catalogEntry.Property(i => i.Price);

        if (priceEntry.IsModified) // Save product's data and publish integration event through the Event Bus if price has changed
        {
            //Create Integration Event to be published through the Event Bus
            var priceChangedEvent = new ProductPriceChangedIntegrationEvent(catalogItem.Id, productToUpdate.Price, priceEntry.OriginalValue);

            // Achieving atomicity between original Catalog database operation and the IntegrationEventLog thanks to a local transaction
            await services.EventService.SaveEventAndCatalogContextChangesAsync(priceChangedEvent);

            // Publish through the Event Bus and mark the saved event as published
            await services.EventService.PublishThroughEventBusAsync(priceChangedEvent);
        }
        else // Just save the updated product because the Product's Price hasn't changed.
        {
            await services.Context.SaveChangesAsync();
        }
        return TypedResults.Created($"/api/v1/catalog/items/{productToUpdate.Id}");
    }

    public static async Task<Created> CreateItem(
        [AsParameters] CatalogServices services,
        CatalogItem product)
    {
        var item = new CatalogItem
        {
            Id = product.Id,
            CatalogBrandId = product.CatalogBrandId,
            CatalogTypeId = product.CatalogTypeId,
            Description = product.Description,
            Name = product.Name,
            PictureFileName = product.PictureFileName,
            Price = product.Price,
            AvailableStock = product.AvailableStock,
            RestockThreshold = product.RestockThreshold,
            MaxStockThreshold = product.MaxStockThreshold
        };
        item.Embedding = await services.CatalogAI.GetEmbeddingAsync(item);

        services.Context.CatalogItems.Add(item);
        await services.Context.SaveChangesAsync();

        return TypedResults.Created($"/api/v1/catalog/items/{item.Id}");
    }

    public static async Task<Results<NoContent, NotFound>> DeleteItemById(
        [AsParameters] CatalogServices services,
        int id)
    {
        var item = services.Context.CatalogItems.SingleOrDefault(x => x.Id == id);

        if (item is null)
        {
            return TypedResults.NotFound();
        }

        services.Context.CatalogItems.Remove(item);
        await services.Context.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    //Deliberately introduce high traffic to simulate a high load scenario. Remove for production.
    public static Results<Ok<PaginatedItems<CatalogItem>>, BadRequest<string>> GetAllCatalogItems(
       [AsParameters] PaginationRequest paginationRequest,
       [AsParameters] CatalogServices services)
    {
        
        // JANK: Just to simulate a slow query
        for (int i = 0; i < 500; i++)
        {
            GetAllItems(paginationRequest, services);
        }
       return GetAllItems(paginationRequest, services);
    }
    private static string GetImageMimeTypeFromImageFileExtension(string extension) => extension switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".tiff" => "image/tiff",
        ".wmf" => "image/wmf",
        ".jp2" => "image/jp2",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    public static string GetFullPath(string contentRootPath, string pictureFileName) =>
        Path.Combine(contentRootPath, "Pics", pictureFileName);
}
