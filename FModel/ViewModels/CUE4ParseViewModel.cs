using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.CriWare;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.CriWare;
using CUE4Parse.UE4.CriWare.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.LowLevel;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;

namespace FModel.ViewModels;

public class CUE4ParseViewModel : ViewModel
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<FieldInfo>> ExpressionFieldCache = new();
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private bool _modelIsOverwritingMaterial;
    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    private bool _modelIsWaitingAnimation;
    public bool ModelIsWaitingAnimation
    {
        get => _modelIsWaitingAnimation;
        set => SetProperty(ref _modelIsWaitingAnimation, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;
    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null) return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }

    public AbstractVfsFileProvider Provider { get; }
    public GameDirectoryViewModel GameDirectory { get; }
    public AssetsFolderViewModel AssetsFolder { get; }
    public SearchViewModel SearchVm { get; }
    public TabControlViewModel TabControl { get; }
    public ConfigIni IoStoreOnDemand { get; }
    private Lazy<WwiseProvider> _wwiseProviderLazy;
    public WwiseProvider WwiseProvider => _wwiseProviderLazy.Value;
    private Lazy<FModProvider> _fmodProviderLazy;
    public FModProvider FmodProvider => _fmodProviderLazy?.Value;
    private Lazy<CriWareProvider> _criWareProviderLazy;
    public CriWareProvider CriWareProvider => _criWareProviderLazy?.Value;
    public ConcurrentBag<string> UnknownExtensions = [];

    public CUE4ParseViewModel()
    {
        var currentDir = UserSettings.Default.CurrentDir;
        var gameDirectory = currentDir.GameDirectory;
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(currentDir.Versioning.CustomVersions),
            optionOverrides: currentDir.Versioning.Options,
            mapStructTypesOverrides: currentDir.Versioning.MapStructTypes);
        var pathComparer = StringComparer.OrdinalIgnoreCase;

        switch (gameDirectory)
        {
            case Constants._FN_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                break;
            }
            case Constants._VAL_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                break;
            }
            default:
            {
                var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content").SubstringAfterLast("\\");
                Provider = project switch
                {
                    "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_AshEchoes => new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_BlackStigma => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
                    _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
                };

                break;
            }
        }

        Provider.ReadScriptData = UserSettings.Default.ReadScriptData;
        Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
        Provider.ReadNaniteData = true;

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));
    }

    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            switch (Provider)
            {
                case StreamedFileProvider p:
                    switch (p.LiveGame)
                    {
                        case "FortniteLive":
                        {
                            var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                            if (manifestInfo is null)
                            {
                                throw new FileLoadException("Could not load latest Fortnite manifest, you may have to switch to your local installation.");
                            }

                            var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
                            var manifestOptions = new ManifestParseOptions
                            {
                                ChunkCacheDirectory = cacheDir,
                                ManifestCacheDirectory = cacheDir,
                                ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/",
                                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                                DecompressorState = ZlibHelper.Instance,
                                CacheChunksAsIs = false
                            };

                            var startTs = Stopwatch.GetTimestamp();
                            FBuildPatchAppManifest manifest;

                            try
                            {
                                (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                                    cancellationToken: cancellationToken,
                                    elementManifestPredicate: static x => x.Uri.Host == "download.epicgames.com"
                                ).GetAwaiter().GetResult();
                            }
                            catch (HttpRequestException ex)
                            {
                                Log.Error("Failed to download manifest ({ManifestUri})", ex.Data["ManifestUri"]?.ToString() ?? "");
                                throw;
                            }

                            if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
                            {
                                IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
                            }

                            Parallel.ForEach(manifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                            {
                                p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                    it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), p.Versions));
                            });

                            var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Fortnite [LIVE] has been loaded successfully in {elapsedTime.TotalMilliseconds:F1}ms", Constants.WHITE, true));
                            break;
                        }
                        case "ValorantLive":
                        {
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception("Could not load latest Valorant manifest, you may have to switch to your local installation.");
                            }

                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' has been loaded successfully", Constants.WHITE, true));
                            break;
                        }
                    }

                    break;
                case DefaultFileProvider:
                {
                    var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                    if (File.Exists(ioStoreOnDemandPath))
                    {
                        using var s = new StreamReader(ioStoreOnDemandPath);
                        IoStoreOnDemand.Read(s);
                    }
                    break;
                }
            }

            Provider.Initialize();
            _wwiseProviderLazy = new Lazy<WwiseProvider>(() => new WwiseProvider(Provider, UserSettings.Default.WwiseMaxBnkPrefetch));
            _fmodProviderLazy = new Lazy<FModProvider>(() => new FModProvider(Provider, UserSettings.Default.GameDirectory));
            _criWareProviderLazy = new Lazy<CriWareProvider>(() => new CriWareProvider(Provider, UserSettings.Default.GameDirectory));
            Log.Information($"{Provider.Versions.Game} ({Provider.Versions.Platform}) | Archives: x{Provider.UnloadedVfs.Count} | AES: x{Provider.RequiredKeys.Count} | Loose Files: x{Provider.Files.Count}");
        });
    }

    /// <summary>
    /// load virtual files system from GameDirectory
    /// </summary>
    /// <returns></returns>
    public void LoadVfs(IEnumerable<KeyValuePair<FGuid, FAesKey>> aesKeys)
    {
        Provider.SubmitKeys(aesKeys);
        Provider.PostMount();

        var aesMax = Provider.RequiredKeys.Count + Provider.Keys.Count;
        var archiveMax = Provider.UnloadedVfs.Count + Provider.MountedVfs.Count;
        Log.Information($"Project: {Provider.ProjectName} | Mounted: {Provider.MountedVfs.Count}/{archiveMax} | AES: {Provider.Keys.Count}/{aesMax} | Files: x{Provider.Files.Count}");
    }

    public void ClearProvider()
    {
        if (Provider == null) return;

        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        Helper.CloseWindow<AdonisWindow>("Search View");
        Provider.UnloadNonStreamedVfs();
        GC.Collect();
    }

    public async Task RefreshAes()
    {
        // game directory dependent, we don't have the provider game name yet since we don't have aes keys
        // except when this comes from the AES Manager
        if (!UserSettings.IsEndpointValid(EEndpointType.Aes, out var endpoint))
            return;

        await _threadWorkerView.Begin(cancellationToken =>
        {
            // deprecated values
            if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/aes") endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/aes";

            var aes = _apiEndpointView.DynamicApi.GetAesKeys(cancellationToken, endpoint.Url, endpoint.Path);
            if (aes is not { IsValid: true }) return;

            UserSettings.Default.CurrentDir.AesKeys = aes;
        });
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null) return;

            FLogger.Append(ELog.None, () =>
            {
                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.Text(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        });
    }

    public Task InitMappings(bool force = false)
    {
        if (!UserSettings.IsEndpointValid(EEndpointType.Mapping, out var endpoint))
        {
            Provider.MappingsContainer = null;
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var l = ELog.Information;
            if (endpoint.Overwrite && File.Exists(endpoint.FilePath))
            {
                Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(endpoint.FilePath);
            }
            else if (endpoint.IsValid)
            {
                // deprecated values
                if (endpoint.Path == "$.[?(@.meta.compressionMethod=='Oodle')].['url','fileName']") endpoint.Path = "$.[0].['url','fileName']";
                if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/mappings")
                {
                    endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/mappings";
                    endpoint.Path = "$.mappings.ZStandard";
                }

                var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                var mappings = _apiEndpointView.DynamicApi.GetMappings(CancellationToken.None, endpoint.Url, endpoint.Path);
                if (mappings is { Length: > 0 })
                {
                    foreach (var mapping in mappings)
                    {
                        if (!mapping.IsValid) continue;

                        var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                        if (force || !File.Exists(mappingPath) || new FileInfo(mappingPath).Length == 0)
                        {
                            _apiEndpointView.DownloadFile(mapping.Url, mappingPath);
                        }

                        Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
                        break;
                    }
                }

                if (Provider.MappingsContainer == null)
                {
                    var latestUsmaps = new DirectoryInfo(mappingsFolder).GetFiles("*_oo.usmap");
                    if (latestUsmaps.Length <= 0) return;

                    var latestUsmapInfo = latestUsmaps.OrderBy(f => f.LastWriteTime).Last();
                    Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(latestUsmapInfo.FullName);
                    l = ELog.Warning;
                }
            }

            if (Provider.MappingsContainer is FileUsmapTypeMappingsProvider m)
            {
                Log.Information($"Mappings pulled from '{m.FileName}'");
                FLogger.Append(l, () => FLogger.Text($"Mappings pulled from '{m.FileName}'", Constants.WHITE, true));
            }
        });
    }

    public Task VerifyConsoleVariables()
    {
        if (Provider.Versions["StripAdditiveRefPose"])
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("Additive animations have their reference pose stripped, which will lead to inaccurate preview and export", Constants.WHITE, true));
        }

        if (Provider.Versions.Game is EGame.GAME_UE4_LATEST or EGame.GAME_UE5_LATEST && !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase)) // ignore fortnite globally
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Experimental UE version selected, likely unsuitable for '{Provider.GameDisplayName ?? Provider.ProjectName}'", Constants.WHITE, true));
        }

        return Task.CompletedTask;
    }

    public Task VerifyOnDemandArchives()
    {
        // only local fortnite
        if (Provider is not DefaultFileProvider || !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // scuffed but working
        var persistentDownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame/Saved/PersistentDownloadDir");
        var iasFileInfo = new FileInfo(Path.Combine(persistentDownloadDir, "ias", "ias.cache.0"));
        if (!iasFileInfo.Exists || iasFileInfo.Length == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var inst = new List<InstructionToken>();
            IoStoreOnDemand.FindPropertyInstructions("Endpoint", "TocPath", inst);
            if (inst.Count <= 0) return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud", inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath)) return;

            await _apiEndpointView.EpicApi.VerifyAuth(CancellationToken.None);
            await Provider.RegisterVfs(new IoChunkToc(ioStoreOnDemandPath), new IoStoreOnDemandOptions
            {
                ChunkBaseUri = new Uri("https://download.epicgames.com/ias/fortnite/", UriKind.Absolute),
                ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                Authorization = new AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken),
                Timeout = TimeSpan.FromSeconds(30)
            });
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com", Constants.WHITE, true));
        });
    }

    public int LocalizedResourcesCount { get; set; }
    public bool LocalResourcesDone { get; set; }
    public bool HotfixedResourcesDone { get; set; }

    public async Task LoadLocalizedResources()
    {
        var snapshot = LocalizedResourcesCount;
        await Task.WhenAll(LoadGameLocalizedResources(), LoadHotfixedLocalizedResources()).ConfigureAwait(false);

        LocalizedResourcesCount = Provider.Internationalization.Count;
        if (snapshot != LocalizedResourcesCount)
        {
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{LocalizedResourcesCount} localized resources loaded for '{UserSettings.Default.AssetLanguage.GetDescription()}'", Constants.WHITE, true));
            Utils.Typefaces = new Typefaces(this);
        }
    }

    private Task LoadGameLocalizedResources()
    {
        if (LocalResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            LocalResourcesDone = Provider.TryChangeCulture(Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
        });
    }

    private Task LoadHotfixedLocalizedResources()
    {
        if (!Provider.ProjectName.Equals("fortnitegame", StringComparison.OrdinalIgnoreCase) || HotfixedResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var hotfixes = ApplicationService.ApiEndpointView.CentralApi.GetHotfixes(CancellationToken.None, Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
            if (hotfixes == null) return;

            Provider.Internationalization.Override(hotfixes);
            HotfixedResourcesDone = true;
        });
    }

    private int _virtualPathCount { get; set; }
    public Task LoadVirtualPaths()
    {
        if (_virtualPathCount > 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _virtualPathCount = Provider.LoadVirtualPaths(UserSettings.Default.CurrentDir.UeVersion.GetVersion());
            if (_virtualPathCount > 0)
            {
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"{_virtualPathCount} virtual paths loaded", Constants.WHITE, true));
            }
            else
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("Could not load virtual paths, plugin manifest may not exist", Constants.WHITE, true));
            }
        });
    }

    public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<GameFile> assetItems)
    {
        foreach (var entry in assetItems)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Extract(cancellationToken, entry, TabControl.HasNoTabs);
        }
    }

    private void BulkFolder(CancellationToken cancellationToken, TreeItem folder, Action<GameFile> action)
    {
        foreach (var entry in folder.AssetsList.Assets)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action(entry);
            }
            catch
            {
                // ignore
            }
        }

        foreach (var f in folder.Folders) BulkFolder(cancellationToken, f, action);
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        Parallel.ForEach(folder.AssetsList.Assets, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportData(entry, false);
        });

        foreach (var f in folder.Folders) ExportFolder(cancellationToken, f);
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto));

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto));

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Meshes | EBulkType.Auto));

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto));

    public void AudioFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Audio | EBulkType.Auto));

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false, EBulkType bulk = EBulkType.None)
    {
        Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        if (addNewTab && TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);
        switch (entry.Extension)
        {
            case "uasset":
            case "umap":
            {
                var result = Provider.GetLoadPackageResult(entry);
                TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;

                if (saveProperties || updateUi)
                {
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(saveProperties), Formatting.Indented), saveProperties, updateUi);
                    if (saveProperties) break; // do not search for viewable exports if we are dealing with jsons
                }

                for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                {
                    if (CheckExport(cancellationToken, result.Package, i, bulk))
                        break;
                }

                break;
            }
            case "ini" when entry.Name.Contains("BinaryConfig"):
            {
                var ar = entry.CreateReader();
                var configCache = new FConfigCacheIni(ar);

                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(configCache, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "upluginmanifest":
            case "code-workspace":
            case "projectstore":
            case "uefnproject":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "gitignore":
            case "LICENSE":
            case "playstats": // Dispatch
            case "template":
            case "stUMeta": // LIS: Double Exposure
            case "vmodule":
            case "glslfx":
            case "cptake":
            case "uparam": // Steel Hunters
            case "spi1d":
            case "verse":
            case "html":
            case "json5":
            case "json":
            case "uref":
            case "cube":
            case "usda":
            case "ocio":
            case "data" when Provider.ProjectName is "OakGame":
            case "ini":
            case "txt":
            case "log":
            case "lsd": // Days Gone
            case "bat":
            case "dat":
            case "cfg":
            case "ddr":
            case "ide":
            case "ipl":
            case "zon":
            case "xml":
            case "css":
            case "csv":
            case "pem":
            case "tps":
            case "tgc": // State of Decay 2
            case "cpp":
            case "apx":
            case "udn":
            case "doc":
            case "lua":
            case "vdf":
            case "yml":
            case "js":
            case "po":
            case "md":
            case "h":
            // Uncharted Waters Origin
            case "crn":
            case "uwt":
            case "wvh":
            case "bf":
            case "bl":
            case "bm":
            case "br":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                break;
            }
            case "locmeta":
            {
                var archive = entry.CreateReader();
                var metadata = new FTextLocalizationMetaDataResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "locres":
            {
                var archive = entry.CreateReader();
                var locres = new FTextLocalizationResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FAssetRegistryState(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FGlobalShaderCache(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bank":
            {
                var archive = entry.CreateReader();
                if (!FModProvider.TryLoadBank(archive, entry.NameWithoutExtension, out var fmodReader))
                {
                    Log.Error($"Failed to load FMOD bank {entry.Path}");
                    break;
                }

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(fmodReader, Formatting.Indented, converters: [new FmodSoundBankConverter(), new StringEnumConverter()]), saveProperties, updateUi);

                var extractedSounds = FmodProvider.ExtractBankSounds(fmodReader);
                var directory = Path.GetDirectoryName(entry.Path) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "bnk":
            case "pck":
            {
                var archive = entry.CreateReader();
                var wwise = new WwiseReader(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), saveProperties, updateUi);

                var medias = WwiseProvider.ExtractBankSounds(wwise);
                foreach (var media in medias)
                {
                    SaveAndPlaySound(media.OutputPath, media.Extension, media.Data, saveAudio);
                }

                break;
            }
            case "awb":
            {
                var archive = entry.CreateReader();
                var awbReader = new AwbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(awbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(awbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "acb":
            {
                var archive = entry.CreateReader();
                var acbReader = new AcbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(acbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(acbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }

                break;
            }
            case "xvag":
            case "flac":
            case "at9":
            case "wem":
            case "wav":
            case "WAV":
            case "ogg":
                // todo: CSCore.MediaFoundation.MediaFoundationException The byte stream type of the given URL is unsupported. case "aif":
            {
                var data = Provider.SaveAsset(entry);
                SaveAndPlaySound(entry.PathWithoutExtension, entry.Extension, data, saveAudio);

                break;
            }
            case "udic":
            {
                var archive = entry.CreateReader();
                var header = new FOodleDictionaryArchive(archive).Header;
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "png":
            case "jpg":
            case "bmp":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, SKBitmap.Decode(stream), saveTextures, updateUi);

                break;
            }
            case "svg":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(512, 512));
                svg.Load(stream);

                var bitmap = new SKBitmap(512, 512);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
                {
                    canvas.DrawPicture(svg.Picture, paint);
                }

                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);

                break;
            }
            case "ufont":
            case "otf":
            case "ttf":
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"Export '{entry.Name}' raw data and change its extension if you want it to be an installable font file", Constants.WHITE, true));
                break;
            case "ushaderbytecode":
            case "ushadercode":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderCodeArchive(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "upipelinecache":
            {
                var archive = entry.CreateReader();
                var ar = new FPipelineCacheFile(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "stinfo":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderTypeHashes(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "res": // just skip
            case "luac": // compiled lua
            case "bytes": // wuthering waves
                break;
            default:
            {
                Log.Warning($"The package '{entry.Name}' is of an unknown type.");
                if (!UnknownExtensions.Contains(entry.Extension))
                {
                    UnknownExtensions.Add(entry.Extension);
                FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"There are some packages with an unknown type {entry.Extension}. Check Log file for a full list.", Constants.WHITE, true));
                }
                break;
            }
        }
    }

    public void ExtractAndScroll(CancellationToken cancellationToken, string fullPath, string objectName, string parentExportType)
    {
        Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);

        var entry = Provider[fullPath];
        TabControl.AddTab(entry, parentExportType);
        TabControl.SelectedTab.ScrollTrigger = objectName;

        var result = Provider.GetLoadPackageResult(entry, objectName);

        TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.Indented), false, false);

        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
        {
            if (CheckExport(cancellationToken, result.Package, i))
                break;
        }
    }

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index, EBulkType bulk = EBulkType.None) // return true once you want to stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
            {
                if (!TabControl.CanAddTabs) return false;

                TabControl.AddTab($"{verseDigest.ProjectName}.verse");
                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("verse");
                TabControl.SelectedTab.SetDocumentText(verseDigest.ReadableCode, false, false);
                return true;
            }
            case UTexture when (isNone || saveTextures) && pointer.Object.Value is UTexture texture:
            {
                TabControl.SelectedTab.AddImage(texture, saveTextures, updateUi);
                return false;
            }
            case USvgAsset when (isNone || saveTextures) && pointer.Object.Value is USvgAsset svgasset:
            {
                const int size = 512;
                var data = svgasset.GetOrDefault<byte[]>("SvgData");
                var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(size, size));
                svg.Load(stream);

                var bitmap = new SKBitmap(size, size);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
                {
                    canvas.DrawPicture(svg.Picture, paint);
                }

                if (saveTextures)
                {
                    var fileName = sourceFile.SubstringAfterLast('/');
                    var path = Path.Combine(UserSettings.Default.TextureDirectory,
                        UserSettings.Default.KeepDirectoryStructure ? TabControl.SelectedTab.Entry.Directory : "", fileName!).Replace('\\', '/');

                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));

                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    fs.Write(data, 0, data.Length);
                    if (File.Exists(path))
                    {
                        Log.Information("{FileName} successfully saved", fileName);
                        if (updateUi)
                        {
                            FLogger.Append(ELog.Information, () =>
                            {
                                FLogger.Text("Successfully saved ", Constants.WHITE);
                                FLogger.Link(fileName, path, true);
                            });
                        }
                    }
                    else
                    {
                        Log.Error("{FileName} could not be saved", fileName);
                        if (updateUi)
                            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{fileName}'", Constants.WHITE, true));
                    }
                }

                TabControl.SelectedTab.AddImage(sourceFile.SubstringAfterLast('/'), false, bitmap, false, updateUi);
                return false;
            }
            case UAkAudioEvent when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEvent audioEvent:
            {
                var extractedSounds = WwiseProvider.ExtractAudioEventSounds(audioEvent);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(sound.OutputPath, sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case UFMODEvent when (isNone || saveAudio) && pointer.Object.Value is UFMODEvent fmodEvent:
            {
                var extractedSounds = FmodProvider.ExtractEventSounds(fmodEvent);
                var directory = Path.GetDirectoryName(fmodEvent.Owner?.Name) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case UFMODBank when (isNone || saveAudio) && pointer.Object.Value is UFMODBank fmodBank:
            {
                var extractedSounds = FmodProvider.ExtractBankSounds(fmodBank);
                var directory = Path.GetDirectoryName(fmodBank.Owner?.Name) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case USoundAtomCueSheet or UAtomCueSheet or USoundAtomCue or UAtomWaveBank when (isNone || saveAudio) && pointer.Object.Value is UObject atomObject:
            {
                var extractedSounds = atomObject switch
                {
                    USoundAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    UAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    USoundAtomCue cue => CriWareProvider.ExtractCriWareSounds(cue),
                    UAtomWaveBank awb => CriWareProvider.ExtractCriWareSounds(awb),
                    _ => []
                };

                var directory = Path.GetDirectoryName(atomObject.Owner?.Name) ?? "/Criware/";
                directory = Path.GetDirectoryName(atomObject.Owner.Provider.FixPath(directory));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio);
                }
                return false;
            }
            case UAkMediaAssetData when isNone || saveAudio:
            case USoundWave when isNone || saveAudio:
            {
                var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                var hasAf = !string.IsNullOrEmpty(audioFormat);
                if (data == null || !hasAf)
                {
                    if (hasAf) FLogger.Append(ELog.Warning, () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                    return false;
                }

                SaveAndPlaySound(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/'), audioFormat, data, saveAudio);
                return false;
            }
            case UWorld when isNone && UserSettings.Default.PreviewWorlds:
            case UBlueprintGeneratedClass when isNone && UserSettings.Default.PreviewWorlds && TabControl.SelectedTab.ParentExportType switch
            {
                "JunoBuildInstructionsItemDefinition" => true,
                "JunoBuildingSetAccountItemDefinition" => true,
                "JunoBuildingPropAccountItemDefinition" => true,
                _ => false
            }:
            case UPaperSprite when isNone && UserSettings.Default.PreviewMaterials:
            case UStaticMesh when isNone && UserSettings.Default.PreviewStaticMeshes:
            case USkeletalMesh when isNone && UserSettings.Default.PreviewSkeletalMeshes:
            case USkeleton when isNone && UserSettings.Default.SaveSkeletonAsMesh:
            case UMaterialInstance when isNone && UserSettings.Default.PreviewMaterials && !ModelIsOverwritingMaterial &&
                                        !(Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) &&
                                          (pkg.Name.Contains("/MI_OfferImages/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/RenderSwitch_Materials/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/MI_BPTile/", StringComparison.OrdinalIgnoreCase))):
            {
                if (SnooperViewer.TryLoadExport(cancellationToken, dummy, pointer.Object))
                    SnooperViewer.Run();
                return true;
            }
            case UMaterialInstance when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
            {
                SnooperViewer.Renderer.Swap(m);
                SnooperViewer.Run();
                return true;
            }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations || ModelIsWaitingAnimation:
            {
                // animate all animations using their specified skeleton or when we explicitly asked for a loaded model to be animated (ignoring whether we wanted to preview animations)
                SnooperViewer.Renderer.Animate(pointer.Object.Value);
                SnooperViewer.Run();
                return true;
            }
            case UStaticMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeletalMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeleton when UserSettings.Default.SaveSkeletonAsMesh && HasFlag(bulk, EBulkType.Meshes):
            // case UMaterialInstance when HasFlag(bulk, EBulkType.Materials): // read the fucking json
            case UAnimSequenceBase when HasFlag(bulk, EBulkType.Animations):
            {
                SaveExport(pointer.Object.Value, updateUi);
                return true;
            }
            default:
            {
                if (!isNone && !saveTextures) return false;

                using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object, UserSettings.Default.CosmeticStyle);
                if (!cPackage.TryConstructCreator(out var creator))
                    return false;

                creator.ParseForInfo();
                TabControl.SelectedTab.AddImage(pointer.Object.Value.Name, false, creator.Draw(), saveTextures, updateUi);
                return true;

            }
        }
    }

    public void ShowMetadata(GameFile entry)
    {
        var package = Provider.LoadPackage(entry);

        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    public void Decompile(GameFile entry)
    {
        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Decompiled";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");

        UClassCookedMetaData cookedMetaData = null;
        try
        {
            var editorPkg = Provider.LoadPackage(entry.Path.Replace(".uasset", ".o.uasset"));
            cookedMetaData = editorPkg.GetExport<UClassCookedMetaData>("CookedClassMetaData");
        }
        catch
        {
            // ignored
        }

        var cppList = new List<string>();
        var pkg = Provider.LoadPackage(entry);
        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null && pointer.Class?.Object?.Value is null)
                continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
            if (dummy is not UClass || pointer.Object.Value is not UClass blueprint)
                continue;

            cppList.Add(blueprint.DecompileBlueprintToPseudo(cookedMetaData));
        }

        var cpp = cppList.Count > 1 ? string.Join("\n\n", cppList) : cppList.FirstOrDefault() ?? string.Empty;
        if (entry.Path.Contains("_Verse.uasset"))
        {
            cpp = Regex.Replace(cpp, "__verse_0x[a-fA-F0-9]{8}_", ""); // UnmangleCasedName
        }
        cpp = Regex.Replace(cpp, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");


        TabControl.SelectedTab.SetDocumentText(cpp, false, false);
    }

    public void DecompileLowLevel(GameFile entry)
    {
        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Low-Level";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("txt");
        TabControl.SelectedTab.IsLowLevelView = false;
        TabControl.SelectedTab.LowLevelData = null;

        if (!TryLoadPackageForLowLevel(entry, out var pkg, out var loadErrorText))
        {
            TabControl.SelectedTab.SetDocumentText(loadErrorText ?? "// Failed to load package for low-level decompile.", false, false);
            return;
        }

        if (pkg is not AbstractUePackage uePackage)
        {
            var unsupportedText = $"// Unsupported package type for low-level decompile: {pkg.GetType().Name}";
            TabControl.SelectedTab.SetDocumentText(unsupportedText, false, false);
            return;
        }

        byte[]? combinedPackageBytes = null;
        string? rawDataWarning = null;
        if (Provider.TrySavePackage(entry, out var assets))
        {
            _ = TryBuildCombinedPackageBytes(assets, out combinedPackageBytes, out rawDataWarning);
        }
        else
        {
            rawDataWarning = "Unable to load raw package bytes from provider.";
        }

        var sections = new List<string>();
        var classes = new List<LowLevelClassData>();
        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null && pointer?.Class?.Object?.Value is null)
                continue;

            var dummy = uePackage.ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
            if (dummy is not UClass || pointer.Object?.Value is not UClass blueprint)
                continue;

            sections.Add(BuildLowLevelClassView(pkg, i, blueprint, combinedPackageBytes, rawDataWarning));
            classes.Add(BuildLowLevelClassData(pkg, i, blueprint, combinedPackageBytes, rawDataWarning));
        }

        var classesWithReferences = BuildFunctionReferences(pkg, classes);
        var exportMapEntries = BuildExportMapEntries(pkg);
        var importMapEntries = BuildImportMapEntries(pkg);
        var nameMapEntries = BuildNameMapEntries(pkg, exportMapEntries, importMapEntries, classesWithReferences);

        if (sections.Count == 0)
        {
            sections.Add("// No UClass exports were found in this package.");
        }

        var packageData = new LowLevelPackageData(
            entry.Name,
            pkg.GetType().Name,
            classesWithReferences,
            rawDataWarning,
            exportMapEntries,
            importMapEntries,
            nameMapEntries);
        TabControl.SelectedTab.LowLevelData = packageData;
        TabControl.SelectedTab.IsLowLevelView = true;
        TabControl.SelectedTab.SetDocumentText(string.Join($"{Environment.NewLine}{Environment.NewLine}", sections), false, false);
    }

    private bool TryLoadPackageForLowLevel(GameFile entry, out IPackage package, out string? errorText)
    {
        errorText = null;
        try
        {
            package = Provider.LoadPackage(entry);
            return true;
        }
        catch (OodleException oodleEx) when (oodleEx.Message?.Contains("not initialized", StringComparison.OrdinalIgnoreCase) == true)
        {
            var oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_DLL_NAME);
            try
            {
                OodleHelper.Initialize(oodlePath);
                if (OodleHelper.Instance is null)
                    OodleHelper.Initialize();
            }
            catch (Exception initEx)
            {
                Log.Warning(initEx, "Oodle initialization retry failed");
            }

            if (OodleHelper.Instance is not null)
            {
                try
                {
                    package = Provider.LoadPackage(entry);
                    return true;
                }
                catch (Exception retryEx)
                {
                    Log.Error(retryEx, "Failed to load package after Oodle initialization retry");
                    errorText = $"// Failed to load package after Oodle initialization retry:{Environment.NewLine}" +
                                $"// {retryEx.Message}";
                    package = null!;
                    return false;
                }
            }

            errorText = $"// Oodle decompression is not initialized.{Environment.NewLine}" +
                        $"// Expected DLL: {oodlePath}{Environment.NewLine}" +
                        $"// Put `{OodleHelper.OODLE_DLL_NAME}` there (or in app root), then restart FModel.";
            Log.Warning(oodleEx, "Oodle is not initialized; low-level decompile can't load package");
            package = null!;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load package for low-level decompile");
            errorText = $"// Failed to load package:{Environment.NewLine}// {ex.Message}";
            package = null!;
            return false;
        }
    }

    private static string BuildLowLevelClassView(IPackage package, int classExportIndex, UClass blueprint, byte[]? combinedPackageBytes, string? rawDataWarning)
    {
        var className = BlueprintDecompilerUtils.GetClassWithPrefix(blueprint);
        var baseClass = BlueprintDecompilerUtils.GetClassWithPrefix(blueprint.SuperStruct.Load<UStruct>());

        var sb = new StringBuilder();
        sb.AppendLine($"// Class export #{classExportIndex + 1}: {blueprint.Name}");
        sb.AppendLine($"class {className} : {baseClass}");
        sb.AppendLine("{");
        AppendLowLevelFunctions(sb, package, blueprint);
        AppendPropertyTagsSection(sb, package, classExportIndex, blueprint);
        AppendExportHexDumpSection(sb, package, classExportIndex, combinedPackageBytes, rawDataWarning);
        sb.AppendLine("};");
        return sb.ToString();
    }

    private static LowLevelClassData BuildLowLevelClassData(IPackage package, int classExportIndex, UClass blueprint, byte[]? combinedPackageBytes, string? rawDataWarning)
    {
        var className = BlueprintDecompilerUtils.GetClassWithPrefix(blueprint);
        var baseClass = BlueprintDecompilerUtils.GetClassWithPrefix(blueprint.SuperStruct.Load<UStruct>());
        var functions = BuildLowLevelFunctionData(package, className, blueprint, combinedPackageBytes);
        var propertyTags = BuildLowLevelPropertyTagData(package, classExportIndex, blueprint);
        return new LowLevelClassData(
            classExportIndex,
            className,
            baseClass,
            functions,
            propertyTags,
            rawDataWarning);
    }

    private static List<LowLevelFunctionData> BuildLowLevelFunctionData(IPackage package, string className, UClass blueprint, byte[]? combinedPackageBytes)
    {
        var result = new List<LowLevelFunctionData>();
        foreach (var (name, pointer) in blueprint.FuncMap)
        {
            var functionExportIndex = pointer.Index - 1;
            var functionKey = BuildFunctionKey(className, name.Text, functionExportIndex);
            if (!pointer.TryLoad(out var export) || export is not UFunction function)
            {
                result.Add(new LowLevelFunctionData(
                    functionExportIndex,
                    name.Text,
                    className,
                    functionKey,
                    "<unavailable>",
                    string.Empty,
                    0,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    "Failed to load function export."));
                continue;
            }

            var scriptSummary = BuildScriptOffsetSummary(package, functionExportIndex, function);
            var (blocks, edges, blockLookup) = BuildControlFlowGraph(function);
            var delta = ResolveRawToParserDelta(package, functionExportIndex, function.ScriptBytecodeAssetOffset);
            var instructions = BuildLowLevelInstructionData(package, functionExportIndex, function, blockLookup);
            var hexRows = BuildFunctionHexRows(package, functionExportIndex, combinedPackageBytes);
            var warning = function.ScriptBytecodeRaw is null || function.ScriptBytecodeRaw.Length == 0
                ? "Raw script bytes are unavailable."
                : null;

            result.Add(new LowLevelFunctionData(
                functionExportIndex,
                name.Text,
                className,
                functionKey,
                function.FunctionFlags.ToStringBitfield(),
                scriptSummary,
                function.ScriptBytecodeSerializedSize,
                delta,
                instructions,
                hexRows,
                blocks,
                edges,
                [],
                [],
                warning));
        }

        return result;
    }

    private static string BuildScriptOffsetSummary(IPackage package, int exportIndex, UFunction function)
    {
        if (TryConvertParserOffsetToRaw(package, exportIndex, function.ScriptBytecodeAssetOffset, out var scriptRawOffset))
        {
            return scriptRawOffset == function.ScriptBytecodeAssetOffset
                ? $"0x{scriptRawOffset:X8}"
                : $"raw 0x{scriptRawOffset:X8}, parser 0x{function.ScriptBytecodeAssetOffset:X8}";
        }

        return $"parser 0x{function.ScriptBytecodeAssetOffset:X8}";
    }

    private static List<LowLevelInstructionData> BuildLowLevelInstructionData(
        IPackage package,
        int exportIndex,
        UFunction function,
        IReadOnlyDictionary<int, int> blockLookup)
    {
        var result = new List<LowLevelInstructionData>();
        if (function.ScriptBytecode is null || function.ScriptBytecode.Length == 0 || function.ScriptBytecodeRaw is null || function.ScriptBytecodeRaw.Length == 0)
            return result;

        BlueprintDecompilerUtils.Function = function;
        var jumpLabels = CollectJumpLabelOffsets(function);
        var hasScriptRawBase = TryConvertParserOffsetToRaw(package, exportIndex, function.ScriptBytecodeAssetOffset, out var scriptRawBaseOffset);
        for (var i = 0; i < function.ScriptBytecode.Length; i++)
        {
            var expression = function.ScriptBytecode[i];
            var scriptOffset = expression.StatementIndex;
            var parserOffset = function.ScriptBytecodeAssetOffset + scriptOffset;
            var (rawByteStart, length, rawRangeSource, rawRangeValid) = GetInstructionRawRange(function, expression, i);
            var hasRawOffset = TryGetInstructionAssetRawOffset(package, exportIndex, function, parserOffset, rawByteStart, hasScriptRawBase, scriptRawBaseOffset, out var rawOffset);
            var bytes = FormatInstructionBytes(function.ScriptBytecodeRaw, rawByteStart, length);
            var decoded = TryDecodeExpression(expression);
            var operands = BuildOperandTree(function.ScriptBytecodeRaw, expression, rawByteStart, rawByteStart + length);
            var label = jumpLabels.Contains(scriptOffset) ? $"Label_0x{scriptOffset:X4}" : string.Empty;
            var confidence = rawRangeSource == LowLevelRawRangeSource.ParserCaptured && rawRangeValid && hasRawOffset
                ? LowLevelOffsetConfidence.Exact
                : hasRawOffset
                    ? rawOffset == parserOffset
                        ? LowLevelOffsetConfidence.Exact
                        : LowLevelOffsetConfidence.Derived
                    : LowLevelOffsetConfidence.Fallback;
            var blockId = blockLookup.TryGetValue(scriptOffset, out var value) ? value : -1;

            result.Add(new LowLevelInstructionData(
                i,
                hasRawOffset ? rawOffset : parserOffset,
                parserOffset,
                scriptOffset,
                (hasRawOffset ? rawOffset : parserOffset) - parserOffset,
                confidence,
                blockId,
                expression.Token.ToString(),
                (byte) expression.Token,
                GetOpcodeCategory(expression.Token),
                bytes,
                decoded,
                operands,
                label,
                TryGetJumpTargetScriptOffset(expression),
                length,
                rawByteStart,
                rawByteStart + length,
                rawRangeSource,
                rawRangeValid));
        }

        return result;
    }

    private static List<LowLevelPropertyTagData> BuildLowLevelPropertyTagData(IPackage package, int classExportIndex, UClass blueprint)
    {
        var tags = new List<(string Scope, FPropertyTag Tag, int ExportIndex)>();
        AddPropertyTags(tags, "Class", classExportIndex, blueprint.Properties);

        var classDefaultObject = blueprint.ClassDefaultObject.Load();
        if (classDefaultObject != null)
        {
            var cdoExportIndex = package.GetExportIndex(classDefaultObject.Name);
            AddPropertyTags(tags, "CDO", cdoExportIndex, classDefaultObject.Properties);
            AddPropertyTags(tags, "SparseCDO", cdoExportIndex, classDefaultObject.SerializedSparseClassData?.Properties);
        }

        var result = new List<LowLevelPropertyTagData>(tags.Count);
        foreach (var (scope, tag, exportIndex) in tags)
        {
            var (type, value) = GetPropertyTagPreview(tag);

            var (headerOffset, headerFallback) = ResolveDisplayOffset(package, exportIndex, tag.HeaderStartAssetOffset);
            var (valueOffset, valueFallback) = ResolveDisplayOffset(package, exportIndex, tag.ValueStartAssetOffset);
            var (endOffset, endFallback) = ResolveDisplayOffset(package, exportIndex, tag.ValueEndAssetOffset);

            result.Add(new LowLevelPropertyTagData(
                scope,
                tag.Name.Text,
                type,
                value,
                headerOffset,
                endOffset,
                valueOffset,
                headerFallback || valueFallback || endFallback));
        }

        return result;
    }

    private static (long Offset, bool Fallback) ResolveDisplayOffset(IPackage package, int exportIndex, long parserOffset)
    {
        if (TryConvertParserOffsetToRaw(package, exportIndex, parserOffset, out var rawOffset))
            return (rawOffset, false);
        return (parserOffset, true);
    }

    private static string BuildFunctionKey(string className, string functionName, int exportIndex) => $"{className}::{functionName}#{exportIndex}";

    private static long? ResolveRawToParserDelta(IPackage package, int exportIndex, long parserOffset)
    {
        if (TryConvertParserOffsetToRaw(package, exportIndex, parserOffset, out var rawOffset))
            return rawOffset - parserOffset;
        return null;
    }

    private static List<LowLevelClassData> BuildFunctionReferences(IPackage package, IReadOnlyList<LowLevelClassData> classes)
    {
        var functionByExport = classes
            .SelectMany(c => c.Functions)
            .ToDictionary(f => f.ExportIndex, f => f);

        var outgoingByFunction = new Dictionary<string, IReadOnlyList<LowLevelXrefData>>();
        var incomingByFunction = new Dictionary<string, List<LowLevelXrefData>>();
        foreach (var function in functionByExport.Values)
        {
            var outgoing = BuildFunctionXrefs(package, function, functionByExport);
            outgoingByFunction[function.Key] = outgoing;

            foreach (var xref in outgoing)
            {
                if (string.IsNullOrEmpty(xref.TargetFunctionKey))
                    continue;

                if (!incomingByFunction.TryGetValue(xref.TargetFunctionKey, out var list))
                {
                    list = [];
                    incomingByFunction[xref.TargetFunctionKey] = list;
                }

                list.Add(xref.WithDirection("Incoming"));
            }
        }

        var result = new List<LowLevelClassData>(classes.Count);
        foreach (var classData in classes)
        {
            var functions = new List<LowLevelFunctionData>(classData.Functions.Count);
            foreach (var function in classData.Functions)
            {
                outgoingByFunction.TryGetValue(function.Key, out var outgoing);
                incomingByFunction.TryGetValue(function.Key, out var incoming);
                functions.Add(function.WithXrefs(outgoing ?? [], incoming ?? []));
            }

            result.Add(new LowLevelClassData(
                classData.ExportIndex,
                classData.ClassName,
                classData.BaseClassName,
                functions,
                classData.PropertyTags,
                classData.Warning));
        }

        return result;
    }

    private static IReadOnlyList<LowLevelXrefData> BuildFunctionXrefs(IPackage package, LowLevelFunctionData functionData, IReadOnlyDictionary<int, LowLevelFunctionData> functionByExport)
    {
        var result = new List<LowLevelXrefData>();
        var function = package.GetExport(functionData.ExportIndex) as UFunction;
        if (function?.ScriptBytecode is null || function.ScriptBytecode.Length == 0 || functionData.Instructions.Count == 0)
            return result;

        var instructionByIndex = functionData.Instructions.ToDictionary(x => x.Index, x => x);
        for (var i = 0; i < function.ScriptBytecode.Length; i++)
        {
            if (!instructionByIndex.TryGetValue(i, out var instruction))
                continue;

            switch (function.ScriptBytecode[i])
            {
                case EX_LocalFinalFunction localFinal:
                    AddFinalFunctionXref(result, functionData, functionByExport, instruction, localFinal);
                    break;
                case EX_FinalFunction final:
                    AddFinalFunctionXref(result, functionData, functionByExport, instruction, final);
                    break;
                case EX_LocalVirtualFunction localVirtual:
                    AddVirtualFunctionXref(result, functionData, instruction, localVirtual.VirtualFunctionName.Text);
                    break;
                case EX_VirtualFunction virtualFunction:
                    AddVirtualFunctionXref(result, functionData, instruction, virtualFunction.VirtualFunctionName.Text);
                    break;
                case EX_ObjectConst objectConst:
                    AddObjectXref(result, functionData, instruction, objectConst.Value);
                    break;
                case EX_DefaultVariable defaultVariable:
                    AddVariableXref(result, functionData, instruction, defaultVariable.Variable);
                    break;
                case EX_InstanceVariable instanceVariable:
                    AddVariableXref(result, functionData, instruction, instanceVariable.Variable);
                    break;
                case EX_LocalOutVariable localOutVariable:
                    AddVariableXref(result, functionData, instruction, localOutVariable.Variable);
                    break;
                case EX_LocalVariable localVariable:
                    AddVariableXref(result, functionData, instruction, localVariable.Variable);
                    break;
            }
        }

        return result;
    }

    private static void AddFinalFunctionXref(
        List<LowLevelXrefData> result,
        LowLevelFunctionData sourceFunction,
        IReadOnlyDictionary<int, LowLevelFunctionData> functionByExport,
        LowLevelInstructionData instruction,
        EX_FinalFunction expression)
    {
        var targetDisplay = expression.StackNode.Name;
        string? targetFunctionKey = null;
        if (expression.StackNode.IsExport)
        {
            var targetExport = expression.StackNode.Index - 1;
            if (functionByExport.TryGetValue(targetExport, out var targetFunction))
            {
                targetFunctionKey = targetFunction.Key;
                targetDisplay = targetFunction.Name;
            }
        }

        result.Add(new LowLevelXrefData(
            LowLevelXrefKind.CallFinal,
            sourceFunction.Key,
            sourceFunction.Name,
            instruction.Index,
            instruction.ScriptOffset,
            instruction.RawOffset,
            instruction.ParserOffset,
            "Function",
            expression.StackNode.ToString(),
            targetDisplay,
            targetFunctionKey,
            targetFunctionKey is not null,
            "Outgoing"));
    }

    private static void AddVirtualFunctionXref(List<LowLevelXrefData> result, LowLevelFunctionData sourceFunction, LowLevelInstructionData instruction, string functionName)
    {
        result.Add(new LowLevelXrefData(
            LowLevelXrefKind.CallVirtual,
            sourceFunction.Key,
            sourceFunction.Name,
            instruction.Index,
            instruction.ScriptOffset,
            instruction.RawOffset,
            instruction.ParserOffset,
            "VirtualFunction",
            functionName,
            functionName,
            null,
            false,
            "Outgoing"));
    }

    private static void AddObjectXref(List<LowLevelXrefData> result, LowLevelFunctionData sourceFunction, LowLevelInstructionData instruction, FPackageIndex packageIndex)
    {
        var targetDisplay = packageIndex.ResolvedObject?.Name.Text ?? packageIndex.Name;
        result.Add(new LowLevelXrefData(
            LowLevelXrefKind.ObjectRef,
            sourceFunction.Key,
            sourceFunction.Name,
            instruction.Index,
            instruction.ScriptOffset,
            instruction.RawOffset,
            instruction.ParserOffset,
            "Object",
            packageIndex.ToString(),
            targetDisplay,
            null,
            packageIndex.ResolvedObject is not null,
            "Outgoing"));
    }

    private static void AddVariableXref(List<LowLevelXrefData> result, LowLevelFunctionData sourceFunction, LowLevelInstructionData instruction, FKismetPropertyPointer pointer)
    {
        if (!pointer.bNew && pointer.Old is not null)
        {
            result.Add(new LowLevelXrefData(
                LowLevelXrefKind.PropertyRef,
                sourceFunction.Key,
                sourceFunction.Name,
                instruction.Index,
                instruction.ScriptOffset,
                instruction.RawOffset,
                instruction.ParserOffset,
                "Property",
                pointer.Old.ToString(),
                pointer.Old.Name,
                null,
                pointer.Old.ResolvedObject is not null,
                "Outgoing"));
            return;
        }

        if (pointer.New is { Path.Length: > 0 } fieldPath)
        {
            var name = fieldPath.Path[0].Text;
            result.Add(new LowLevelXrefData(
                LowLevelXrefKind.FieldPathRef,
                sourceFunction.Key,
                sourceFunction.Name,
                instruction.Index,
                instruction.ScriptOffset,
                instruction.RawOffset,
                instruction.ParserOffset,
                "FieldPath",
                name,
                name,
                null,
                true,
                "Outgoing"));
        }
    }

    private static (List<LowLevelBasicBlockData> Blocks, List<LowLevelCfgEdgeData> Edges, Dictionary<int, int> BlockLookup) BuildControlFlowGraph(UFunction function)
    {
        var blocks = new List<LowLevelBasicBlockData>();
        var edges = new List<LowLevelCfgEdgeData>();
        var blockLookup = new Dictionary<int, int>();
        if (function.ScriptBytecode is null || function.ScriptBytecode.Length == 0)
            return (blocks, edges, blockLookup);

        var instructions = function.ScriptBytecode;
        var offsetToIndex = new Dictionary<int, int>(instructions.Length);
        for (var i = 0; i < instructions.Length; i++)
        {
            offsetToIndex[instructions[i].StatementIndex] = i;
        }

        var leaders = new HashSet<int> { instructions[0].StatementIndex };
        for (var i = 0; i < instructions.Length; i++)
        {
            var expression = instructions[i];
            var nextIndex = i + 1;
            if (nextIndex < instructions.Length)
            {
                var token = expression.Token;
                if (token == EExprToken.EX_JumpIfNot ||
                    token == EExprToken.EX_Jump ||
                    token == EExprToken.EX_PushExecutionFlow)
                {
                    leaders.Add(instructions[nextIndex].StatementIndex);
                }
            }

            if (TryGetJumpTargetScriptOffset(expression) is { } jumpTarget && offsetToIndex.ContainsKey(jumpTarget))
                leaders.Add(jumpTarget);

            if (expression.Token == EExprToken.EX_PushExecutionFlow &&
                expression is EX_PushExecutionFlow push &&
                offsetToIndex.ContainsKey((int) push.PushingAddress))
            {
                leaders.Add((int) push.PushingAddress);
            }
        }

        var sortedLeaders = leaders.OrderBy(x => x).ToList();
        for (var i = 0; i < sortedLeaders.Count; i++)
        {
            var startOffset = sortedLeaders[i];
            if (!offsetToIndex.TryGetValue(startOffset, out var startInstructionIndex))
                continue;

            var endInstructionIndex = i + 1 < sortedLeaders.Count && offsetToIndex.TryGetValue(sortedLeaders[i + 1], out var nextInstructionIndex)
                ? nextInstructionIndex - 1
                : instructions.Length - 1;
            var endOffset = instructions[endInstructionIndex].StatementIndex;

            var block = new LowLevelBasicBlockData(
                i,
                startOffset,
                endOffset,
                startInstructionIndex,
                endInstructionIndex,
                $"{instructions[startInstructionIndex].Token} .. {instructions[endInstructionIndex].Token}");
            blocks.Add(block);

            for (var instructionIndex = startInstructionIndex; instructionIndex <= endInstructionIndex; instructionIndex++)
                blockLookup[instructions[instructionIndex].StatementIndex] = block.Id;
        }

        var blockById = blocks.ToDictionary(x => x.Id);
        foreach (var block in blocks)
        {
            var lastInstruction = instructions[block.LastInstructionIndex];
            switch (lastInstruction.Token)
            {
                case EExprToken.EX_JumpIfNot when lastInstruction is EX_JumpIfNot jumpIfNot:
                    AddEdgeToScriptOffset(edges, blockById, block.Id, (int) jumpIfNot.CodeOffset, LowLevelCfgEdgeKind.ConditionalFalse, "if-not");
                    AddFallthroughEdge(edges, blockById, block, LowLevelCfgEdgeKind.ConditionalTrue, "if-true");
                    break;
                case EExprToken.EX_Jump when lastInstruction is EX_Jump jump:
                    AddEdgeToScriptOffset(edges, blockById, block.Id, (int) jump.CodeOffset, LowLevelCfgEdgeKind.Jump, "jump");
                    break;
                case EExprToken.EX_PushExecutionFlow when lastInstruction is EX_PushExecutionFlow push:
                    AddEdgeToScriptOffset(edges, blockById, block.Id, (int) push.PushingAddress, LowLevelCfgEdgeKind.ExecutionFlow, "exec-push");
                    AddFallthroughEdge(edges, blockById, block, LowLevelCfgEdgeKind.Fallthrough, "fallthrough");
                    break;
                default:
                    AddFallthroughEdge(edges, blockById, block, LowLevelCfgEdgeKind.Fallthrough, "fallthrough");
                    break;
            }
        }

        return (blocks, edges, blockLookup);
    }

    private static void AddFallthroughEdge(
        List<LowLevelCfgEdgeData> edges,
        IReadOnlyDictionary<int, LowLevelBasicBlockData> blockById,
        LowLevelBasicBlockData sourceBlock,
        LowLevelCfgEdgeKind edgeKind,
        string label)
    {
        var nextBlockId = sourceBlock.Id + 1;
        if (!blockById.ContainsKey(nextBlockId))
            return;

        edges.Add(new LowLevelCfgEdgeData(sourceBlock.Id, nextBlockId, edgeKind, label));
    }

    private static void AddEdgeToScriptOffset(
        List<LowLevelCfgEdgeData> edges,
        IReadOnlyDictionary<int, LowLevelBasicBlockData> blockById,
        int fromBlockId,
        int targetScriptOffset,
        LowLevelCfgEdgeKind edgeKind,
        string label)
    {
        foreach (var block in blockById.Values)
        {
            if (targetScriptOffset < block.StartScriptOffset || targetScriptOffset > block.EndScriptOffset)
                continue;

            edges.Add(new LowLevelCfgEdgeData(fromBlockId, block.Id, edgeKind, label));
            break;
        }
    }

    private static int? TryGetJumpTargetScriptOffset(KismetExpression expression)
    {
        return expression switch
        {
            EX_JumpIfNot jumpIfNot => (int) jumpIfNot.CodeOffset,
            EX_Jump jump => (int) jump.CodeOffset,
            EX_SkipOffsetConst skipOffset => (int) skipOffset.Value,
            EX_PushExecutionFlow pushExecutionFlow => (int) pushExecutionFlow.PushingAddress,
            _ => null
        };
    }

    private static List<LowLevelHexRowData> BuildFunctionHexRows(IPackage package, int exportIndex, byte[]? combinedPackageBytes)
    {
        if (combinedPackageBytes is null || !TryGetExportRange(package, exportIndex, out var range))
            return [];
        if (range.RawStart < 0 || range.Size <= 0)
            return [];

        long endOffset;
        try
        {
            endOffset = checked(range.RawStart + range.Size);
        }
        catch (OverflowException)
        {
            return [];
        }

        if (range.RawStart >= combinedPackageBytes.LongLength || endOffset > combinedPackageBytes.LongLength)
            return [];

        return BuildHexRows(combinedPackageBytes, range.RawStart, range.Size);
    }

    private static List<LowLevelHexRowData> BuildHexRows(byte[] data, long startOffset, long size)
    {
        var rows = new List<LowLevelHexRowData>();
        const int rowWidth = 16;
        for (long cursor = 0; cursor < size; cursor += rowWidth)
        {
            var rowOffset = startOffset + cursor;
            var remaining = size - cursor;
            var rowLength = (int) Math.Min(rowWidth, remaining);
            var absoluteIndex = (int) rowOffset;
            var hexPart = FormatHexGroup(data, absoluteIndex, rowLength);
            var asciiPart = FormatAsciiGroup(data, absoluteIndex, rowLength);
            rows.Add(new LowLevelHexRowData(rowOffset, hexPart, asciiPart));
        }

        return rows;
    }

    private static IReadOnlyList<LowLevelExportMapEntryData> BuildExportMapEntries(IPackage package)
    {
        var result = new List<LowLevelExportMapEntryData>();
        switch (package)
        {
            case Package regularPackage:
                for (var i = 0; i < regularPackage.ExportMap.Length; i++)
                {
                    var entry = regularPackage.ExportMap[i];
                    var flags = ((EObjectFlags) entry.ObjectFlags).ToStringBitfield();
                    result.Add(new LowLevelExportMapEntryData(
                        i,
                        entry.ObjectName.Text,
                        entry.ClassIndex.Name,
                        entry.OuterIndex?.Name ?? "None",
                        entry.SerialOffset,
                        entry.SerialOffset,
                        entry.SerialSize,
                        flags));
                }
                break;
            case IoPackage ioPackage:
                for (var i = 0; i < ioPackage.ExportMap.Length; i++)
                {
                    var entry = ioPackage.ExportMap[i];
                    var objectName = ioPackage.CreateFNameFromMappedName(entry.ObjectName).Text;
                    var className = ioPackage.ResolveObjectIndex(entry.ClassIndex)?.Name.Text ?? entry.ClassIndex.Type.ToString();
                    var outerName = ioPackage.ResolveObjectIndex(entry.OuterIndex)?.Name.Text ?? "None";
                    var rawOffset = ioPackage.ExportRawSerialOffsets.Length > i ? ioPackage.ExportRawSerialOffsets[i] : -1;
                    var parserOffset = ioPackage.ExportParserSerialOffsets.Length > i ? ioPackage.ExportParserSerialOffsets[i] : -1;
                    result.Add(new LowLevelExportMapEntryData(
                        i,
                        objectName,
                        className,
                        outerName,
                        rawOffset,
                        parserOffset,
                        (long) entry.CookedSerialSize,
                        entry.ObjectFlags.ToStringBitfield()));
                }
                break;
        }

        return result;
    }

    private static IReadOnlyList<LowLevelImportMapEntryData> BuildImportMapEntries(IPackage package)
    {
        var result = new List<LowLevelImportMapEntryData>();
        switch (package)
        {
            case Package regularPackage:
                for (var i = 0; i < regularPackage.ImportMap.Length; i++)
                {
                    var entry = regularPackage.ImportMap[i];
                    result.Add(new LowLevelImportMapEntryData(
                        i,
                        entry.ObjectName.Text,
                        entry.ClassName.Text,
                        entry.ClassPackage.Text,
                        entry.OuterIndex?.Name ?? "None",
                        entry.PackageName.Text));
                }
                break;
            case IoPackage ioPackage:
                for (var i = 0; i < ioPackage.ImportMap.Length; i++)
                {
                    var entry = ioPackage.ImportMap[i];
                    var resolved = ioPackage.ResolveObjectIndex(entry);
                    var objectName = resolved?.Name.Text ?? "<unresolved>";
                    var className = resolved?.Class?.Name.Text ?? entry.Type.ToString();
                    var packageName = resolved?.Outer?.Name.Text ?? "None";
                    result.Add(new LowLevelImportMapEntryData(
                        i,
                        objectName,
                        className,
                        entry.Type.ToString(),
                        resolved?.Outer?.Name.Text ?? "None",
                        packageName));
                }
                break;
        }

        return result;
    }

    private static IReadOnlyList<LowLevelNameMapEntryData> BuildNameMapEntries(
        IPackage package,
        IReadOnlyList<LowLevelExportMapEntryData> exportMapEntries,
        IReadOnlyList<LowLevelImportMapEntryData> importMapEntries,
        IReadOnlyList<LowLevelClassData> classes)
    {
        var usage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var export in exportMapEntries)
        {
            IncrementUsage(usage, export.ObjectName);
            IncrementUsage(usage, export.ClassName);
            IncrementUsage(usage, export.OuterName);
        }

        foreach (var import in importMapEntries)
        {
            IncrementUsage(usage, import.ObjectName);
            IncrementUsage(usage, import.ClassName);
            IncrementUsage(usage, import.OuterName);
            IncrementUsage(usage, import.PackageName);
        }

        foreach (var function in classes.SelectMany(x => x.Functions))
        {
            IncrementUsage(usage, function.Name);
            foreach (var xref in function.OutgoingXrefs)
                IncrementUsage(usage, xref.TargetDisplay);
        }

        var names = package.NameMap ?? [];
        var result = new List<LowLevelNameMapEntryData>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i].Name ?? "None";
            usage.TryGetValue(name, out var count);
            result.Add(new LowLevelNameMapEntryData(i, name, count));
        }

        return result;
    }

    private static void IncrementUsage(Dictionary<string, int> usage, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!usage.TryAdd(value, 1))
            usage[value]++;
    }

    private readonly struct ExportRangeInfo
    {
        public readonly long RawStart;
        public readonly long ParserStart;
        public readonly long Size;

        public ExportRangeInfo(long rawStart, long parserStart, long size)
        {
            RawStart = rawStart;
            ParserStart = parserStart;
            Size = size;
        }
    }

    private static bool TryGetExportRange(IPackage package, int exportIndex, out ExportRangeInfo range)
    {
        range = default;
        if (exportIndex < 0)
            return false;

        switch (package)
        {
            case Package regularPackage:
                if (exportIndex >= regularPackage.ExportMap.Length)
                    return false;
                var regularExport = regularPackage.ExportMap[exportIndex];
                if (regularExport.SerialOffset < 0 || regularExport.SerialSize <= 0)
                    return false;
                range = new ExportRangeInfo(regularExport.SerialOffset, regularExport.SerialOffset, regularExport.SerialSize);
                return true;
            case IoPackage ioPackage:
                if (exportIndex >= ioPackage.ExportMap.Length)
                    return false;
                var ioExport = ioPackage.ExportMap[exportIndex];
                if (ioExport.CookedSerialSize == 0 || ioExport.CookedSerialSize > long.MaxValue)
                    return false;

                var rawStart = ioPackage.ExportRawSerialOffsets.Length > exportIndex ? ioPackage.ExportRawSerialOffsets[exportIndex] : -1;
                var parserStart = ioPackage.ExportParserSerialOffsets.Length > exportIndex ? ioPackage.ExportParserSerialOffsets[exportIndex] : -1;
                if (rawStart < 0 || parserStart < 0)
                {
                    if (ioExport.CookedSerialOffset > long.MaxValue)
                        return false;

                    rawStart = (long) ioExport.CookedSerialOffset;
                    parserStart = rawStart;
                }

                range = new ExportRangeInfo(rawStart, parserStart, (long) ioExport.CookedSerialSize);
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertParserOffsetToRaw(IPackage package, int exportIndex, long parserOffset, out long rawOffset)
    {
        rawOffset = parserOffset;
        if (!TryGetExportRange(package, exportIndex, out var range))
            return false;

        long delta;
        try
        {
            delta = checked(parserOffset - range.ParserStart);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (delta < 0 || delta > range.Size)
            return false;

        try
        {
            rawOffset = checked(range.RawStart + delta);
            return true;
        }
        catch (OverflowException)
        {
            rawOffset = parserOffset;
            return false;
        }
    }

    private static void AppendLowLevelFunctions(StringBuilder sb, IPackage package, UClass blueprint)
    {
        sb.AppendLine("Functions:");
        if (blueprint.FuncMap.Count == 0)
        {
            sb.AppendLine("  // None");
            sb.AppendLine();
            return;
        }

        foreach (var (name, pointer) in blueprint.FuncMap)
        {
            sb.AppendLine($"  Function {name.Text}");
            var functionExportIndex = pointer.Index - 1;
            if (!pointer.TryLoad(out var export) || export is not UFunction function)
            {
                sb.AppendLine("    // Failed to load function export.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"    // Flags: {function.FunctionFlags.ToStringBitfield()}");
            if (TryConvertParserOffsetToRaw(package, functionExportIndex, function.ScriptBytecodeAssetOffset, out var scriptRawOffset))
            {
                if (scriptRawOffset != function.ScriptBytecodeAssetOffset)
                    sb.AppendLine($"    // ScriptOffset: raw 0x{scriptRawOffset:X8}, parser 0x{function.ScriptBytecodeAssetOffset:X8}, ScriptSize: 0x{function.ScriptBytecodeSerializedSize:X}");
                else
                    sb.AppendLine($"    // ScriptOffset: 0x{scriptRawOffset:X8}, ScriptSize: 0x{function.ScriptBytecodeSerializedSize:X}");
            }
            else
            {
                sb.AppendLine($"    // ScriptOffset: parser 0x{function.ScriptBytecodeAssetOffset:X8}, ScriptSize: 0x{function.ScriptBytecodeSerializedSize:X} (raw offset unavailable)");
            }

            if (function.ScriptBytecode is null || function.ScriptBytecode.Length == 0)
            {
                sb.AppendLine("    // No parsed script bytecode. Enable `Serialize Script Bytecode` in settings.");
                sb.AppendLine();
                continue;
            }

            if (function.ScriptBytecodeRaw is null || function.ScriptBytecodeRaw.Length == 0)
            {
                sb.AppendLine("    // Raw script bytes are unavailable.");
                sb.AppendLine();
                continue;
            }

            BlueprintDecompilerUtils.Function = function;
            var jumpLabels = CollectJumpLabelOffsets(function);
            for (var i = 0; i < function.ScriptBytecode.Length; i++)
            {
                var expression = function.ScriptBytecode[i];
                if (jumpLabels.Contains(expression.StatementIndex))
                {
                    sb.AppendLine($"    Label_0x{expression.StatementIndex:X4}:");
                }

                sb.AppendLine(FormatLowLevelInstructionLine(package, functionExportIndex, function, expression, i));
            }

            sb.AppendLine();
        }
    }

    private static HashSet<int> CollectJumpLabelOffsets(UFunction function)
    {
        var offsets = new HashSet<int>();
        if (function.ScriptBytecode is null) return offsets;

        foreach (var expression in function.ScriptBytecode)
        {
            switch (expression.Token)
            {
                case EExprToken.EX_JumpIfNot when expression is EX_JumpIfNot jumpIfNot:
                    offsets.Add((int) jumpIfNot.CodeOffset);
                    break;
                case EExprToken.EX_Jump when expression is EX_Jump jump:
                    offsets.Add((int) jump.CodeOffset);
                    break;
                case EExprToken.EX_SkipOffsetConst when expression is EX_SkipOffsetConst skipOffset:
                    offsets.Add((int) skipOffset.Value);
                    break;
            }
        }

        return offsets;
    }

    private static string FormatLowLevelInstructionLine(IPackage package, int exportIndex, UFunction function, KismetExpression expression, int expressionIndex)
    {
        var scriptOffset = expression.StatementIndex;
        var parserAssetOffset = function.ScriptBytecodeAssetOffset + scriptOffset;
        var (rawByteStart, length, rangeSource, rangeValid) = GetInstructionRawRange(function, expression, expressionIndex);
        var hasScriptRawBase = TryConvertParserOffsetToRaw(package, exportIndex, function.ScriptBytecodeAssetOffset, out var scriptRawBaseOffset);
        var hasRawOffset = TryGetInstructionAssetRawOffset(package, exportIndex, function, parserAssetOffset, rawByteStart, hasScriptRawBase, scriptRawBaseOffset, out var rawAssetOffset);
        var bytes = FormatInstructionBytes(function.ScriptBytecodeRaw, rawByteStart, length);
        var decoded = TryDecodeExpression(expression);
        var assetOffsetDisplay = hasRawOffset
            ? rawAssetOffset == parserAssetOffset
                ? $"raw 0x{rawAssetOffset:X8}"
                : $"raw 0x{rawAssetOffset:X8} | parser 0x{parserAssetOffset:X8}"
            : $"parser 0x{parserAssetOffset:X8}";
        var rangeInfo = rangeSource == LowLevelRawRangeSource.ParserCaptured
            ? rangeValid ? string.Empty : " [bytes-invalid]"
            : rangeValid ? " [bytes-fallback]" : " [bytes-fallback-invalid]";
        return $"    [{assetOffsetDisplay} | script 0x{scriptOffset:X4}] {expression.Token,-28} {bytes,-47} ; {decoded}{rangeInfo}";
    }

    private static (int Start, int Length, LowLevelRawRangeSource Source, bool Valid) GetInstructionRawRange(UFunction function, KismetExpression expression, int expressionIndex)
    {
        if (TryGetCapturedRawRange(function.ScriptBytecodeRaw, expression, out var capturedStart, out var capturedLength) &&
            IsTokenByteMatch(function.ScriptBytecodeRaw, capturedStart, expression.Token))
            return (capturedStart, capturedLength, LowLevelRawRangeSource.ParserCaptured, true);

        var start = expression.StatementIndex;
        var length = GetInstructionLengthFallback(function, expressionIndex);
        if (function.ScriptBytecodeRaw is null || function.ScriptBytecodeRaw.Length == 0)
            return (start, length, LowLevelRawRangeSource.FallbackDerived, false);

        if (start < 0 || start >= function.ScriptBytecodeRaw.Length)
            return (start, 0, LowLevelRawRangeSource.FallbackDerived, false);

        if (length <= 0)
            length = 1;

        var available = function.ScriptBytecodeRaw.Length - start;
        var clampedLength = Math.Min(length, available);
        var valid = clampedLength > 0 && IsTokenByteMatch(function.ScriptBytecodeRaw, start, expression.Token);
        return (start, clampedLength, LowLevelRawRangeSource.FallbackDerived, valid);
    }

    private static bool TryGetInstructionAssetRawOffset(
        IPackage package,
        int exportIndex,
        UFunction function,
        long parserOffset,
        int rawByteStart,
        bool hasScriptRawBase,
        long scriptRawBaseOffset,
        out long rawOffset)
    {
        rawOffset = parserOffset;

        if (hasScriptRawBase &&
            function.ScriptBytecodeRaw is { Length: > 0 } rawScript &&
            rawByteStart >= 0 &&
            rawByteStart < rawScript.Length)
        {
            rawOffset = scriptRawBaseOffset + rawByteStart;
            return true;
        }

        return TryConvertParserOffsetToRaw(package, exportIndex, parserOffset, out rawOffset);
    }

    private static bool IsTokenByteMatch(byte[]? rawBytes, int start, EExprToken token)
    {
        if (rawBytes is null || start < 0 || start >= rawBytes.Length)
            return false;

        return rawBytes[start] == (byte) token;
    }

    private static bool TryGetCapturedRawRange(byte[]? rawBytes, KismetExpression expression, out int start, out int length)
    {
        start = expression.RawStartIndex;
        length = 0;
        if (rawBytes is null || rawBytes.Length == 0)
            return false;

        var end = expression.RawEndIndex;
        if (start < 0 || end <= start || end > rawBytes.Length)
            return false;

        length = end - start;
        return length > 0;
    }

    private static int GetInstructionLengthFallback(UFunction function, int expressionIndex)
    {
        if (function.ScriptBytecode is null || function.ScriptBytecode.Length == 0)
            return 0;

        var currentOffset = function.ScriptBytecode[expressionIndex].StatementIndex;
        var nextOffset = expressionIndex + 1 < function.ScriptBytecode.Length
            ? function.ScriptBytecode[expressionIndex + 1].StatementIndex
            : function.ScriptBytecodeSerializedSize;

        var length = nextOffset - currentOffset;
        if (length > 0)
            return length;

        return currentOffset < function.ScriptBytecodeSerializedSize ? 1 : 0;
    }

    private static string FormatInstructionBytes(byte[]? rawBytes, int offset, int length)
    {
        if (rawBytes is null || length <= 0 || offset < 0 || offset >= rawBytes.Length)
            return "<no-bytes>";

        var available = Math.Min(length, rawBytes.Length - offset);
        var bytes = new StringBuilder(available * 3);
        for (var i = 0; i < available; i++)
        {
            if (i > 0) bytes.Append(' ');
            bytes.Append(rawBytes[offset + i].ToString("X2"));
        }

        return bytes.ToString();
    }

    private static string TryDecodeExpression(KismetExpression expression)
    {
        try
        {
            var decoded = BlueprintDecompilerUtils.GetLineExpression(expression);
            if (string.IsNullOrWhiteSpace(decoded))
                return "<empty>";

            return TruncateForDisplay(ToSingleLine(decoded), 240);
        }
        catch (Exception e)
        {
            return TruncateForDisplay(ToSingleLine($"<decode-error: {e.Message}>"), 240);
        }
    }

    private static LowLevelOpcodeCategory GetOpcodeCategory(EExprToken token)
    {
        return token switch
        {
            EExprToken.EX_Return or
            EExprToken.EX_Jump or
            EExprToken.EX_JumpIfNot or
            EExprToken.EX_Skip or
            EExprToken.EX_PushExecutionFlow or
            EExprToken.EX_PopExecutionFlow or
            EExprToken.EX_PopExecutionFlowIfNot or
            EExprToken.EX_ComputedJump or
            EExprToken.EX_SwitchValue or
            EExprToken.EX_SkipOffsetConst => LowLevelOpcodeCategory.Flow,

            EExprToken.EX_VirtualFunction or
            EExprToken.EX_FinalFunction or
            EExprToken.EX_LocalVirtualFunction or
            EExprToken.EX_LocalFinalFunction or
            EExprToken.EX_CallMath or
            EExprToken.EX_CallMulticastDelegate => LowLevelOpcodeCategory.Call,

            EExprToken.EX_LocalVariable or
            EExprToken.EX_InstanceVariable or
            EExprToken.EX_DefaultVariable or
            EExprToken.EX_LocalOutVariable or
            EExprToken.EX_Let or
            EExprToken.EX_LetBool or
            EExprToken.EX_LetObj or
            EExprToken.EX_LetWeakObjPtr or
            EExprToken.EX_LetValueOnPersistentFrame or
            EExprToken.EX_ClassSparseDataVariable => LowLevelOpcodeCategory.Variable,

            EExprToken.EX_IntConst or
            EExprToken.EX_FloatConst or
            EExprToken.EX_StringConst or
            EExprToken.EX_ObjectConst or
            EExprToken.EX_NameConst or
            EExprToken.EX_RotationConst or
            EExprToken.EX_VectorConst or
            EExprToken.EX_ByteConst or
            EExprToken.EX_IntZero or
            EExprToken.EX_IntOne or
            EExprToken.EX_True or
            EExprToken.EX_False or
            EExprToken.EX_TextConst or
            EExprToken.EX_NoObject or
            EExprToken.EX_TransformConst or
            EExprToken.EX_IntConstByte or
            EExprToken.EX_NoInterface or
            EExprToken.EX_StructConst or
            EExprToken.EX_PropertyConst or
            EExprToken.EX_UnicodeStringConst or
            EExprToken.EX_Int64Const or
            EExprToken.EX_UInt64Const or
            EExprToken.EX_DoubleConst or
            EExprToken.EX_MapConst or
            EExprToken.EX_SetConst or
            EExprToken.EX_Vector3fConst or
            EExprToken.EX_SoftObjectConst or
            EExprToken.EX_FieldPathConst or
            EExprToken.EX_BitFieldConst => LowLevelOpcodeCategory.Constant,

            EExprToken.EX_SetArray or
            EExprToken.EX_SetSet or
            EExprToken.EX_SetMap or
            EExprToken.EX_ArrayConst or
            EExprToken.EX_ArrayGetByRef => LowLevelOpcodeCategory.Container,

            EExprToken.EX_BindDelegate or
            EExprToken.EX_InstanceDelegate or
            EExprToken.EX_AddMulticastDelegate or
            EExprToken.EX_RemoveMulticastDelegate or
            EExprToken.EX_ClearMulticastDelegate or
            EExprToken.EX_LetDelegate or
            EExprToken.EX_LetMulticastDelegate => LowLevelOpcodeCategory.Delegate,

            EExprToken.EX_Cast or
            EExprToken.EX_DynamicCast or
            EExprToken.EX_MetaCast or
            EExprToken.EX_ObjToInterfaceCast or
            EExprToken.EX_CrossInterfaceCast or
            EExprToken.EX_InterfaceToObjCast => LowLevelOpcodeCategory.Cast,

            EExprToken.EX_Context or
            EExprToken.EX_Context_FailSilent or
            EExprToken.EX_ClassContext or
            EExprToken.EX_InterfaceContext or
            EExprToken.EX_StructMemberContext => LowLevelOpcodeCategory.Context,

            EExprToken.EX_InstrumentationEvent or
            EExprToken.EX_Tracepoint or
            EExprToken.EX_WireTracepoint or
            EExprToken.EX_Breakpoint or
            EExprToken.EX_Assert => LowLevelOpcodeCategory.Instrumentation,

            EExprToken.EX_AutoRtfmTransact or
            EExprToken.EX_AutoRtfmStopTransact or
            EExprToken.EX_AutoRtfmAbortIfNot => LowLevelOpcodeCategory.Transaction,

            _ => LowLevelOpcodeCategory.Other
        };
    }

    private static IReadOnlyList<LowLevelOperandNodeData> BuildOperandTree(byte[]? rawBytes, KismetExpression expression, int instructionStart, int instructionEnd)
    {
        var safeStart = Math.Max(0, instructionStart);
        var safeEnd = Math.Max(safeStart, instructionEnd);
        if (TryBuildTraceOperandTree(rawBytes, expression, safeStart, safeEnd, out var tracedNodes))
            return tracedNodes;

        var nodes = new List<MutableOperandNode>();
        if (safeEnd > safeStart)
        {
            nodes.Add(new MutableOperandNode(
                "opcode",
                "byte",
                $"0x{(byte) expression.Token:X2} ({expression.Token})",
                FormatInstructionBytes(rawBytes, safeStart, 1),
                safeStart,
                safeStart + 1,
                LowLevelOperandConfidence.Exact,
                typeof(byte),
                (byte) expression.Token,
                false,
                kind: "Opcode"));
        }

        var payloadStart = Math.Min(safeStart + 1, safeEnd);
        nodes.AddRange(BuildExpressionMembers(expression, rawBytes, payloadStart, safeEnd));
        return nodes.Select(ToOperandNodeData).ToList();
    }

    private static bool TryBuildTraceOperandTree(
        byte[]? rawBytes,
        KismetExpression expression,
        int instructionStart,
        int instructionEnd,
        out IReadOnlyList<LowLevelOperandNodeData> nodes)
    {
        nodes = [];
        var traceEntries = expression.TraceEntries;
        if (traceEntries is null || traceEntries.Count == 0)
            return false;

        var sorted = traceEntries
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();
        if (sorted.Count == 0)
            return false;

        var nestedNameMap = BuildNestedExpressionNameMap(expression);
        var scalarFields = BuildScalarFieldList(expression);
        var scalarTraceEntries = new List<KismetReadTraceEntry>();
        var tracedNodes = new List<MutableOperandNode>();

        foreach (var entry in sorted)
        {
            switch (entry.Kind)
            {
                case KismetReadTraceKind.Opcode:
                    tracedNodes.Add(new MutableOperandNode(
                        "opcode",
                        "byte",
                        entry.ValuePreview ?? $"0x{(byte) expression.Token:X2}",
                        FormatInstructionBytes(rawBytes, entry.Start, entry.Length),
                        entry.Start,
                        entry.End,
                        LowLevelOperandConfidence.Exact,
                        typeof(byte),
                        (byte) expression.Token,
                        false,
                        kind: "Opcode"));
                    break;
                case KismetReadTraceKind.NestedExpression when entry.NestedExpression is not null:
                    var nestedExpression = entry.NestedExpression;
                    var nestedName = nestedNameMap.TryGetValue(nestedExpression, out var fieldName)
                        ? fieldName
                        : $"expr_{nestedExpression.Token}";
                    tracedNodes.Add(new MutableOperandNode(
                        nestedName,
                        nestedExpression.Token.ToString(),
                        $"{nestedExpression.Token} @0x{nestedExpression.StatementIndex:X4}",
                        FormatInstructionBytes(rawBytes, entry.Start, entry.Length),
                        entry.Start,
                        entry.End,
                        LowLevelOperandConfidence.Exact,
                        nestedExpression.GetType(),
                        nestedExpression,
                        false,
                        BuildOperandTree(rawBytes, nestedExpression, nestedExpression.RawStartIndex, nestedExpression.RawEndIndex)
                            .Select(ToMutableOperandNode)
                            .ToList(),
                        kind: "NestedExpression"));
                    break;
                default:
                    scalarTraceEntries.Add(entry);
                    break;
            }
        }

        tracedNodes.AddRange(BuildScalarNodesFromTrace(rawBytes, scalarTraceEntries, scalarFields));
        AppendUnknownCoverageNodes(tracedNodes, rawBytes, instructionStart, instructionEnd);
        tracedNodes = tracedNodes
            .Where(x => x.Start >= 0 && x.End > x.Start)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();
        nodes = tracedNodes.Select(ToOperandNodeData).ToList();
        return nodes.Count > 0;
    }

    private static Dictionary<KismetExpression, string> BuildNestedExpressionNameMap(KismetExpression expression)
    {
        var result = new Dictionary<KismetExpression, string>();
        foreach (var field in GetExpressionFields(expression.GetType()))
        {
            object? memberValue;
            try
            {
                memberValue = field.GetValue(expression);
            }
            catch
            {
                memberValue = null;
            }

            if (memberValue is KismetExpression nested)
            {
                if (!result.ContainsKey(nested))
                    result[nested] = field.Name;
                continue;
            }

            if (!TryGetExpressionSequence(memberValue, out var nestedList))
                continue;
            for (var i = 0; i < nestedList.Count; i++)
            {
                var key = nestedList[i];
                if (!result.ContainsKey(key))
                    result[key] = $"{field.Name}[{i}]";
            }
        }

        return result;
    }

    private static List<ScalarFieldInfo> BuildScalarFieldList(KismetExpression expression)
    {
        var result = new List<ScalarFieldInfo>();
        foreach (var field in GetExpressionFields(expression.GetType()))
        {
            object? memberValue;
            try
            {
                memberValue = field.GetValue(expression);
            }
            catch
            {
                memberValue = null;
            }

            if (memberValue is KismetExpression)
                continue;
            if (TryGetExpressionSequence(memberValue, out _))
                continue;

            result.Add(new ScalarFieldInfo(field.Name, field.FieldType, memberValue));
        }

        return result;
    }

    private static List<MutableOperandNode> BuildScalarNodesFromTrace(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        IReadOnlyList<ScalarFieldInfo> scalarFields)
    {
        var result = new List<MutableOperandNode>();
        var traceIndex = 0;
        for (var fieldIndex = 0; fieldIndex < scalarFields.Count && traceIndex < traceEntries.Count; fieldIndex++)
        {
            var field = scalarFields[fieldIndex];
            var minimumEntriesForRemaining = 0;
            for (var i = fieldIndex + 1; i < scalarFields.Count; i++)
                minimumEntriesForRemaining += GetMinimumTraceEntryCountForField(scalarFields[i]);

            var remainingEntries = traceEntries.Count - traceIndex;
            var availableForField = Math.Max(1, remainingEntries - minimumEntriesForRemaining);
            if (TryBuildCompositeFieldNode(rawBytes, traceEntries, traceIndex, availableForField, field, out var compositeNode, out var consumed))
            {
                result.Add(compositeNode);
                traceIndex += consumed;
                continue;
            }

            if (IsStructuredField(field))
            {
                var failedEntry = traceEntries[traceIndex];
                traceIndex++;
                if (failedEntry.End > failedEntry.Start)
                {
                    result.Add(CreateUnknownNode(
                        rawBytes,
                        $"unknown_segment_{result.Count:D2}",
                        failedEntry.Start,
                        failedEntry.End,
                        $"Unable to decode {GetFriendlyTypeName(field.FieldType)} from trace"));
                }
                continue;
            }

            var entry = traceEntries[traceIndex];
            traceIndex++;
            if (entry.End <= entry.Start)
                continue;

            result.Add(new MutableOperandNode(
                field.Name,
                GetFriendlyTypeName(field.FieldType),
                FormatOperandValue(field.Value),
                FormatInstructionBytes(rawBytes, entry.Start, entry.Length),
                entry.Start,
                entry.End,
                LowLevelOperandConfidence.Exact,
                field.FieldType,
                field.Value,
                false,
                kind: GetTraceEntryKindLabel(entry.Kind)));
        }

        var unknownIndex = 0;
        while (traceIndex < traceEntries.Count)
        {
            var entry = traceEntries[traceIndex++];
            if (entry.End <= entry.Start)
                continue;
            result.Add(CreateUnknownNode(
                rawBytes,
                $"unknown_segment_{unknownIndex:D2}",
                entry.Start,
                entry.End,
                "Unmapped trace bytes"));
            unknownIndex++;
        }

        return result;
    }

    private static bool IsStructuredField(ScalarFieldInfo field)
    {
        return field.Value is FKismetPropertyPointer or FFieldPath or FName or FPackageIndex;
    }

    private static int GetMinimumTraceEntryCountForField(ScalarFieldInfo field)
    {
        if (field.Value is FKismetPropertyPointer pointer)
            return pointer.bNew && pointer.New is { } newPath ? 1 + newPath.Path.Length * 2 : 1;
        if (field.Value is FFieldPath fieldPath)
            return 1 + fieldPath.Path.Length * 2;
        if (field.Value is FName)
            return 2;

        return 1;
    }

    private static bool TryBuildCompositeFieldNode(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        int startIndex,
        int maxEntries,
        ScalarFieldInfo field,
        out MutableOperandNode node,
        out int consumed)
    {
        // Keep operand semantics deterministic: decode only from known parser structures.
        // If layout is ambiguous, caller will emit Unknown nodes instead of guessing.
        node = null!;
        consumed = 0;
        if (maxEntries <= 0 || startIndex < 0 || startIndex >= traceEntries.Count)
            return false;

        if (field.Value is FKismetPropertyPointer pointer)
            return TryBuildPropertyPointerNode(rawBytes, traceEntries, startIndex, maxEntries, field.Name, pointer, field.FieldType, out node, out consumed);
        if (field.Value is FFieldPath fieldPath)
            return TryBuildFieldPathNode(rawBytes, traceEntries, startIndex, maxEntries, field.Name, fieldPath, field.FieldType, out node, out consumed);
        if (field.Value is FName name)
            return TryBuildFNameNode(rawBytes, traceEntries, startIndex, maxEntries, field.Name, name, field.FieldType, out node, out consumed);
        if (field.Value is FPackageIndex packageIndex)
            return TryBuildPackageIndexNode(rawBytes, traceEntries, startIndex, maxEntries, field.Name, packageIndex, field.FieldType, out node, out consumed);

        return false;
    }

    private static bool TryBuildPackageIndexNode(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        int startIndex,
        int maxEntries,
        string fieldName,
        FPackageIndex packageIndex,
        Type declaredType,
        out MutableOperandNode node,
        out int consumed)
    {
        node = null!;
        consumed = 0;
        if (maxEntries < 1 || startIndex >= traceEntries.Count)
            return false;
        var entry = traceEntries[startIndex];
        if (entry.End <= entry.Start)
            return false;

        var children = new List<MutableOperandNode>
        {
            CreateTraceEntryNode(rawBytes, "Index", "Int32", entry.ValuePreview ?? packageIndex.Index.ToString(), entry)
        };
        node = new MutableOperandNode(
            fieldName,
            GetFriendlyTypeName(declaredType),
            packageIndex.ToString(),
            FormatInstructionBytes(rawBytes, entry.Start, entry.Length),
            entry.Start,
            entry.End,
            LowLevelOperandConfidence.Exact,
            declaredType,
            packageIndex,
            false,
            children,
            kind: "Composite");
        consumed = 1;
        return true;
    }

    private static bool TryBuildFNameNode(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        int startIndex,
        int maxEntries,
        string fieldName,
        FName name,
        Type declaredType,
        out MutableOperandNode node,
        out int consumed)
    {
        node = null!;
        consumed = 0;
        if (maxEntries < 2 || startIndex + 1 >= traceEntries.Count)
            return false;

        var indexEntry = traceEntries[startIndex];
        var numberEntry = traceEntries[startIndex + 1];
        if (indexEntry.End <= indexEntry.Start || numberEntry.End <= numberEntry.Start)
            return false;

        var children = new List<MutableOperandNode>
        {
            CreateTraceEntryNode(rawBytes, "NameIndex", "Int32", indexEntry.ValuePreview ?? name.Index.ToString(), indexEntry),
            CreateTraceEntryNode(rawBytes, "Number", "Int32", numberEntry.ValuePreview ?? name.Number.ToString(), numberEntry)
        };
        node = new MutableOperandNode(
            fieldName,
            GetFriendlyTypeName(declaredType),
            name.Text,
            FormatInstructionBytes(rawBytes, indexEntry.Start, numberEntry.End - indexEntry.Start),
            indexEntry.Start,
            numberEntry.End,
            LowLevelOperandConfidence.Exact,
            declaredType,
            name,
            false,
            children,
            kind: "Composite");
        consumed = 2;
        return true;
    }

    private static bool TryBuildPropertyPointerNode(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        int startIndex,
        int maxEntries,
        string fieldName,
        FKismetPropertyPointer pointer,
        Type declaredType,
        out MutableOperandNode node,
        out int consumed)
    {
        node = null!;
        consumed = 0;

        if (!pointer.bNew || pointer.New is null)
        {
            if (!TryBuildPackageIndexNode(rawBytes, traceEntries, startIndex, maxEntries, "Old", pointer.Old ?? new FPackageIndex(), typeof(FPackageIndex), out var oldNode, out var oldConsumed))
                return false;

            node = new MutableOperandNode(
                fieldName,
                GetFriendlyTypeName(declaredType),
                pointer.ToString(),
                oldNode.Bytes,
                oldNode.Start,
                oldNode.End,
                oldNode.Confidence,
                declaredType,
                pointer,
                false,
                [oldNode],
                kind: "Composite");
            consumed = oldConsumed;
            return true;
        }

        if (!TryBuildFieldPathNode(rawBytes, traceEntries, startIndex, maxEntries, "FieldPath", pointer.New, typeof(FFieldPath), out var pathNode, out var pathConsumed))
            return false;

        node = new MutableOperandNode(
            fieldName,
            GetFriendlyTypeName(declaredType),
            pointer.ToString(),
            pathNode.Bytes,
            pathNode.Start,
            pathNode.End,
            pathNode.Confidence,
            declaredType,
            pointer,
            false,
            [pathNode],
            kind: "Composite");
        consumed = pathConsumed;
        return true;
    }

    private static bool TryBuildFieldPathNode(
        byte[]? rawBytes,
        IReadOnlyList<KismetReadTraceEntry> traceEntries,
        int startIndex,
        int maxEntries,
        string fieldName,
        FFieldPath fieldPath,
        Type declaredType,
        out MutableOperandNode node,
        out int consumed)
    {
        node = null!;
        consumed = 0;
        if (startIndex >= traceEntries.Count || maxEntries <= 0)
            return false;

        var pathCount = fieldPath.Path.Length;
        var minimumEntries = 1 + pathCount * 2;
        if (maxEntries < minimumEntries || startIndex + minimumEntries - 1 >= traceEntries.Count)
            return false;

        var children = new List<MutableOperandNode>();
        var pathCountEntry = traceEntries[startIndex];
        if (pathCountEntry.End <= pathCountEntry.Start)
            return false;

        children.Add(CreateTraceEntryNode(rawBytes, "PathCount", "Int32", pathCountEntry.ValuePreview ?? pathCount.ToString(), pathCountEntry));
        var cursor = startIndex + 1;
        for (var i = 0; i < pathCount; i++)
        {
            if (cursor + 1 >= traceEntries.Count)
                return false;

            var nameIndexEntry = traceEntries[cursor];
            var nameNumberEntry = traceEntries[cursor + 1];
            if (nameIndexEntry.End <= nameIndexEntry.Start || nameNumberEntry.End <= nameNumberEntry.Start)
                return false;

            var pathChildren = new List<MutableOperandNode>
            {
                CreateTraceEntryNode(rawBytes, "NameIndex", "Int32", nameIndexEntry.ValuePreview ?? string.Empty, nameIndexEntry),
                CreateTraceEntryNode(rawBytes, "Number", "Int32", nameNumberEntry.ValuePreview ?? string.Empty, nameNumberEntry)
            };
            children.Add(new MutableOperandNode(
                $"Path[{i}]",
                "FName",
                i < fieldPath.Path.Length ? fieldPath.Path[i].Text : string.Empty,
                FormatInstructionBytes(rawBytes, nameIndexEntry.Start, nameNumberEntry.End - nameIndexEntry.Start),
                nameIndexEntry.Start,
                nameNumberEntry.End,
                LowLevelOperandConfidence.Exact,
                typeof(FName),
                i < fieldPath.Path.Length ? fieldPath.Path[i] : null,
                false,
                pathChildren,
                kind: "Composite"));
            cursor += 2;
        }

        var expectedEntries = minimumEntries;
        if (maxEntries > minimumEntries && cursor < traceEntries.Count)
        {
            var ownerEntry = traceEntries[cursor];
            if (ownerEntry.End > ownerEntry.Start)
            {
                children.Add(CreateTraceEntryNode(rawBytes, "Owner.Index", "Int32", ownerEntry.ValuePreview ?? string.Empty, ownerEntry));
                expectedEntries++;
                cursor++;
            }
        }

        var start = pathCountEntry.Start;
        var end = children.Count > 0 ? children.Max(x => x.End) : pathCountEntry.End;
        node = new MutableOperandNode(
            fieldName,
            GetFriendlyTypeName(declaredType),
            fieldPath.ToString(),
            FormatInstructionBytes(rawBytes, start, end - start),
            start,
            end,
            LowLevelOperandConfidence.Exact,
            declaredType,
            fieldPath,
            false,
            children,
            kind: "Composite");
        consumed = expectedEntries;
        return true;
    }

    private static MutableOperandNode CreateTraceEntryNode(
        byte[]? rawBytes,
        string name,
        string type,
        string value,
        KismetReadTraceEntry entry)
    {
        return new MutableOperandNode(
            name,
            type,
            value,
            FormatInstructionBytes(rawBytes, entry.Start, entry.Length),
            entry.Start,
            entry.End,
            LowLevelOperandConfidence.Exact,
            typeof(object),
            null,
            false,
            kind: GetTraceEntryKindLabel(entry.Kind));
    }

    private static string GetTraceEntryKindLabel(KismetReadTraceKind kind)
    {
        return kind switch
        {
            KismetReadTraceKind.Primitive => "Primitive",
            KismetReadTraceKind.Bytes => "Bytes",
            KismetReadTraceKind.String => "String",
            KismetReadTraceKind.Opcode => "Opcode",
            KismetReadTraceKind.NestedExpression => "NestedExpression",
            _ => "Field"
        };
    }

    private static void AppendUnknownCoverageNodes(List<MutableOperandNode> nodes, byte[]? rawBytes, int instructionStart, int instructionEnd)
    {
        if (instructionEnd <= instructionStart)
            return;

        var gaps = BuildGaps(instructionStart, instructionEnd, nodes);
        var unknownIndex = nodes.Count(x => x.Kind == "Unknown");
        foreach (var gap in gaps)
        {
            if (gap.End <= gap.Start)
                continue;
            nodes.Add(CreateUnknownNode(rawBytes, $"unknown_segment_{unknownIndex:D2}", gap.Start, gap.End, "Unmapped instruction bytes"));
            unknownIndex++;
        }
    }

    private static MutableOperandNode CreateUnknownNode(byte[]? rawBytes, string name, int start, int end, string reason)
    {
        return new MutableOperandNode(
            name,
            "bytes",
            reason,
            FormatInstructionBytes(rawBytes, start, end - start),
            start,
            end,
            LowLevelOperandConfidence.Fallback,
            typeof(byte[]),
            null,
            false,
            kind: "Unknown",
            reason: reason);
    }

    private static List<MutableOperandNode> BuildExpressionMembers(KismetExpression expression, byte[]? rawBytes, int payloadStart, int payloadEnd)
    {
        var nodes = new List<MutableOperandNode>();
        foreach (var field in GetExpressionFields(expression.GetType()))
        {
            object? memberValue;
            try
            {
                memberValue = field.GetValue(expression);
            }
            catch
            {
                memberValue = null;
            }

            if (memberValue is KismetExpression childExpression)
            {
                nodes.Add(CreateExpressionNode(field.Name, childExpression, rawBytes));
                continue;
            }

            if (TryGetExpressionSequence(memberValue, out var expressionSequence))
            {
                var childNodes = new List<MutableOperandNode>(expressionSequence.Count);
                for (var i = 0; i < expressionSequence.Count; i++)
                    childNodes.Add(CreateExpressionNode($"[{i}]", expressionSequence[i], rawBytes));

                var (start, end, confidence) = MergeChildrenRange(childNodes);
                nodes.Add(new MutableOperandNode(
                    field.Name,
                    GetFriendlyTypeName(field.FieldType),
                    $"{expressionSequence.Count} item(s)",
                    start >= 0 && end > start ? FormatInstructionBytes(rawBytes, start, end - start) : "<no-bytes>",
                    start,
                    end,
                    confidence,
                    field.FieldType,
                    memberValue,
                    false,
                    childNodes));
                continue;
            }

            nodes.Add(new MutableOperandNode(
                field.Name,
                GetFriendlyTypeName(field.FieldType),
                FormatOperandValue(memberValue),
                "<no-bytes>",
                -1,
                -1,
                LowLevelOperandConfidence.Fallback,
                field.FieldType,
                memberValue,
                true));
        }

        AssignScalarRanges(nodes, payloadStart, payloadEnd, rawBytes);
        AppendUnmappedGapNodes(nodes, payloadStart, payloadEnd, rawBytes);
        return nodes;
    }

    private static MutableOperandNode CreateExpressionNode(string name, KismetExpression expression, byte[]? rawBytes)
    {
        var hasRange = TryGetCapturedRawRange(rawBytes, expression, out var start, out var length);
        var end = hasRange ? start + length : -1;
        var confidence = hasRange ? LowLevelOperandConfidence.Exact : LowLevelOperandConfidence.Fallback;
        var node = new MutableOperandNode(
            name,
            expression.Token.ToString(),
            $"{expression.Token} @0x{expression.StatementIndex:X4}",
            hasRange ? FormatInstructionBytes(rawBytes, start, length) : "<no-bytes>",
            hasRange ? start : -1,
            end,
            confidence,
            expression.GetType(),
            expression,
            false,
            kind: "NestedExpression");

        if (hasRange && end > start + 1)
        {
            var children = BuildExpressionMembers(expression, rawBytes, start + 1, end);
            foreach (var child in children)
                node.Children.Add(child);
        }

        return node;
    }

    private static IReadOnlyList<FieldInfo> GetExpressionFields(Type expressionType)
    {
        return ExpressionFieldCache.GetOrAdd(expressionType, static type =>
        {
            var chain = new Stack<Type>();
            var cursor = type;
            while (cursor is not null && cursor != typeof(KismetExpression))
            {
                chain.Push(cursor);
                cursor = cursor.BaseType;
            }

            var fields = new List<FieldInfo>();
            while (chain.Count > 0)
            {
                var current = chain.Pop();
                var declaredFields = current
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(f =>
                        !f.IsStatic &&
                        f.Name is not nameof(KismetExpression.StatementIndex) and not nameof(KismetExpression.RawStartIndex) and not nameof(KismetExpression.RawEndIndex))
                    .OrderBy(f => f.MetadataToken);
                fields.AddRange(declaredFields);
            }

            return fields;
        });
    }

    private static bool TryGetExpressionSequence(object? value, out IReadOnlyList<KismetExpression> sequence)
    {
        switch (value)
        {
            case KismetExpression[] array:
                sequence = array;
                return true;
            case IList<KismetExpression> list:
                sequence = list.ToList();
                return true;
            case IEnumerable<KismetExpression> enumerable:
                sequence = enumerable.ToList();
                return true;
            default:
                sequence = [];
                return false;
        }
    }

    private static void AssignScalarRanges(List<MutableOperandNode> nodes, int payloadStart, int payloadEnd, byte[]? rawBytes)
    {
        var scalarNodes = nodes.Where(x => x.IsScalarPlaceholder).ToList();
        if (scalarNodes.Count == 0 || payloadEnd <= payloadStart)
            return;

        var gaps = BuildGaps(payloadStart, payloadEnd, nodes);
        if (gaps.Count == 0)
            return;

        var gapIndex = 0;
        var gapOffset = 0;

        for (var i = 0; i < scalarNodes.Count; i++)
        {
            var scalar = scalarNodes[i];
            var remainingScalars = scalarNodes.Count - i;
            var remainingBytes = RemainingBytes(gaps, gapIndex, gapOffset);
            if (remainingBytes <= 0)
                break;

            var fixedSize = TryGetFixedSerializedSize(scalar.DeclaredType, scalar.SourceValue);
            var proposedSize = fixedSize ?? (remainingScalars == 1 ? remainingBytes : Math.Max(1, remainingBytes / remainingScalars));

            if (!TryTakeGapRange(gaps, ref gapIndex, ref gapOffset, proposedSize, out var start, out var length))
                continue;

            scalar.Start = start;
            scalar.End = start + length;
            scalar.Bytes = FormatInstructionBytes(rawBytes, start, length);
            scalar.Confidence = fixedSize.HasValue && fixedSize.Value == length
                ? LowLevelOperandConfidence.Derived
                : LowLevelOperandConfidence.Fallback;
        }
    }

    private static void AppendUnmappedGapNodes(List<MutableOperandNode> nodes, int payloadStart, int payloadEnd, byte[]? rawBytes)
    {
        if (payloadEnd <= payloadStart)
            return;

        var gaps = BuildGaps(payloadStart, payloadEnd, nodes);
        for (var i = 0; i < gaps.Count; i++)
        {
            var gap = gaps[i];
            if (gap.End <= gap.Start)
                continue;

            nodes.Add(CreateUnknownNode(rawBytes, $"unknown_segment_{i:D2}", gap.Start, gap.End, "Parser consumed bytes with no explicit member mapping"));
        }
    }

    private static List<(int Start, int End)> BuildGaps(int start, int end, IEnumerable<MutableOperandNode> nodes)
    {
        var occupied = nodes
            .Where(x => x.Start >= 0 && x.End > x.Start)
            .Select(x => (Start: x.Start, End: x.End))
            .OrderBy(x => x.Start)
            .ToList();
        var gaps = new List<(int Start, int End)>();
        var cursor = start;
        foreach (var range in occupied)
        {
            if (range.End <= cursor)
                continue;

            var clampedStart = Math.Max(start, range.Start);
            var clampedEnd = Math.Min(end, range.End);
            if (clampedStart > cursor)
                gaps.Add((cursor, clampedStart));

            cursor = Math.Max(cursor, clampedEnd);
            if (cursor >= end)
                break;
        }

        if (cursor < end)
            gaps.Add((cursor, end));
        return gaps;
    }

    private static int RemainingBytes(IReadOnlyList<(int Start, int End)> gaps, int gapIndex, int gapOffset)
    {
        var total = 0;
        for (var i = gapIndex; i < gaps.Count; i++)
        {
            var (start, end) = gaps[i];
            if (end <= start)
                continue;

            if (i == gapIndex)
            {
                var consumed = Math.Max(0, gapOffset);
                total += Math.Max(0, end - start - consumed);
            }
            else
            {
                total += end - start;
            }
        }

        return total;
    }

    private static bool TryTakeGapRange(
        IReadOnlyList<(int Start, int End)> gaps,
        ref int gapIndex,
        ref int gapOffset,
        int requestedLength,
        out int start,
        out int length)
    {
        start = -1;
        length = 0;
        while (gapIndex < gaps.Count)
        {
            var gap = gaps[gapIndex];
            var cursor = gap.Start + gapOffset;
            var available = gap.End - cursor;
            if (available <= 0)
            {
                gapIndex++;
                gapOffset = 0;
                continue;
            }

            length = Math.Min(Math.Max(1, requestedLength), available);
            start = cursor;
            gapOffset += length;
            if (gap.Start + gapOffset >= gap.End)
            {
                gapIndex++;
                gapOffset = 0;
            }

            return true;
        }

        return false;
    }

    private static int? TryGetFixedSerializedSize(Type declaredType, object? value)
    {
        if (declaredType.IsEnum)
            declaredType = Enum.GetUnderlyingType(declaredType);

        if (declaredType == typeof(byte) || declaredType == typeof(sbyte) || declaredType == typeof(bool))
            return 1;
        if (declaredType == typeof(short) || declaredType == typeof(ushort))
            return 2;
        if (declaredType == typeof(int) || declaredType == typeof(uint) || declaredType == typeof(float))
            return 4;
        if (declaredType == typeof(long) || declaredType == typeof(ulong) || declaredType == typeof(double))
            return 8;
        if (declaredType == typeof(FPackageIndex))
            return 4;
        if (declaredType == typeof(FName))
            return 8;
        if (declaredType == typeof(FKismetPropertyPointer))
        {
            if (value is FKismetPropertyPointer pointer && !pointer.bNew)
                return 4;
            return null;
        }

        return null;
    }

    private static (int Start, int End, LowLevelOperandConfidence Confidence) MergeChildrenRange(IEnumerable<MutableOperandNode> children)
    {
        var ranges = children.Where(x => x.Start >= 0 && x.End > x.Start).ToList();
        if (ranges.Count == 0)
            return (-1, -1, LowLevelOperandConfidence.Fallback);

        var start = ranges.Min(x => x.Start);
        var end = ranges.Max(x => x.End);
        var confidence = ranges.All(x => x.Confidence == LowLevelOperandConfidence.Exact)
            ? LowLevelOperandConfidence.Exact
            : LowLevelOperandConfidence.Derived;
        return (start, end, confidence);
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var genericTypeName = type.Name;
        var tickIndex = genericTypeName.IndexOf('`');
        if (tickIndex >= 0)
            genericTypeName = genericTypeName[..tickIndex];
        var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
        return $"{genericTypeName}<{genericArgs}>";
    }

    private static string FormatOperandValue(object? value)
    {
        if (value is null)
            return "null";

        if (value is string text)
            return $"\"{TruncateForDisplay(ToSingleLine(text), 140)}\"";

        if (value is FName name)
            return name.Text;

        if (value is FPackageIndex packageIndex)
            return packageIndex.ToString();

        if (value is FKismetPropertyPointer pointer)
            return pointer.ToString();

        if (value is Array array)
            return $"{array.Length} item(s)";

        return TruncateForDisplay(ToSingleLine(value.ToString() ?? string.Empty), 140);
    }

    private static LowLevelOperandNodeData ToOperandNodeData(MutableOperandNode source)
    {
        var children = source.Children.Select(ToOperandNodeData).ToList();
        return new LowLevelOperandNodeData(
            source.Name,
            source.Kind,
            source.Type,
            source.Value,
            source.Bytes,
            source.Start,
            source.End,
            source.Confidence,
            source.Reason,
            children);
    }

    private static MutableOperandNode ToMutableOperandNode(LowLevelOperandNodeData source)
    {
        var children = source.Children.Select(ToMutableOperandNode).ToList();
        return new MutableOperandNode(
            source.Name,
            source.Type,
            source.Value,
            source.Bytes,
            source.Start,
            source.End,
            source.Confidence,
            typeof(object),
            null,
            false,
            children,
            source.Kind,
            source.Reason);
    }

    private readonly record struct ScalarFieldInfo(string Name, Type FieldType, object? Value);

    private sealed class MutableOperandNode
    {
        public string Name { get; }
        public string Kind { get; }
        public string Type { get; }
        public string Value { get; }
        public string Bytes { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public LowLevelOperandConfidence Confidence { get; set; }
        public Type DeclaredType { get; }
        public object? SourceValue { get; }
        public bool IsScalarPlaceholder { get; }
        public string? Reason { get; }
        public List<MutableOperandNode> Children { get; }

        public MutableOperandNode(
            string name,
            string type,
            string value,
            string bytes,
            int start,
            int end,
            LowLevelOperandConfidence confidence,
            Type declaredType,
            object? sourceValue,
            bool isScalarPlaceholder,
            List<MutableOperandNode>? children = null,
            string kind = "Field",
            string? reason = null)
        {
            Name = name;
            Kind = kind;
            Type = type;
            Value = value;
            Bytes = bytes;
            Start = start;
            End = end;
            Confidence = confidence;
            DeclaredType = declaredType;
            SourceValue = sourceValue;
            IsScalarPlaceholder = isScalarPlaceholder;
            Reason = reason;
            Children = children ?? [];
        }
    }

    private static void AppendPropertyTagsSection(StringBuilder sb, IPackage package, int classExportIndex, UClass blueprint)
    {
        sb.AppendLine("Property Tags:");
        var tags = new List<(string Scope, FPropertyTag Tag, int ExportIndex)>();
        AddPropertyTags(tags, "Class", classExportIndex, blueprint.Properties);

        var classDefaultObject = blueprint.ClassDefaultObject.Load();
        if (classDefaultObject != null)
        {
            var cdoExportIndex = package.GetExportIndex(classDefaultObject.Name);
            AddPropertyTags(tags, "CDO", cdoExportIndex, classDefaultObject.Properties);
            AddPropertyTags(tags, "SparseCDO", cdoExportIndex, classDefaultObject.SerializedSparseClassData?.Properties);
        }

        if (tags.Count == 0)
        {
            sb.AppendLine("  // None");
            sb.AppendLine();
            return;
        }

        foreach (var (scope, tag, exportIndex) in tags)
        {
            var (type, value) = GetPropertyTagPreview(tag);
            var headerOffset = FormatAssetOffset(package, exportIndex, tag.HeaderStartAssetOffset, out var headerFallback);
            var valueStartOffset = FormatAssetOffset(package, exportIndex, tag.ValueStartAssetOffset, out var valueStartFallback);
            var valueEndOffset = FormatAssetOffset(package, exportIndex, tag.ValueEndAssetOffset, out var valueEndFallback);
            var fallbackComment = headerFallback || valueStartFallback || valueEndFallback ? " // parser-offset-fallback" : string.Empty;
            sb.AppendLine($"  [{headerOffset}..{valueEndOffset}] {scope}.{tag.Name.Text} : {type} @{valueStartOffset} = {value}{fallbackComment}");
        }

        sb.AppendLine();
    }

    private static string FormatAssetOffset(IPackage package, int exportIndex, long parserOffset, out bool fallbackUsed)
    {
        if (TryConvertParserOffsetToRaw(package, exportIndex, parserOffset, out var rawOffset))
        {
            fallbackUsed = false;
            return $"0x{rawOffset:X8}";
        }

        fallbackUsed = true;
        return $"0x{parserOffset:X8}";
    }

    private static void AddPropertyTags(ICollection<(string Scope, FPropertyTag Tag, int ExportIndex)> sink, string scope, int exportIndex, IEnumerable<FPropertyTag>? source)
    {
        if (source is null)
            return;

        foreach (var tag in source)
        {
            sink.Add((scope, tag, exportIndex));
        }
    }

    private static (string Type, string Value) GetPropertyTagPreview(FPropertyTag tag)
    {
        if (BlueprintDecompilerUtils.GetPropertyTagVariable(tag, out var variableType, out var variableValue))
        {
            var resolvedType = string.IsNullOrWhiteSpace(variableType) ? tag.PropertyType.Text : variableType;
            return (resolvedType, TruncateForDisplay(ToSingleLine(variableValue), 220));
        }

        var fallbackType = tag.PropertyType.IsNone ? "Unknown" : tag.PropertyType.Text;
        var fallbackValue = tag.Tag?.ToString() ?? "null";
        return (fallbackType, TruncateForDisplay(ToSingleLine(fallbackValue), 220));
    }

    private static void AppendExportHexDumpSection(StringBuilder sb, IPackage package, int exportIndex, byte[]? combinedPackageBytes, string? rawDataWarning)
    {
        sb.AppendLine("Export Hex Dump:");
        if (!string.IsNullOrWhiteSpace(rawDataWarning))
        {
            sb.AppendLine($"  // {rawDataWarning}");
        }

        if (combinedPackageBytes is null)
        {
            sb.AppendLine("  // Skipped because raw package bytes are unavailable.");
            sb.AppendLine();
            return;
        }

        if (!TryGetExportRange(package, exportIndex, out var range))
        {
            sb.AppendLine("  // Unable to resolve export serial range.");
            sb.AppendLine();
            return;
        }

        if (range.RawStart < 0 || range.Size <= 0)
        {
            sb.AppendLine("  // Export has no serial payload.");
            sb.AppendLine();
            return;
        }

        long endOffset;
        try
        {
            endOffset = checked(range.RawStart + range.Size);
        }
        catch (OverflowException)
        {
            sb.AppendLine("  // Export range overflowed 64-bit bounds.");
            sb.AppendLine();
            return;
        }

        if (endOffset > combinedPackageBytes.LongLength || range.RawStart >= combinedPackageBytes.LongLength)
        {
            sb.AppendLine($"  // Export range [0x{range.RawStart:X8}..0x{endOffset:X8}) is outside available data (0x{combinedPackageBytes.LongLength:X8}).");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"  // Range: [0x{range.RawStart:X8}..0x{endOffset:X8}) Size: 0x{range.Size:X}");
        if (range.ParserStart != range.RawStart)
        {
            try
            {
                var parserEndOffset = checked(range.ParserStart + range.Size);
                sb.AppendLine($"  // Parser range: [0x{range.ParserStart:X8}..0x{parserEndOffset:X8})");
            }
            catch (OverflowException)
            {
                sb.AppendLine($"  // Parser range starts at 0x{range.ParserStart:X8} (end overflowed).");
            }
        }
        AppendHexDump(sb, combinedPackageBytes, range.RawStart, range.Size);
        sb.AppendLine();
    }

    private static void AppendHexDump(StringBuilder sb, byte[] data, long startOffset, long size)
    {
        const int rowWidth = 16;
        for (long cursor = 0; cursor < size; cursor += rowWidth)
        {
            var rowOffset = startOffset + cursor;
            var remaining = size - cursor;
            var rowLength = (int) Math.Min(rowWidth, remaining);
            var absoluteIndex = (int) rowOffset;

            var hexPart = FormatHexGroup(data, absoluteIndex, rowLength);
            var asciiPart = FormatAsciiGroup(data, absoluteIndex, rowLength);
            sb.AppendLine($"  0x{rowOffset:X8}  {hexPart}  |{asciiPart}|");
        }
    }

    private static string FormatHexGroup(byte[] data, int offset, int length)
    {
        const int rowWidth = 16;
        var values = new string[rowWidth];
        for (var i = 0; i < rowWidth; i++)
        {
            values[i] = i < length ? data[offset + i].ToString("X2") : "  ";
        }

        return string.Join(" ", values);
    }

    private static string FormatAsciiGroup(byte[] data, int offset, int length)
    {
        const int rowWidth = 16;
        var chars = new char[rowWidth];
        for (var i = 0; i < rowWidth; i++)
        {
            if (i >= length)
            {
                chars[i] = ' ';
                continue;
            }

            var b = data[offset + i];
            chars[i] = b is >= 32 and <= 126 ? (char) b : '.';
        }

        return new string(chars);
    }

    private static bool TryBuildCombinedPackageBytes(IReadOnlyDictionary<string, byte[]> assets, out byte[]? combined, out string? warning)
    {
        combined = null;
        warning = null;

        var uassetPair = assets.FirstOrDefault(x => x.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
        if (uassetPair.Value is not { Length: > 0 } uassetBytes)
        {
            warning = "Could not find .uasset bytes.";
            return false;
        }

        var uexpPair = assets.FirstOrDefault(x => x.Key.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase));
        if (uexpPair.Value is not { Length: > 0 } uexpBytes)
        {
            combined = uassetBytes;
            return true;
        }

        combined = new byte[uassetBytes.Length + uexpBytes.Length];
        Buffer.BlockCopy(uassetBytes, 0, combined, 0, uassetBytes.Length);
        Buffer.BlockCopy(uexpBytes, 0, combined, uassetBytes.Length, uexpBytes.Length);
        return true;
    }

    private static string ToSingleLine(string value)
    {
        return string.Join(" ", value.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string TruncateForDisplay(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 3)] + "...";
    }

    private void SaveAndPlaySound(string fullPath, string ext, byte[] data, bool isBulk)
    {
        if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
        var savedAudioPath = Path.Combine(UserSettings.Default.AudioDirectory,
            UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/')).Replace('\\', '/') + $".{ext.ToLowerInvariant()}";

        if (isBulk)
        {
            Directory.CreateDirectory(savedAudioPath.SubstringBeforeLast('/'));
            using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(data);
            writer.Flush();
            return;
        }

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = Helper.GetWindow<AudioPlayer>("Audio Player", () => new AudioPlayer().Show());
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        var toSave = new Exporter(export, UserSettings.Default.ExportOptions);
        var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
        if (toSave.TryWriteToDir(toSaveDirectory, out var label, out var savedFilePath))
        {
            Log.Information("Successfully saved {FilePath}", savedFilePath);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(label, savedFilePath, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be saved", export.Name);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}'", Constants.WHITE, true));
        }
    }

    private readonly object _rawData = new ();
    public void ExportData(GameFile entry, bool updateUi = true)
    {
        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            Parallel.ForEach(assets, kvp =>
            {
                lock (_rawData)
                {
                    path = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }
            });

            Log.Information("{FileName} successfully exported", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported ", Constants.WHITE);
                    FLogger.Link(entry.Name, path, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }

    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }
}
