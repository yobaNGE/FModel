using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    private static readonly FileExtensionOption[] BuiltInExtensionOptions =
    {
        new("Assets (.uasset)", new[] { ".uasset" }),
        new("Maps (.umap / .map)", new[] { ".umap", ".map" }),
        new("Assets → Maps (.uasset, .umap, .map)", new[] { ".uasset", ".umap", ".map" }),
        new("Common asset files (.uasset, .umap, .map, .uexp, .ubulk, .locres)", new[] { ".uasset", ".umap", ".map", ".uexp", ".ubulk", ".locres" })
    };
    private static readonly string[] ExtensionHintTokens = BuiltInExtensionOptions
        .SelectMany(option => option.Extensions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private readonly ApplicationViewModel _applicationView = ApplicationService.ApplicationView;
    private readonly ThreadWorkerViewModel _threadWorkerView = ApplicationService.ThreadWorkerView;

    private readonly IReadOnlyList<FileExtensionOption> _extensionOptions;

    public MissingAssetExtractorViewModel()
    {
        _extensionOptions = Array.AsReadOnly(BuiltInExtensionOptions);
        _selectedExtensionOption = _extensionOptions.Count > 2 ? _extensionOptions[2] : _extensionOptions[0];
    }

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

    public IReadOnlyList<FileExtensionOption> ExtensionOptions => _extensionOptions;

    private FileExtensionOption _selectedExtensionOption;
    public FileExtensionOption SelectedExtensionOption
    {
        get => _selectedExtensionOption;
        set
        {
            if (_extensionOptions.Count == 0)
            {
                SetProperty(ref _selectedExtensionOption, value);
                return;
            }

            var fallback = _extensionOptions[0];
            SetProperty(ref _selectedExtensionOption, value ?? fallback);
        }
    }

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
        var selectedExtensions = SelectedExtensionOption?.Extensions ?? Array.Empty<string>();
        try
        {
            await _threadWorkerView.Begin(cancellationToken =>
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryResolveGameFile(request, selectedExtensions, out var gameFile, out var resolvedAssetPath, out var error))
                    {
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, resolvedAssetPath, false, error));
                        continue;
                    }

                    try
                    {
                        _applicationView.CUE4Parse.Extract(cancellationToken, gameFile, _applicationView.CUE4Parse.TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto);
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, resolvedAssetPath, true, "Exported properties (.json)."));
                    }
                    catch (Exception e)
                    {
                        extractionResults.Add(new MissingAssetExtractionResult(request.OriginalPath, resolvedAssetPath, false, e.Message));
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


            if (!TryNormalizePath(rawPath, out var normalizedBasePath, out var providedExtension, out var errorMessage))
            {
                failures.Add(new MissingAssetExtractionResult(rawPath, string.Empty, false, errorMessage));
                continue;
            }

            var dedupeKey = providedExtension == null ? normalizedBasePath : normalizedBasePath + providedExtension;
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            requests.Add(new MissingAssetRequest(rawPath, normalizedBasePath, providedExtension));
        }

        return requests;
    }

    private static bool TryNormalizePath(string rawPath, out string normalizedBasePath, out string? providedExtension, out string errorMessage)
    {
        normalizedBasePath = string.Empty;
        errorMessage = string.Empty;
        providedExtension = null;

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

        cleaned = cleaned.TrimEnd('/');

        var extension = Path.GetExtension(cleaned);
        if (!string.IsNullOrEmpty(extension))
        {
            providedExtension = extension;
            cleaned = cleaned[..^extension.Length];
        }

        cleaned = cleaned.TrimEnd('.');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            errorMessage = "The detected path could not be converted to a valid asset path.";
            return false;
        }

        normalizedBasePath = cleaned;
        return true;
    }

    private static bool LooksLikeAssetReference(string value)
    {
        if (value.Contains(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var extension in ExtensionHintTokens)
        {
            if (value.Contains(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private bool TryResolveGameFile(MissingAssetRequest request, IReadOnlyList<string> preferredExtensions, out GameFile gameFile, out string resolvedAssetPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        var candidates = BuildCandidatePaths(request, preferredExtensions);

        foreach (var candidate in candidates)
        {
            if (TryGetGameFile(candidate, out gameFile))
            {
                resolvedAssetPath = gameFile.Path;
                return true;
            }
        }

        gameFile = null;
        resolvedAssetPath = candidates.Count > 0
            ? candidates[0]
            : request.AssetBasePath + (request.ProvidedExtension ?? string.Empty);

        errorMessage = request.ProvidedExtension != null
            ? "Asset was not found in the mounted game files."
            : BuildMissingExtensionError(preferredExtensions);
        return false;
    }

    private static List<string> BuildCandidatePaths(MissingAssetRequest request, IReadOnlyList<string> preferredExtensions)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(request.ProvidedExtension))
        {
            AddCandidate(candidates, seen, request.AssetBasePath + request.ProvidedExtension);
        }

        if (preferredExtensions.Count == 0)
        {
            if (candidates.Count == 0)
            {
                AddCandidate(candidates, seen, request.AssetBasePath);
            }

            return candidates;
        }

        foreach (var extension in preferredExtensions)
        {
            var normalizedExtension = NormalizeExtension(extension);
            if (string.IsNullOrEmpty(normalizedExtension))
            {
                continue;
            }

            AddCandidate(candidates, seen, request.AssetBasePath + normalizedExtension);
        }

        if (candidates.Count == 0)
        {
            AddCandidate(candidates, seen, request.AssetBasePath);
        }

        return candidates;
    }

    private static void AddCandidate(ICollection<string> candidates, ISet<string> seen, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
        {
            return;
        }
        candidates.Add(candidate);
    }

    private bool TryGetGameFile(string assetPath, out GameFile gameFile)
    {
        if (_applicationView.CUE4Parse.Provider.Files.TryGetValue(assetPath, out gameFile))
        {
            return true;
        }

        // Fallback to a case-insensitive search if the direct lookup fails.
        gameFile = _applicationView.CUE4Parse.Provider.Files.Values
            .FirstOrDefault(file => file.Path.Equals(assetPath, StringComparison.OrdinalIgnoreCase));

        return gameFile != null;
    }

    private static string BuildMissingExtensionError(IReadOnlyList<string> preferredExtensions)
    {
        if (preferredExtensions.Count == 0)
        {
            return "Asset was not found in the mounted game files.";
        }

        var normalized = preferredExtensions
            .Select(NormalizeExtension)
            .Where(extension => !string.IsNullOrEmpty(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0
            ? "Asset was not found in the mounted game files."
            : $"Asset was not found in the mounted game files. Extensions tried: {string.Join(", ", normalized)}.";
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : $".{trimmed}";
    }

    private readonly record struct MissingAssetRequest(string OriginalPath, string AssetBasePath, string? ProvidedExtension);
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

public class FileExtensionOption
{
    public FileExtensionOption(string displayName, IEnumerable<string> extensions)
    {
        DisplayName = displayName;
        Extensions = extensions?
                         .Select(NormalizeExtension)
                         .Where(extension => !string.IsNullOrWhiteSpace(extension))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToArray()
                     ?? Array.Empty<string>();
    }

    public string DisplayName { get; }
    public IReadOnlyList<string> Extensions { get; }
    public string Description => Extensions.Count == 0 ? "No extensions configured." : string.Join(", ", Extensions);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : $".{trimmed}";
    }
}
