using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Compression;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.Utils;

namespace Main;

public static class Program
{
    private static readonly Regex VerseMangleRegex = new Regex(@"__verse_0x[a-fA-F0-9]{8}_", RegexOptions.Compiled);
    private static readonly Regex CallFuncRegex = new Regex(@"CallFunc_([A-Za-z0-9_]+)_ReturnValue", RegexOptions.Compiled);
    private static readonly Regex DynamicCastRegex = new Regex(@"K2Node_DynamicCast_([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex K2NodeRegex = new Regex(@"K2Node_([A-Za-z0-9_]+)", RegexOptions.Compiled);

    // UE4 package NameMap entries serialize an FString immediately after a
    // little-endian signed character count (including the trailing NUL).
    // Prefiltering the small .uasset/.umap header this way avoids loading the
    // often very large companion .uexp merely to discover that a name cannot
    // occur. Full CUE4Parse validation still runs for every raw candidate.
    private static bool ContainsSerializedPackageName(
        ReadOnlySpan<byte> bytes, string name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => c <= 0x7f))
            return false;

        var encoded = Encoding.ASCII.GetBytes(name);
        var serialized = new byte[sizeof(int) + encoded.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(
            serialized.AsSpan(0, sizeof(int)), encoded.Length + 1);
        encoded.CopyTo(serialized.AsSpan(sizeof(int)));
        return bytes.IndexOf(serialized) >= 0;
    }

    public static async Task Main(string[] args)
    {
        try
        {
#if DEBUG
            //Log.Logger = new LoggerConfiguration().WriteTo.Console(theme: AnsiConsoleTheme.Literate).CreateLogger();
#endif
            // Raw Lux battle assets (.khd/.mot/.mif/.khi/.dat) are ordinary
            // files in SC6's pak, not UObject packages.  Keep this narrow mode
            // separate from Blueprint decompilation so reverse-engineering
            // tools can inspect/list them without pretending they are assets.
            if (args.Length >= 3 && args[0] == "--list-raw")
            {
                var rawProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17);
                rawProvider.PostMount();
                Console.Error.WriteLine($"Mounted {rawProvider.Files.Count} files from {rawProvider.MountedVfs.Count} containers; {rawProvider.UnloadedVfs.Count} unloaded; {rawProvider.RequiredKeys.Count} keys required");
                foreach (var file in rawProvider.Files.Values
                             .Where(file => file.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{file.Path}\t{file.Size}");
                }
                return;
            }

            if (args.Length >= 4 && args[0] == "--extract-raw")
            {
                var rawProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17);
                rawProvider.PostMount();
                Console.Error.WriteLine($"Mounted {rawProvider.Files.Count} files from {rawProvider.MountedVfs.Count} containers; {rawProvider.UnloadedVfs.Count} unloaded; {rawProvider.RequiredKeys.Count} keys required");
                var outputRoot = Path.GetFullPath(args[3]);
                foreach (var file in rawProvider.Files.Values
                             .Where(file => file.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
                    var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relative));
                    if (!outputPath.StartsWith(outputRoot + Path.DirectorySeparatorChar,
                                               StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Refusing path outside output root: {file.Path}");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllBytes(outputPath, file.Read());
                    Console.WriteLine($"{file.Path}\t{file.Size}\t{outputPath}");
                }
                return;
            }

            // Export the cooked reference skeleton in a stable, compact form for
            // static Lux motion/KHit analysis.  The provider resolves each loose
            // .uasset together with its adjacent .uexp companion.
            if (args.Length >= 3 && args[0] == "--export-skeleton")
            {
                var skeletonProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17);
                skeletonProvider.PostMount();
                var requestedPath = args[2].Replace('\\', '/');
                var package = skeletonProvider.LoadPackage(requestedPath);
                var exports = package.GetExports().ToArray();
                var referenceSkeleton = exports.OfType<USkeleton>().SingleOrDefault()?.ReferenceSkeleton
                    ?? exports.OfType<USkeletalMesh>().SingleOrDefault()?.ReferenceSkeleton
                    ?? throw new InvalidDataException(
                        $"No USkeleton or USkeletalMesh reference skeleton in {requestedPath}");

                var result = new
                {
                    source = requestedPath,
                    bones = referenceSkeleton.FinalRefBoneInfo.Select((bone, index) =>
                    {
                        var pose = referenceSkeleton.FinalRefBonePose[index];
                        return new
                        {
                            index,
                            name = bone.Name.Text,
                            parent = bone.ParentIndex,
                            rotation = new[] { pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W },
                            translation = new[] { pose.Translation.X, pose.Translation.Y, pose.Translation.Z },
                            scale = new[] { pose.Scale3D.X, pose.Scale3D.Y, pose.Scale3D.Z },
                        };
                    }),
                };
                var json = JsonConvert.SerializeObject(result, Formatting.Indented) + Environment.NewLine;
                if (args.Length >= 4)
                {
                    var outputPath = Path.GetFullPath(args[3]);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                    Console.Error.WriteLine($"Skeleton JSON written to {outputPath}");
                }
                else
                {
                    Console.Write(json);
                }
                return;
            }

            // Export every UObject in one loose cooked package as CUE4Parse's
            // stable JSON projection.  Static battle-science tools use this
            // for RegularProfile values (BODY/UPPER/LOWER scales and related
            // setup) without launching the game or approximating UObject
            // property serialization themselves.
            if (args.Length >= 3 && (args[0] == "--export-package-json" || args[0] == "--export-script-json"))
            {
                var packageProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17);
                // Opt in to primary Kismet expressions when auditing the
                // pseudocode's branch targets and inherited timer callbacks.
                packageProvider.ReadScriptData = args[0] == "--export-script-json";
                packageProvider.PostMount();
                var requestedPath = args[2].Replace('\\', '/');
                var exports = packageProvider.LoadPackage(requestedPath).GetExports().ToArray();
                var json = JsonConvert.SerializeObject(exports, Formatting.Indented) + Environment.NewLine;
                if (args.Length >= 4)
                {
                    var outputPath = Path.GetFullPath(args[3]);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                    Console.Error.WriteLine($"Package JSON written to {outputPath}");
                }
                else
                {
                    Console.Write(json);
                }
                return;
            }

            // Compact projection of the RegularProfile fields consumed by
            // native collision-reference rescaling.  Keeping this separate
            // from the generic package dump gives static analyzers a small,
            // deterministic shipped-asset input.
            if (args.Length >= 3 && args[0] == "--export-regular-profile")
            {
                var profileProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17);
                profileProvider.PostMount();
                var requestedPath = args[2].Replace('\\', '/');
                var exports = profileProvider.LoadPackage(requestedPath).GetExports().ToArray();
                var exportJson = JArray.FromObject(exports);
                var properties = exportJson
                    .Select(token => token["Properties"])
                    .FirstOrDefault(token => token is not null)
                    ?? throw new InvalidDataException($"No UObject properties in {requestedPath}");
                var result = new JObject
                {
                    ["source"] = requestedPath,
                    ["character"] = properties["Character"]?.DeepClone(),
                    ["style"] = properties["Style"]?.DeepClone(),
                    // UE omits properties equal to class defaults.  An absent
                    // BodyScales array therefore means the native default
                    // unit scales, not an unknown parse failure.
                    ["bodyScales"] = properties["BodyScales"]?.DeepClone() ?? new JArray(),
                    ["parts"] = properties["Parts"]?.DeepClone() ?? new JArray(),
                };
                var json = result.ToString(Formatting.Indented) + Environment.NewLine;
                if (args.Length >= 4)
                {
                    var outputPath = Path.GetFullPath(args[3]);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                    Console.Error.WriteLine($"RegularProfile JSON written to {outputPath}");
                }
                else
                {
                    Console.Write(json);
                }
                return;
            }

            // Search cooked package name maps without materializing every asset.
            // This is useful when auditing whether a native Blueprint-callable
            // function is reachable from stock content: UE serializes referenced
            // function names in the package name map even when the bytecode lives
            // in the companion .uexp file.
            if (args.Length >= 3 && args[0] == "--find-package-name")
            {
                var packageSearch = Directory.EnumerateFiles(args[1], "*.pak", SearchOption.TopDirectoryOnly).Any()
                    ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                var searchProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17,
                    packageSearch);
                searchProvider.PostMount();
                var needle = args[2];
                var pathPrefix = args.Length >= 4 ? args[3] : string.Empty;
                var candidates = searchProvider.Files.Values
                    .Where(file =>
                        (file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                         file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrEmpty(pathPrefix) ||
                         file.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var matches = new ConcurrentBag<string>();
                var failures = 0;

                Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
                {
                    try
                    {
                        var package = searchProvider.LoadPackage(file.Path);
                        if (package.NameMap.Any(entry => string.Equals(entry.Name, needle, StringComparison.Ordinal)))
                            matches.Add(file.Path);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                });

                foreach (var match in matches.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine(match);
                Console.Error.WriteLine($"Scanned {candidates.Length} packages; {matches.Count} matches; {failures} parse failures");
                return;
            }

            // Multi-name form of the reachability audit above. Mounting and
            // parsing the complete stock SC6 package set dominates the cost,
            // so resolve all requested UFunction names in one pass instead of
            // remounting roughly 49,000 packages once per native thunk.
            if (args.Length >= 3 && args[0] == "--find-package-names")
            {
                var packageSearch = Directory.EnumerateFiles(args[1], "*.pak", SearchOption.TopDirectoryOnly).Any()
                    ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                var searchProvider = InitializeProvider(args[1], string.Empty, EGame.GAME_UE4_17,
                    packageSearch);
                searchProvider.PostMount();
                Console.Error.WriteLine($"Mounted {searchProvider.Files.Count} files from {searchProvider.MountedVfs.Count} containers; {searchProvider.UnloadedVfs.Count} unloaded; {searchProvider.RequiredKeys.Count} keys required");
                var needles = args.Skip(2)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (needles.Length == 0)
                    throw new ArgumentException("At least one package name is required");
                var needleSet = new HashSet<string>(needles, StringComparer.Ordinal);
                var candidates = searchProvider.Files.Values
                    .Where(file =>
                        file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                        file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var matches = new ConcurrentBag<(string Name, string Path)>();
                var rawCandidates = new ConcurrentBag<GameFile>();
                var rawFailures = 0;
                var failures = 0;

                Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
                {
                    try
                    {
                        var header = file.Read();
                        if (needles.Any(needle =>
                                ContainsSerializedPackageName(header, needle)))
                            rawCandidates.Add(file);
                    }
                    catch
                    {
                        Interlocked.Increment(ref rawFailures);
                    }
                });

                Parallel.ForEach(rawCandidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
                {
                    try
                    {
                        var package = searchProvider.LoadPackage(file.Path);
                        foreach (var entry in package.NameMap)
                            if (needleSet.Contains(entry.Name))
                                matches.Add((entry.Name, file.Path));
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                });

                foreach (var match in matches
                             .Distinct()
                             .OrderBy(value => value.Name, StringComparer.Ordinal)
                             .ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{match.Name}\t{match.Path}");
                Console.Error.WriteLine($"Scanned {candidates.Length} package headers for {needles.Length} names; {rawCandidates.Count} raw candidates; {matches.Count} matches; {rawFailures} header read failures; {failures} candidate parse failures");
                return;
            }

            var config = Utils.LoadConfig("config.json");

            string pakFolderPath = config.PakFolderPath;
            if (string.IsNullOrEmpty(pakFolderPath) || pakFolderPath.Length < 1)
            {
                Console.WriteLine("Please provide a pak folder path in the config.json file.");
                return;
            }

            string blueprintPath = config.BlueprintPath;
            if (string.IsNullOrEmpty(blueprintPath) || blueprintPath.Length < 1)
            {
                Console.WriteLine("No blueprint path specified in the config.json file. Processing all compatible blueprints.");
            }

            EGame version = config.Version;
            if (string.IsNullOrEmpty(version.ToString()) || version.ToString().Length < 1)
            {
                Console.WriteLine("Please provide a UE version in the config.json file.");
                return;
            }

            string usmapPath = config.UsmapPath;
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;

            var provider = InitializeProvider(pakFolderPath, usmapPath, version);
            provider.ReadScriptData = true;
            var hasPakContainers = Directory.EnumerateFiles(
                    pakFolderPath,
                    "*.pak",
                    SearchOption.TopDirectoryOnly)
                .Any();
            if (hasPakContainers)
            {
                Console.WriteLine("If the game is not fortnite and the game is encrypted, modify aes.json file.");
                await LoadAesKeysAsync(provider,"https://export-service-new.dillyapis.com/v1/aes"); // allow users to change the aes url?
            }
            else
            {
                provider.PostMount();
            }

            var files = new Dictionary<string, GameFile[]>();

            bool isFile = provider.Files.ContainsKey(blueprintPath);
            if (string.IsNullOrEmpty(blueprintPath))
            {
                files = provider.Files.Values
                    .Where(f => (f.Path.EndsWith(".uasset") || f.Path.EndsWith(".umap")) && !f.Path.Contains(".o.") && !f.Path.Contains("PPID_") && !f.Path.Contains("/VKTemplates") && !f.Path.Contains("/NaniteDisplacedMesh_"))
                    .GroupBy(f => f.Path.SubstringBeforeLast('/'))
                    .ToDictionary(g => g.Key, g => g.ToArray());
            }
            else if (isFile)
            {
                files = new Dictionary<string, GameFile[]>
                {
                    [blueprintPath] = new[] { provider.Files[blueprintPath] }
                };
            }
            else
            {
                files = provider.Files.Values
                    .Where(f => f.Path.StartsWith(blueprintPath + "/") &&
                                (f.Path.EndsWith(".uasset") || f.Path.EndsWith(".umap")) &&
                                !f.Path.Contains(".o.") &&
                                !f.Path.Contains("/PPID_") && // ignore these (fortnite, it slows the program by like 10x)
                                !f.Path.Contains("/VKTemplates") &&
                                !f.Path.Contains("/NaniteDisplacedMesh_"))
                    .GroupBy(f => f.Path.SubstringBeforeLast('/'))
                    .ToDictionary(g => g.Key, g => g.ToArray());
            }

            int index = -1;
            int totalGameFiles = files.Sum(kv => kv.Value.Length);
            Console.WriteLine($"Starting Decompilation of {totalGameFiles} game files.");

            // loop from https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse.Example/Exporter.cs#L104
            foreach (var (_, packages) in files)
            {
                Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, package =>
                {
                    try
                    {
                        if (!package.IsUePackage)
                            return;
                        int currentIndex = Interlocked.Increment(ref index);
                        string path = package.Path;

                        Console.WriteLine($"Processing {path} ({currentIndex}/{totalGameFiles})");

                        var pkg = provider.LoadPackage(path);

                        var cpp = new StringBuilder();
                        for (var i = 0; i < pkg?.ExportMapLength; i++)
                        {
                            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
                            if (pointer?.Object?.Value is not UClass blueprint)
                                continue;

                            if (cpp.Length > 0)
                                cpp.Append("\n\n");

                            cpp.Append(blueprint.DecompileBlueprintToPseudo(pkg.Mappings));
                        }

                        if (cpp.Length > 0)
                        {
                            string blueprintDirRel = Path.GetDirectoryName(path)!;
                            string blueprintDirOutput = Path.Combine(exeDirectory, blueprintDirRel);
                            Directory.CreateDirectory(blueprintDirOutput);

                            string outputFile = Path.ChangeExtension(package.Name, ".cpp");
                            string outputFilePath = Path.Combine(blueprintDirOutput, outputFile);

                            string cppclean = cpp.ToString();
                            if (path.Contains("_Verse.uasset"))
                            {
                                cppclean = VerseMangleRegex.Replace(cppclean, "");
                            }
                            cppclean = CallFuncRegex.Replace(cppclean, "$1");
                            cppclean = DynamicCastRegex.Replace(cppclean, "$1");
                            cppclean = K2NodeRegex.Replace(cppclean, "$1");

                            using (var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: false))
                            using (var writer = new StreamWriter(fs, Encoding.UTF8, bufferSize: 8192))
                            {
                                writer.Write(cppclean);
                                writer.Flush();
                            }
                            Console.WriteLine($"Output written to: {outputFilePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error Processing: {package.Path} {ex.Message}\n{ex.StackTrace}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}\n{ex.StackTrace}");
        }
    }

    static DefaultFileProvider InitializeProvider(string pakFolderPath, string usmapPath, EGame version,
        SearchOption searchOption = SearchOption.AllDirectories)
    {
        OodleHelper.Initialize(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT));
        ZlibHelper.Initialize(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ZlibHelper.DLL_NAME));

        var provider = new DefaultFileProvider(pakFolderPath, searchOption, true, new VersionContainer(version));

        if (!string.IsNullOrEmpty(usmapPath) && usmapPath.Length > 0)
        {
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        }

        provider.Initialize();

        return provider;
    }

    static async Task LoadAesKeysAsync(DefaultFileProvider provider, string aesUrl)
    {
        string cacheFilePath = "aes.json";

        if (File.Exists(cacheFilePath))
        {
            string cachedAesJson = await File.ReadAllTextAsync(cacheFilePath);
            LoadAesKeysFromJson(provider, cachedAesJson);
        }
        else
        {
            using var httpClient = new HttpClient();
            string aesJson = await httpClient.GetStringAsync(aesUrl);
            await File.WriteAllTextAsync(cacheFilePath, aesJson);
            LoadAesKeysFromJson(provider, aesJson);
        }

        provider.PostMount();
        //provider.LoadLocalization();
    }

    private static void LoadAesKeysFromJson(DefaultFileProvider provider, string aesJson)
    {
        var aesData = JObject.Parse(aesJson);
        string mainKey = aesData["mainKey"]?.ToString() ?? string.Empty;
        provider.SubmitKey(new FGuid(), new FAesKey(mainKey));

        foreach (var key in aesData["dynamicKeys"]?.ToObject<JArray>() ?? new JArray())
        {
            var guid = key["guid"]?.ToString();
            var aesKey = key["key"]?.ToString();
            if (!string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(aesKey))
            {
                provider.SubmitKey(new FGuid(guid), new FAesKey(aesKey));
            }
        }
    }
}
