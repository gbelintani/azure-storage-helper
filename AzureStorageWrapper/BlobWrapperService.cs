﻿using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageWrapper.Configurations;
using AzureStorageWrapper.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorageWrapper;

public class BlobWrapperService : IBlobWrapperService
{
    private readonly ILogger<BlobWrapperService> _logger;
    private readonly AzureStorageWrapperOptions _options;

    public BlobWrapperService(ILogger<BlobWrapperService> logger, IOptions<AzureStorageWrapperOptions> options)
    {
        _logger = logger;
        if (!options.Value.IsValid())
            throw new ArgumentException("No ConnectionString defined for AzureStorageWrapper");
        _options = options.Value;
    }

    public Task<string> Upload(IBlob blobInfo)
    {
        return blobInfo.Type switch
        {
            WrapperBlobType.File => Upload(blobInfo as FileBlobBase),
            WrapperBlobType.Image => Upload(blobInfo as ImageBlobBase),
            _ => throw new ArgumentException("Blob type not supported")
        };
    }

    public async Task<bool> DeleteByUrl(WrapperBlobType type, string blobUrl)
    {
        var path = $"{_options.GetContainer(type)}/";
        var blobName = blobUrl.Substring(blobUrl.IndexOf(path, StringComparison.Ordinal) + path.Length);

        return await Delete(type, blobName);
    }

    public async Task<bool> Delete(WrapperBlobType type, string blobname)
    {
        _logger.LogDebug($"{blobname} Blob will be deleted");
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(type));
        var blob = await container.DeleteBlobIfExistsAsync(blobname);
        return blob.Value;
    }

    public async Task<IEnumerable<BlobResponse>> GetAll(WrapperBlobType type, string? prefix = null)
    {
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(type));
        var blobs = new List<BlobResponse>();
        await foreach (var blobItem in container.GetBlobsAsync(prefix: prefix))
            blobs.Add(new BlobResponse(blobItem.Name, blobItem.Properties.LastModified));

        return blobs;
    }

    public async Task<IEnumerable<BlobResponse>> GetByTag(WrapperBlobType type, KeyValuePair<string, string> tagKv)
    {
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(type));
        var blobs = new List<BlobResponse>();
        var blobsByTag = container.FindBlobsByTagsAsync($"{tagKv.Key}='{tagKv.Value}'");
        await foreach (var blobItem in blobsByTag) blobs.Add(new BlobResponse(blobItem.BlobName, null));

        return blobs;
    }

    public async Task<Stream?> GetFileStream(WrapperBlobType type, string blobName)
    {
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(type));
        var fileStream = new MemoryStream();
        var result = await container.GetBlobClient(blobName).DownloadToAsync(fileStream);
        if (result.IsError) return null;

        return fileStream;
    }

    private Task<string> Upload(FileBlobBase blobInfo)
    {
        using var memoryStream = new MemoryStream();
        blobInfo.Stream.CopyTo(memoryStream);
        return UploadToBlobContainer(blobInfo, memoryStream.ToArray());
    }

    private async Task<string> Upload(ImageBlobBase blobInfo)
    {
        byte[] finalImage;
        if (blobInfo.ImageStrategy != null)
        {
            _logger.LogDebug($"Image will be processed and saved for {blobInfo.Name}");
            finalImage = await ImageProcessor.ProcessAsync(blobInfo);
            _logger.LogDebug($"Image processed for {blobInfo.Name}");
        }
        else
        {
            var streamEnd = Convert.ToInt32(blobInfo.Stream.Length);
            var buffer = new byte[streamEnd];
            await blobInfo.Stream.ReadAsync(buffer, 0, streamEnd);
            finalImage = buffer;
        }

        return await UploadToBlobContainer(blobInfo, finalImage);
    }

    private async Task<string> UploadToBlobContainer(IBlob blobInfo, byte[] stream)
    {
        _logger.LogDebug($"Uploading blob {blobInfo.Name} to container {_options.GetContainer(blobInfo.Type)}");
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(blobInfo.Type));

        var blob = container.GetBlobClient(blobInfo.GetFullBlobName());
        var blobHttpHeader = new BlobHttpHeaders
        {
            ContentType = GetContentType(blobInfo)
        };

        try
        {
            using var ms = new MemoryStream(stream);
            await blob.UploadAsync(ms, blobHttpHeader);
            ms.Close();
            if (blobInfo.Metadata.Any()) await blob.SetMetadataAsync(blobInfo.Metadata);

            if (blobInfo.Tags.Any()) await blob.SetTagsAsync(blobInfo.Tags);
            _logger.LogDebug($"Blob saved for {blobInfo.Name} to {blob.Uri.AbsoluteUri}");
            return blob.Uri.AbsoluteUri;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error saving blob for {blobInfo.Name}", ex);
            throw;
        }
    }

    public async Task<bool> Delete(WrapperBlobType type, KeyValuePair<string, string> tagKv)
    {
        _logger.LogDebug($"Blob with tag {tagKv.Value} will be deleted");
        var client = new BlobServiceClient(_options.ConnectionString);
        var container = client.GetBlobContainerClient(_options.GetContainer(type));
        var blobs = container.FindBlobsByTagsAsync($"{tagKv.Key}='{tagKv.Value}'");
        await foreach (var blob in blobs)
        {
            _logger.LogDebug($"Deleting blob {blob.BlobName}");
            var blobResult = await container.DeleteBlobIfExistsAsync(blob.BlobName);
        }

        return true;
    }

    private static string GetContentType(IBlob blob)
    {
        return blob.GetFullBlobName()
                .Substring(blob.GetFullBlobName().LastIndexOf(".", StringComparison.Ordinal)) switch
            {
                ".html" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" => "image/jpg",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                _ => blob.ContentType
            };
    }
}