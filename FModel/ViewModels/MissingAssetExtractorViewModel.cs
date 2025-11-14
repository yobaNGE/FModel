using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;

namespace FModel.ViewModels;

public class MissingAssetExtractorViewModel : ViewModel
{
    private const string DefaultStatusMessage = "Paste asset property JSON paths (one per line) or log entries that contain missing assets, then choose Extract.";
    private static readonly Regex MissingAssetRegex = new("Missing asset:\\s*(?<path>.+?)(?:\\s*\\(|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ApplicationViewModel _applicationView = ApplicationService.ApplicationView;
    private readonly ThreadWorkerViewModel _threadWorkerView = ApplicationService.ThreadWorkerView;

    private string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set => SetProperty(ref _isProcessing, value);
    }

    private string _statusMessage = DefaultStatusMessage;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<MissingAssetExtractionResult> Results { get; } = new();

    public void Reset()
    {
        InputText = string.Empty;
        Results.Clear();
        StatusMessage = DefaultStatusMessage;
    }

    public async Task ExtractAsync()
    {
        if (IsProcessing)
        {
            return;
        }

        Results.Clear();

        var parseFailures = new List<MissingAssetExtractionResult>();
        var requests = ParseRequests(InputText, parseFailures);

        foreach (var failure in parseFailures)
        {
            Results.Add(failure);
        }

        if (requests.Count == 0)
        {
            StatusMessage = parseFailures.Count > 0
                ? "No valid asset paths were found in the provided text."
                : "No missing asset entries were detected.";
            return;
        }

        IsProcessing = true;
        StatusMessage = "Exporting properties for the detected assets...";

        var extractionResults = new List<MissingAssetExtractionResult>();
        try
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryResolveGameFile(request.AssetPath, out var gameFile, out var error))
                    {
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, request.AssetPath, false, error));
                        continue;
                    }

                    try
                    {
                        _applicationView.CUE4Parse.Extract(cancellationToken, gameFile, _applicationView.CUE4Parse.TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto);
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, request.AssetPath, true, "Exported properties (.json)."));
                    }
                    catch (Exception e)
                    {
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, request.AssetPath, false, e.Message));
                    }
                }
            });
        }
        finally
        {
            IsProcessing = false;
        }

        foreach (var result in extractionResults)
        {
            Results.Add(result);
        }

        var requestedCount = requests.Count;
        var successCount = extractionResults.Count(r => r.Success);
        var failureCount = Results.Count(r => !r.Success);

        StatusMessage = failureCount == 0
            ? $"Completed. Exported {successCount} asset{(successCount == 1 ? string.Empty : "s")} to Properties."
            : $"Completed with issues. Exported {successCount} of {requestedCount} asset{(requestedCount == 1 ? string.Empty : "s")}. Check the list below for details.";

        IsProcessing = false;
    }

    private static List<MissingAssetRequest> ParseRequests(string input, ICollection<MissingAssetExtractionResult> failures)
    {
        var requests = new List<MissingAssetRequest>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return requests;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            string rawPath;
            var match = MissingAssetRegex.Match(trimmed);
            if (match.Success)
            {
                rawPath = match.Groups["path"].Value.Trim().Trim('"');
            }
            else if (LooksLikeAssetReference(trimmed))
            {
                rawPath = trimmed.Trim('"');
            }
            else
            {
                continue;
            }

            if (!TryNormalizePath(rawPath, out var normalizedPath, out var errorMessage))
            {
                failures.Add(new MissingAssetExtractionResult(rawPath, string.Empty, false, errorMessage));
                continue;
            }

            if (!seen.Add(normalizedPath))
            {
                continue;
            }

            requests.Add(new MissingAssetRequest(rawPath, normalizedPath));
        }

        return requests;
    }

    private static bool TryNormalizePath(string rawPath, out string normalizedPath, out string errorMessage)
    {
        normalizedPath = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            errorMessage = "The detected path was empty.";
            return false;
        }

        var cleaned = rawPath.Replace('\\', '/');
        var exportsIndex = cleaned.IndexOf("/Exports/", StringComparison.OrdinalIgnoreCase);
        if (exportsIndex >= 0)
        {
            cleaned = cleaned[(exportsIndex + "/Exports/".Length)..];
        }
        else
        {
            var outputIndex = cleaned.IndexOf("/Output/", StringComparison.OrdinalIgnoreCase);
            if (outputIndex >= 0)
            {
                cleaned = cleaned[(outputIndex + "/Output/".Length)..];
                if (cleaned.StartsWith("Exports/", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned["Exports/".Length..];
                }
            }
        }

        cleaned = cleaned.TrimStart(' ', '/', '.');

        cleaned = RemoveDuplicatePluginFolder(cleaned);

        if (cleaned.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^5];
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            errorMessage = "The detected path could not be converted to a valid asset path.";
            return false;
        }

        if (!cleaned.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
            !cleaned.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            cleaned += ".uasset";
        }

        normalizedPath = cleaned;
        return true;
    }

    private static bool LooksLikeAssetReference(string value)
    {
        return value.Contains(".json", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".uasset", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".umap", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveDuplicatePluginFolder(string cleaned)
    {
        if (string.IsNullOrEmpty(cleaned))
        {
            return cleaned;
        }

        var segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return cleaned;
        }

        var contentIndex = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], "Content", StringComparison.OrdinalIgnoreCase))
            {
                contentIndex = i;
                break;
            }
        }

        if (contentIndex <= 0 || contentIndex >= segments.Length - 1)
        {
            return cleaned;
        }

        var pluginFolder = segments[contentIndex - 1];
        var folderAfterContent = segments[contentIndex + 1];

        if (!string.Equals(pluginFolder, folderAfterContent, StringComparison.OrdinalIgnoreCase))
        {
            return cleaned;
        }

        var keptSegments = new List<string>(segments.Length - 1);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i == contentIndex + 1)
            {
                continue;
            }

            keptSegments.Add(segments[i]);
        }

        return string.Join('/', keptSegments);
    }

    private bool TryResolveGameFile(string assetPath, out GameFile gameFile, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (_applicationView.CUE4Parse.Provider.Files.TryGetValue(assetPath, out gameFile))
        {
            return true;
        }

        // Fallback to a case-insensitive search if the direct lookup fails.
        gameFile = _applicationView.CUE4Parse.Provider.Files.Values
            .FirstOrDefault(file => file.Path.Equals(assetPath, StringComparison.OrdinalIgnoreCase));

        if (gameFile != null)
        {
            return true;
        }

        errorMessage = "Asset was not found in the mounted game files.";
        return false;
    }

    private readonly record struct MissingAssetRequest(string OriginalPath, string AssetPath);
}

public class MissingAssetExtractionResult
{
    public MissingAssetExtractionResult(string originalPath, string assetPath, bool success, string message)
    {
        OriginalPath = originalPath;
        AssetPath = assetPath;
        Success = success;
        Message = message;
    }

    public string OriginalPath { get; }
    public string AssetPath { get; }
    public bool Success { get; }
    public string Message { get; }

    public string Status => Success ? "Success" : "Failed";
}
