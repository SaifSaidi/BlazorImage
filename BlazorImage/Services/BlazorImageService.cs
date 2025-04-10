﻿using BlazorImage.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

using System.Threading.Channels;

namespace BlazorImage.Services;

internal class BlazorImageService : IBlazorImageService
{
    private readonly ICacheService _cacheService;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IImageElementService _imageElementService;
    private readonly BlazorImageConfig _config;
    private readonly ILogger<BlazorImageService> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly ConcurrentDictionary<string, int> _requestCounts = new();
    private readonly IFileService _fileService;
    public BlazorImageService(
        ICacheService cacheService,
        IImageProcessingService imageProcessingService,
        IOptions<BlazorImageConfig> config,
        ILogger<BlazorImageService> logger,
        IImageElementService imageElementService,
        IFileService fileService)
    {
        _cacheService = cacheService;
        _imageProcessingService = imageProcessingService;
        _config = config.Value;
        _logger = logger;
        _imageElementService = imageElementService;
        _fileService = fileService;
    }

    // Generates an image processing output directory
    private string GetImageProccessOutputDir(string fileName)
    {
        var path = _fileService.GetRootPath(Path.Combine(_config.Dir, fileName));
        return path;
    }
    public async ValueTask<Result<ImageInfo>?> GetImageInfoAsync(string src, int? quality, FileFormat? format)
    {

        if (string.IsNullOrWhiteSpace(src))
        {
            return Result<ImageInfo>
                .Failure("Source path cannot be empty.");

        }

        quality ??= _config.DefaultQuality; 
        format ??= _config.DefaultFileFormat;

        var cacheKey = GenerateCacheKey(src, quality, format);
         
        var cachedInfo = await _cacheService.GetFromCacheAsync(cacheKey);


        if (cachedInfo != null)
        {
            return Result<ImageInfo>
                .Success(cachedInfo); // Cache hit, return immediately.
        }

        if (!_fileService.FileExistsInRootPath(src))
        {
            return Result<ImageInfo>
                .Failure("Source path not exists.");

        }

        if (quality < 15 || quality > 100)
        {
            return Result<ImageInfo>
            .Failure("Image Quality must be greater than 15, and less than 100.");
        }

        return null;
    }

    public async ValueTask<Result<ImageInfo>?> ProcessAndGetImageInfoAsync(string src, int? quality, FileFormat? format, ChannelWriter<string> writer)
    {

        quality ??= _config.DefaultQuality;
        format ??= _config.DefaultFileFormat;

        var cacheKey = GenerateCacheKey(src, quality, format);


        Result<ImageInfo>? info = null;

        _ = Task.Run(async () =>
        {
            await writer.WriteAsync("0% Starting optimization...");
            info = await OptimizeAndCacheImage(src, quality.Value, format.Value, cacheKey, writer);
            await writer.WriteAsync("100% Optimization completed.");
            writer.Complete();

        });
        return await Task.FromResult(info);
    }

 
    private static string GenerateCacheKey(string src, int? quality, FileFormat? format)
    {
         static ReadOnlySpan<char> GetFormatSpan(FileFormat? format) =>
            format.HasValue ? FileFormatExtensions.FormatStrings[(int)format.Value].AsSpan() : default;

         Span<char> buffer = stackalloc char[256];
        int pos = 0;

        // Append source
        if (!string.IsNullOrEmpty(src))
        {
            src.AsSpan().CopyTo(buffer.Slice(pos));
            pos += src.Length;
        }

        // Append format
        buffer[pos++] = '-';
        var formatSpan = GetFormatSpan(format);
        if (!formatSpan.IsEmpty)
        {
            formatSpan.CopyTo(buffer.Slice(pos));
            pos += formatSpan.Length;
        }

        // Append quality
        buffer[pos++] = '-';
        if (quality.HasValue)
        {
            Span<char> numBuffer = stackalloc char[4];
            if (!quality.Value.TryFormat(numBuffer, out int written))
                throw new ArgumentException("Invalid quality value");

            numBuffer.Slice(0, written).CopyTo(buffer.Slice(pos));
            pos += written;
        }

        return new string(buffer.Slice(0, pos));
    }

     

    private async Task<Result<ImageInfo>> OptimizeAndCacheImage(string src, int quality, FileFormat format, string cacheKey, ChannelWriter<string> writer)
    {
        var lockKey = $"image_processing_{cacheKey}";
        var processingLock = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await processingLock.WaitAsync().ConfigureAwait(false);

        try
        {

            await writer.WriteAsync("25% Request processing...");

            var originalPath = _fileService.GetRootPath(src);

            if (!File.Exists(originalPath))
            {
                return Result<ImageInfo>.Failure($"File not found: {originalPath}");
            }

            if (IsImageBeingProcessed(cacheKey))
            {
                return Result<ImageInfo>.Failure("Request limit exceeded for this image.");
            }

            MarkImageAsProcessing(cacheKey);

            await writer.WriteAsync("50% Generating image sizes...");
            var tasks = ProcessImages(src, quality, format, writer);
            await Task.WhenAll(tasks); // Process all sizes in parallel

            await writer.WriteAsync("90% Generating placeholder and fallback images...");
            await Task.WhenAll(
                GeneratePlaceholderImage(originalPath, src, format),
                GenerateFallbackImages(originalPath, src, quality, format)
            );

            await writer.WriteAsync("95% Almost done...");
            var processedImageInfo = CreateImageInfo(src, quality, format);
            await writer.WriteAsync("99% Finalizing...");

            await _cacheService.SaveToCacheAsync(cacheKey, processedImageInfo);

            return Result<ImageInfo>.Success(processedImageInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing and getting image info for {Src}", src);
            return Result<ImageInfo>.Failure("An error occurred while processing the image.");
        }
        finally
        {
            processingLock.Release();
            _locks.TryRemove(lockKey, out _);
            DecrementRequestCount(cacheKey);
        }
    }

    private bool IsImageBeingProcessed(string cacheKey) =>
        _requestCounts.TryGetValue(cacheKey, out var count) && count > 0;

    private void MarkImageAsProcessing(string cacheKey) =>
        _requestCounts[cacheKey] = 1; // Mark that the image is being processed.

    private void DecrementRequestCount(string cacheKey) =>
        _requestCounts.AddOrUpdate(cacheKey, 0, (_, currentCount) => currentCount - 1); // Release the request count

    private IEnumerable<Task> ProcessImages(string src, int quality, FileFormat format, ChannelWriter<string> writer)
    {
        var sizes = _config.ConfigSizes;
        var originalPath = _fileService.GetRootPath(src);
        var i = 1;
        return sizes.Select(size =>
        {
            return Task.Run(async () =>
            {
                var width = size;
                var height = HelpersMethods.ToAspectRatio(width, _config.AspectWidth, _config.AspectHeigth);
                var imageName = _imageElementService.GenerateImageName(src, width, quality, format);
                var outputFilePath = GetImageProccessOutputDir(imageName);
                await writer.WriteAsync($"Generating image {size}w size... {i} of {sizes.Length}");
                i = i + 1;

                if (_fileService.FileExistsInRootPath(outputFilePath))
                {
                    await Task.CompletedTask;
                }
                else
                {
                    await _imageProcessingService.ProcessAndSaveImageAsync(originalPath, outputFilePath, width, height, quality, format);
                }
            });
        });
    }

    private async Task GeneratePlaceholderImage(string originalPath, string src, FileFormat format)
    {

        var placeholderWidth = Constants.PlaceholderWidth;
        var placeholderImageName = _imageElementService.GenerateImagePlaceholder(src, format);
        var placeholderOutputFile = GetImageProccessOutputDir(placeholderImageName);
        var placeholderHeight = HelpersMethods.ToAspectRatio(placeholderWidth, _config.AspectWidth, _config.AspectHeigth);
        if (_fileService.FileExistsInRootPath(placeholderOutputFile))
        {
            return;
        }
        // Ensure the directory exists before processing the images
        _fileService.CreateDirectoryForFile(placeholderOutputFile); 
        await _imageProcessingService.ProcessAndSaveImageAsync(originalPath, placeholderOutputFile, placeholderWidth, placeholderHeight, Constants.PlaceholderQuality, format);
    }

    private async Task GenerateFallbackImages(string originalPath, string src, int quality, FileFormat format)
    {
        var fallbackWidth = _config.ConfigSizes.Last(); // Default fallback width
        var fallbackHeight = HelpersMethods.ToAspectRatio(fallbackWidth, _config.AspectWidth, _config.AspectHeigth);

        var jpegFallbackOutputFile = _fileService.GetRootPath(Path.Combine(_config.Dir, _imageElementService.GenerateImageFallbackSrc(src, FileFormat.jpeg)));
        var formatFallbackOutputFile = _fileService.GetRootPath(Path.Combine(_config.Dir, _imageElementService.GenerateImageFallbackSrc(src, format)));

        if (_fileService.FileExistsInRootPath(jpegFallbackOutputFile) && _fileService.FileExistsInRootPath(formatFallbackOutputFile))
        {
            return;
        }

        // Ensure the directory exists before processing the images
        _fileService.CreateDirectoryForFile(jpegFallbackOutputFile);
        _fileService.CreateDirectoryForFile(formatFallbackOutputFile);

        await Task.WhenAll(
            _imageProcessingService.ProcessAndSaveImageAsync(originalPath, jpegFallbackOutputFile, fallbackWidth, fallbackHeight, quality, FileFormat.jpeg),
            _imageProcessingService.ProcessAndSaveImageAsync(originalPath, formatFallbackOutputFile, fallbackWidth, fallbackHeight, quality, format)
        );
    }

    private ImageInfo CreateImageInfo(string src, int quality, FileFormat format)
    {
        var sanitizedName = _fileService.SanitizeFileName(src);
        var width = _config.ConfigSizes.Last();
        var height = HelpersMethods.ToAspectRatio(width, _config.AspectWidth, _config.AspectHeigth);


        return new ImageInfo( sanitizedName, width, height, format, quality) {
            Key= $"{src}-{format}-{quality}"
        };
    }
}
